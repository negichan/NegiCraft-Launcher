using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using NegiCraftLauncher.Skin.Controls.OpenGL;
using NegiCraftLauncher.Skin.Services;
using MinecraftSkinRender;
using MinecraftSkinRender.OpenGL;
using SkiaSharp;

namespace NegiCraftLauncher.Skin.Controls;

public class SkinRenderControl : OpenGlControlBase, ICustomHitTest
{
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private unsafe delegate void GlReadPixelsDelegate(int x, int y, int width, int height, int format, int type, void* data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GlBindFramebufferDelegate(int target, int framebuffer);

    private GlReadPixelsDelegate? _glReadPixels;
    private GlBindFramebufferDelegate? _glBindFramebuffer;
    private WriteableBitmap? _lastFrameSnapshot;
    private readonly object _snapshotLock = new();

    private SkinRenderOpenGL? _skin;
    private DateTime _lastTime;
    private SKBitmap? _skinBitmap;
    private DispatcherTimer? _idleTimer;
    private double _idleClock;
    private bool _sneak;
    private double _sneakK;
    private bool _isDangling;
    private double _dangleK;
    private const double DangleTransition = 0.15;
    private bool _isWalking;
    private double _walkK;
    private double _walkClock;
    private bool _isJumping;
    private double _jumpK;
    private double _attackT = 1.0;
    private bool _attackParked;

    /// <summary>
    /// Starts one arm swing, restarting it if one is already playing. Passing a progress parks the
    /// swing there instead: a 0.3s animation is over before the debug bridge can round-trip, so
    /// photographing it means freezing it mid-swing.
    /// </summary>
    public void TriggerAttack(double? parkAt = null)
    {
        _attackT = parkAt ?? 0.0;
        _attackParked = parkAt.HasValue;
        RequestNextFrameRendering();
    }

    public bool IsDangling
    {
        get => _isDangling;
        set
        {
            if (_isDangling != value)
            {
                _isDangling = value;
                RequestNextFrameRendering();
            }
        }
    }

    public bool IsWalking
    {
        get => _isWalking;
        set
        {
            if (_isWalking != value)
            {
                _isWalking = value;
                RequestNextFrameRendering();
            }
        }
    }

    public bool IsJumping
    {
        get => _isJumping;
        set
        {
            if (_isJumping != value)
            {
                _isJumping = value;
                RequestNextFrameRendering();
            }
        }
    }

    private bool _isSprinting;
    public bool IsSprinting
    {
        get => _isSprinting;
        set
        {
            if (_isSprinting != value)
            {
                _isSprinting = value;
                RequestNextFrameRendering();
            }
        }
    }

    private volatile bool _snapshotRequested;

    public SkinRenderControl()
    {
        ClipToBounds = false;
        LoadInitialSkin();
    }

    public void SetSneak(bool on)
    {
        _sneak = on;
        RequestNextFrameRendering();
    }

    private double _renderScaling = 1.0;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            _renderScaling = top.RenderScaling;
        }
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _idleTimer.Tick += (_, _) => RequestNextFrameRendering();
        _idleTimer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _idleTimer?.Stop();
        _idleTimer = null;
    }

    public bool HitTest(Point point)
    {
        return Bounds.Contains(point);
    }

    private void LoadInitialSkin()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://NegiCraftLauncher.Skin/Assets/skin_alex.png"));
            _skinBitmap = SKBitmap.Decode(stream);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinRenderControl] Failed to load skin: {ex.Message}");
        }
    }

    private SkinType? _lastExplicitType;

    /// <summary>
    /// Picks the model from the texture we just loaded. Legacy 64x32 skins only have a head
    /// overlay defined upstream — its limb overlay VAOs are never populated but still drawn, so
    /// leaving the second layer on paints garbage triangles where the arms and legs are.
    /// </summary>
    private void ApplySkinType(SkinType? explicitType = null)
    {
        if (_skin is null || _skinBitmap is null) return;

        SkinType type;
        if (explicitType.HasValue && explicitType.Value != SkinType.Unkonw)
        {
            type = explicitType.Value;
        }
        else
        {
            type = SkinTypeChecker.GetTextType(_skinBitmap);
        }

        _skin.SkinType = type != SkinType.Unkonw ? type : SkinType.NewSlim;
        _skin.EnableTop = type != SkinType.Old;
    }

    public void SetSkin(byte[] pngBytes, SkinType? explicitType = null)
    {
        _lastExplicitType = explicitType;
        try
        {
            using var ms = new MemoryStream(pngBytes);
            var bmp = SKBitmap.Decode(ms);
            if (bmp != null)
            {
                _skinBitmap?.Dispose();
                _skinBitmap = bmp;
                if (_skin != null)
                {
                    _skin.SetSkinTex(_skinBitmap, explicitType);
                    ApplySkinType(explicitType);
                    RequestNextFrameRendering();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinRenderControl] SetSkin error: {ex.Message}");
        }
    }

    public string? CurrentLoadedUser { get; private set; }

    public void LoadFromUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;
        CurrentLoadedUser = username;

        _ = Task.Run(async () =>
        {
            try
            {
                // Use cached or fetched skin, or fall back to bundled default for model
                var data = await SkinService.GetOrFetchSkinDataAsync(username);
                byte[] bytes;
                bool isSlim;
                if (data != null && data.Bytes.Length > 0)
                {
                    bytes = data.Bytes;
                    isSlim = data.IsSlim;
                }
                else
                {
                    isSlim = SkinService.IsSlimForPlayerName(username);
                    bytes = SkinService.DefaultSkinBytes(isSlim);
                }

                if (bytes.Length > 0)
                {
                    // Let the texture decide its own format: a 64x32 legacy skin has to render as
                    // SkinType.Old, not as the 1.8 model the slim flag alone would imply.
                    var skinType = SkinService.ResolveSkinType(bytes, isSlim);
                    Dispatcher.UIThread.Post(() => SetSkin(bytes, skinType));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkinRenderControl] LoadFromUsername({username}) error: {ex}");
            }
        });
    }

    public float CurrentYawDeg { get; private set; } = 24f;

    /// <summary>The model the renderer is actually using right now (wide vs slim), for diagnostics.</summary>
    public SkinType? LiveSkinType => _skin?.SkinType;

    /// <summary>Whether the second (jacket/hat) layer is being drawn, for diagnostics.</summary>
    public bool? LiveTopLayer => _skin?.EnableTop;    private float _lastTargetPitchDeg = 0f;
    private float _lastTargetYawDeg = 0f;

    // skinview3d's CrouchAnimation, in skin pixels; 8 px = 1 model unit in the renderer.
    // Body z is 1.3256181 - 3.4500310377 and the arm z is 3.618325234674 - 3.4500310377 there.
    private const float CrouchBodyLean = 0.4537860552f;
    private const float CrouchBodyY = -2.103677462f;
    private const float CrouchBodyZ = -2.1244129f;
    private const float CrouchHeadY = -3.618325234674f;
    private const float CrouchArmLean = 0.410367746202f;
    private const float CrouchArmSplay = 0.1f;
    private const float CrouchArmY = -2.53943318f;
    private const float CrouchArmZ = 0.1682942f;
    private const float CrouchLegZ = -3.4500310377f;
    private const double SneakTransition = 0.12;

    // Bare-hand swing in Minecraft lasts 6 game ticks.
    private const double AttackDuration = 0.3;

    // The renderer's rotation inputs are divided by 360 before being used as radians.
    private const float RadToRotateInput = 360f;

    public void RotateModel(float deltaYawDeg)
    {
        CurrentYawDeg += deltaYawDeg;
        while (CurrentYawDeg > 180f) CurrentYawDeg -= 360f;
        while (CurrentYawDeg < -180f) CurrentYawDeg += 360f;

        // The renderer folds rotation deltas into its model matrix on Tick, in 1/360 radian.
        _skin?.Rot(0, deltaYawDeg * 2f * (float)Math.PI);
        UpdateHeadRotate();
        RequestNextFrameRendering();
    }

    public void SetHeadLookAt(float targetPitchDeg, float targetYawDeg)
    {
        _lastTargetPitchDeg = targetPitchDeg;
        _lastTargetYawDeg = targetYawDeg;
        UpdateHeadRotate();
        RequestNextFrameRendering();
    }

    private void UpdateHeadRotate()
    {
        if (_skin != null)
        {
            // Relative neck yaw: headYaw = targetYawDeg - CurrentYawDeg
            float diffYaw = _lastTargetYawDeg - CurrentYawDeg;
            while (diffYaw > 180f) diffYaw -= 360f;
            while (diffYaw < -180f) diffYaw += 360f;

            // Clamp head rotation to realistic neck limits
            float headYaw = Math.Clamp(diffYaw, -55f, 55f);
            float headPitch = Math.Clamp(_lastTargetPitchDeg, -25f, 25f);
            float headRoll = 0f;

            // When dangling, play a vivid protesting head-shake animation:
            // Rapid left-right shaking like a rattle drum (~16 rad/s, +/- 28 deg),
            // accompanied by looking up towards the cursor and a slight struggle roll
            float dk = (float)_dangleK;
            if (dk > 0)
            {
                float shakeYaw = (float)(Math.Sin(_idleClock * 16.0) * 28.0);
                float shakePitch = -18f + (float)(Math.Sin(_idleClock * 8.0) * 4.0);
                float shakeRoll = (float)(Math.Sin(_idleClock * 16.0 + Math.PI / 4) * 6.0);

                headYaw = (1 - dk) * headYaw + dk * shakeYaw;
                headPitch = (1 - dk) * headPitch + dk * shakePitch;
                headRoll = dk * shakeRoll;
            }

            float radPitch = headPitch * 2.0f * (float)Math.PI;
            float radYaw = headYaw * 2.0f * (float)Math.PI;
            float radRoll = headRoll * 2.0f * (float)Math.PI;
            _skin.HeadRotate = new Vector3(radRoll, radPitch, radYaw);
        }
    }

    private void UpdatePose()
    {
        if (_skin == null) return;

        // skinview3d ramps the crouch with |sin(pr·π/2)|; the same curve plays here over
        // SneakTransition seconds in both directions.
        float e = (float)Math.Sin(_sneakK * Math.PI / 2);
        float px = e / 8f;

        float dk = (float)_dangleK;
        float wk = (float)_walkK * (1.0f - dk);
        float jk = (float)_jumpK * (1.0f - dk);

        // Attack animation: authentic 1:1 vanilla Minecraft humanoid attack mechanics
        float bodyTurn = 0f;
        float attackPitch = 0f;
        float attackYaw = 0f;
        float attackRoll = 0f;
        float attackBodyYaw = 0f;

        if (_attackT < 1.0)
        {
            float p = (float)_attackT;

            // 1. Torso twist: sin(sqrt(p) * 2pi) * 0.18 rad
            // Starts at 0, turns right to wind up, then twists left into punch, smoothly returns to 0 at p=1
            bodyTurn = -(float)Math.Sin(Math.Sqrt(p) * Math.PI * 2.0) * 0.18f;
            attackBodyYaw = bodyTurn * RadToRotateInput * (1 - dk);

            // 2. Minecraft vanilla quartic ease-out curve for forward slash: f = 1 - (1 - p)^4
            float f = 1.0f - p;
            f = 1.0f - f * f * f * f;
            float f1 = (float)Math.Sin(f * Math.PI);
            float headPitchRad = _lastTargetPitchDeg * ((float)Math.PI / 180f);
            float f2 = (float)Math.Sin(p * Math.PI) * -(headPitchRad - 0.7f) * 0.75f;

            // 3. Pitch: rapid forward upward rise followed by downward chop (negative is forward/up in our coords)
            attackPitch = -(f1 * 1.25f + f2) * RadToRotateInput * (1 - dk);

            // 4. Yaw: right arm follows torso twist and swings inward across chest towards center
            attackYaw = bodyTurn * 1.8f * RadToRotateInput * (1 - dk);

            // 5. Roll: right arm rolls inward across chest (vanilla zRot = -0.4 * sin(p * pi); positive is inward in our coords)
            attackRoll = (float)Math.Sin(p * Math.PI) * 0.40f * RadToRotateInput * (1 - dk);
        }

        // Arm shoulder translation to naturally match the torso twist
        float shoulderOffsetZ = (float)(0.75 * Math.Sin(bodyTurn)) * (1 - dk);

        _skin.BodyPos = new Vector3(0, CrouchBodyY * px, CrouchBodyZ * px);
        _skin.HeadPos = new Vector3(0, CrouchHeadY * px, 0);
        _skin.LeftArmPos = new Vector3(0, CrouchArmY * px, CrouchArmZ * px - shoulderOffsetZ);
        _skin.RightArmPos = new Vector3(0, CrouchArmY * px, CrouchArmZ * px + shoulderOffsetZ);
        _skin.LeftLegPos = new Vector3(0, 0, CrouchLegZ * px);
        _skin.RightLegPos = _skin.LeftLegPos;

        // Idle breathing (skinview3d IdleAnimation): leftArm.rotation.z = 0.02π + 0.03cos(2t),
        // mirrored onto the right arm so both arms sway outward together. The crouch adds its
        // own outward splay and leans both arms back so they keep hanging, not swinging.
        float idle = (float)(0.02 * Math.PI + 0.03 * Math.Cos(2 * _idleClock)) * RadToRotateInput;
        float splay = idle + CrouchArmSplay * e * RadToRotateInput;
        float armLean = CrouchArmLean * e * RadToRotateInput;

        // Walking & sprinting limb swing (airborne suppresses ground walk swings):
        float sprintBodyLean = _isSprinting ? (0.07f * RadToRotateInput * wk * (1.0f - jk)) : 0f;
        float sprintFactor = _isSprinting ? 1.25f : 1.0f;
        float airLimbFactor = 1.0f - jk * 0.85f;
        float legSwing = (float)(Math.Sin(_walkClock) * 0.62 * RadToRotateInput) * wk * sprintFactor * airLimbFactor;
        float armSwing = (float)(Math.Sin(_walkClock) * 0.52 * RadToRotateInput) * wk * sprintFactor * airLimbFactor;
        float jumpLegSwing = 0.22f * RadToRotateInput * jk;

        // Set absolute position: slightly raise the character when picked up, plus walking step bounce (ground only)
        float walkBob = (float)(Math.Abs(Math.Sin(_walkClock)) * 0.025) * wk * (1.0f - jk);
        _skin.SetPos(0, 0.3f + 0.12f * dk + walkBob);

        // Arms reach high up overhead to grab the cursor with fluttering struggle
        float flutter = (float)(Math.Sin(_idleClock * 16.0) * 0.08 * RadToRotateInput);
        float dangleArmPitch = (-2.75f * RadToRotateInput) + flutter;
        float dangleArmLX = (-0.15f * RadToRotateInput) + flutter * 0.2f;
        float dangleArmRX = (0.15f * RadToRotateInput) - flutter * 0.2f;

        float normalArmLY = armLean + armSwing;
        float normalArmRY = armLean - armSwing;

        float finalArmLX = (1 - dk) * splay + dk * dangleArmLX;
        float finalArmRX = (1 - dk) * (-splay) + dk * dangleArmRX;
        float finalArmLY = (1 - dk) * normalArmLY + dk * dangleArmPitch;
        float finalArmRY = (1 - dk) * normalArmRY + dk * dangleArmPitch;

        float leftArmYaw = bodyTurn * 0.8f * RadToRotateInput * (1 - dk);

        _skin.LeftArmRotate = new Vector3(finalArmLX, finalArmLY, leftArmYaw);
        _skin.RightArmRotate = new Vector3(finalArmRX + attackRoll, finalArmRY + attackPitch, attackYaw);

        // Legs: walking swing + jump spread + dangling kicks
        float kick = (float)(Math.Sin(_idleClock * 13.5) * 0.65 * RadToRotateInput);
        float legSplay = 0.12f * RadToRotateInput;

        float leftLegY = (1 - dk) * (legSwing * (1 - jk * 0.5f) + jumpLegSwing) + kick * dk;
        float rightLegY = (1 - dk) * (-legSwing * (1 - jk * 0.5f) - jumpLegSwing) - kick * dk;

        _skin.LeftLegRotate = new Vector3(legSplay * dk, leftLegY, 0);
        _skin.RightLegRotate = new Vector3(-legSplay * dk, rightLegY, 0);

        // Body swings slightly from the kicking momentum, walking swagger, and sprint forward lean
        float bodySwing = (float)(Math.Sin(_idleClock * 13.5) * 0.05 * RadToRotateInput);
        float walkBodyTilt = (float)(Math.Sin(_walkClock) * 0.03 * RadToRotateInput) * wk * (1.0f - jk);
        _skin.BodyRotate = new Vector3(0, (CrouchBodyLean * e - 0.06f * dk + sprintBodyLean) * RadToRotateInput, (bodySwing * dk) + walkBodyTilt + attackBodyYaw);
    }

    protected override unsafe void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            var api = new AvaloniaApi(gl);
            _skin = new SkinRenderOpenGL(api)
            {
                IsGLES = GlVersion.Type == GlProfileType.OpenGLES,
                BackColor = new Vector4(0, 0, 0, 0), // Fully transparent background
                EnableTop = true, // Second skin layer (jacket, hat, sleeves)
                Animation = false // Disabled walking animation per user request
            };

            if (_skinBitmap != null)
            {
                _skin.SetSkinTex(_skinBitmap, _lastExplicitType);
                ApplySkinType(_lastExplicitType);
            }
            else
            {
                _skin.SkinType = SkinType.NewSlim;
            }

            _skin.OpenGlInit();
            _skin.ResetPos();
            // Position character up by 0.3 units so head sits at Y ≈ 36px and feet at Y ≈ 209px
            _skin.SetPos(0, 0.3f);
            _skin.Rot(0, CurrentYawDeg * 2f * (float)Math.PI);
            UpdateHeadRotate();
            UpdatePose();
            RequestNextFrameRendering();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinRenderControl] OnOpenGlInit error: {ex.Message}");
        }
    }

    public int RenderCount { get; private set; }
    public string RenderStats => $"renders={RenderCount} w={Bounds.Width:F1} h={Bounds.Height:F1}";

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        RenderCount++;
        if (_skin == null) return;

        double scaling = _renderScaling;
        int w = (int)Math.Round(Bounds.Width * scaling);
        int h = (int)Math.Round(Bounds.Height * scaling);

        if (w <= 0 || h <= 0) return;

        _skin.Width = w;
        _skin.Height = h;

        var now = DateTime.Now;
        double dt = (_lastTime == default) ? 0.016 : (now - _lastTime).TotalSeconds;
        _lastTime = now;

        _idleClock += dt;
        _sneakK = Math.Clamp(_sneakK + ((_sneak ? 1 : -1) * dt / SneakTransition), 0, 1);
        _dangleK = Math.Clamp(_dangleK + ((_isDangling ? 1 : -1) * dt / DangleTransition), 0, 1);
        _walkK = Math.Clamp(_walkK + ((_isWalking ? 1 : -1) * dt / 0.08), 0, 1);
        if (_walkK > 0)
        {
            double walkFreq = _isSprinting ? 16.5 : (12.0 * (1.0 - 0.45 * _sneakK));
            double groundProgress = 1.0 - 0.85 * _jumpK;
            _walkClock += dt * walkFreq * groundProgress;
        }
        _jumpK = Math.Clamp(_jumpK + ((_isJumping ? 1 : -1) * dt / 0.10), 0, 1);
        if (_attackT < 1.0 && !_attackParked) _attackT = Math.Min(1.0, _attackT + dt / AttackDuration);

        _skin.Tick(dt);
        UpdateHeadRotate();
        UpdatePose();
        _skin.OpenGlRender(fb);

        if (_snapshotRequested)
        {
            CaptureFrameSnapshot(gl, fb, w, h);
            _snapshotRequested = false;
        }

        if (_dangleK > 0 || _isDangling || (_sneakK > 0 && _sneakK < 1) || _walkK > 0 || _isWalking || _jumpK > 0 || _isJumping || (_attackT < 1.0 && !_attackParked))
        {
            RequestNextFrameRendering();
        }
    }

    private unsafe void CaptureFrameSnapshot(GlInterface gl, int fb, int w, int h)
    {
        try
        {
            if (_glReadPixels == null)
            {
                var proc = gl.GetProcAddress("glReadPixels");
                if (proc != IntPtr.Zero)
                {
                    _glReadPixels = Marshal.GetDelegateForFunctionPointer<GlReadPixelsDelegate>(proc);
                }
            }
            if (_glBindFramebuffer == null)
            {
                var proc = gl.GetProcAddress("glBindFramebuffer");
                if (proc != IntPtr.Zero)
                {
                    _glBindFramebuffer = Marshal.GetDelegateForFunctionPointer<GlBindFramebufferDelegate>(proc);
                }
            }

            if (_glReadPixels != null && w > 0 && h > 0)
            {
                _glBindFramebuffer?.Invoke(0x8D40 /* GL_FRAMEBUFFER */, fb);

                var bmp = new WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Premul);
                using (var fbLock = bmp.Lock())
                {
                    var tempBuffer = new byte[w * h * 4];
                    fixed (byte* p = tempBuffer)
                    {
                        _glReadPixels(0, 0, w, h, 0x1908 /* GL_RGBA */, 0x1401 /* GL_UNSIGNED_BYTE */, p);
                    }
                    byte* dst = (byte*)fbLock.Address;
                    int stride = fbLock.RowBytes;
                    fixed (byte* src = tempBuffer)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            Buffer.MemoryCopy(src + (h - 1 - y) * w * 4, dst + y * stride, stride, w * 4);
                        }
                    }
                }

                lock (_snapshotLock)
                {
                    _lastFrameSnapshot?.Dispose();
                    _lastFrameSnapshot = bmp;
                }
            }
        }
        catch { }
    }


    public void SaveSnapshot(string filePath)
    {
        _snapshotRequested = true;
        RequestNextFrameRendering();
        lock (_snapshotLock)
        {
            _lastFrameSnapshot?.Save(filePath, new PngBitmapEncoderOptions());
        }
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        try
        {
            _skin?.OpenGlDeinit();
            _skin = null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinRenderControl] OnOpenGlDeinit error: {ex.Message}");
        }
    }
}
