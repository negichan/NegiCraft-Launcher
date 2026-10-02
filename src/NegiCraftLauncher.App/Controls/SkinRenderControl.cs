using System;
using System.IO;
using System.Numerics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using NegiCraftLauncher.App.Controls.OpenGL;
using NegiCraftLauncher.App.Services;
using MinecraftSkinRender;
using MinecraftSkinRender.OpenGL;
using SkiaSharp;

namespace NegiCraftLauncher.App.Controls;

public class SkinRenderControl : OpenGlControlBase, ICustomHitTest
{
    private SkinRenderOpenGL? _skin;
    private DateTime _lastTime;
    private SKBitmap? _skinBitmap;
    private DispatcherTimer? _idleTimer;
    private double _idleClock;
    private bool _sneak;
    private double _sneakK;

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

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
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
            if (AssetLoader.Exists(new Uri("avares://NegiCraftLauncher.App/Assets/skin_miku_mew.png")))
            {
                using var stream = AssetLoader.Open(new Uri("avares://NegiCraftLauncher.App/Assets/skin_miku_mew.png"));
                _skinBitmap = SKBitmap.Decode(stream);
                return;
            }

            string skinPath = Path.Combine(AppContext.BaseDirectory, "Assets", "skin_miku_mew.png");
            if (!File.Exists(skinPath))
            {
                skinPath = Path.Combine(Directory.GetCurrentDirectory(), "src", "NegiCraftLauncher.App", "Assets", "skin_miku_mew.png");
            }
            if (File.Exists(skinPath))
            {
                using var stream = File.OpenRead(skinPath);
                _skinBitmap = SKBitmap.Decode(stream);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinRenderControl] Failed to load skin: {ex.Message}");
        }
    }

    public void SetSkin(byte[] pngBytes)
    {
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
                    _skin.SetSkinTex(_skinBitmap);
                    var type = SkinTypeChecker.GetTextType(_skinBitmap);
                    _skin.SkinType = type != SkinType.Unkonw ? type : SkinType.NewSlim;
                    RequestNextFrameRendering();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinRenderControl] SetSkin error: {ex.Message}");
        }
    }

    public void LoadFromUsername(string username)
    {
        _ = Task.Run(async () =>
        {
            var bytes = await SkinService.FetchSkinBytesOnlineAsync(username);
            if (bytes != null && bytes.Length > 0)
            {
                Dispatcher.UIThread.Post(() => SetSkin(bytes));
            }
        });
    }

    public float CurrentYawDeg { get; private set; } = 24f;
    private float _lastTargetPitchDeg = 0f;
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

            // MinecraftSkinRender: Vector3.X = Roll, Vector3.Y = Pitch, Vector3.Z = Yaw
            // In GLSL vertex shader:
            // Positive Y in HeadRotate tilts head DOWN (when dy > 0, looking down)
            // Positive Z in HeadRotate turns face to the RIGHT (when dx > 0, looking right towards mouse)
            float radPitch = headPitch * 2.0f * (float)Math.PI;
            float radYaw = headYaw * 2.0f * (float)Math.PI;
            _skin.HeadRotate = new Vector3(0, radPitch, radYaw);
        }
    }

    private void UpdatePose()
    {
        if (_skin == null) return;

        // skinview3d ramps the crouch with |sin(pr·π/2)|; the same curve plays here over
        // SneakTransition seconds in both directions.
        float e = (float)Math.Sin(_sneakK * Math.PI / 2);
        float px = e / 8f;

        _skin.BodyPos = new Vector3(0, CrouchBodyY * px, CrouchBodyZ * px);
        _skin.BodyRotate = new Vector3(0, CrouchBodyLean * e * RadToRotateInput, 0);
        _skin.HeadPos = new Vector3(0, CrouchHeadY * px, 0);
        _skin.LeftArmPos = new Vector3(0, CrouchArmY * px, CrouchArmZ * px);
        _skin.RightArmPos = _skin.LeftArmPos;
        _skin.LeftLegPos = new Vector3(0, 0, CrouchLegZ * px);
        _skin.RightLegPos = _skin.LeftLegPos;

        // Idle breathing (skinview3d IdleAnimation): leftArm.rotation.z = 0.02π + 0.03cos(2t),
        // mirrored onto the right arm so both arms sway outward together. The crouch adds its
        // own outward splay and leans both arms back so they keep hanging, not swinging.
        float idle = (float)(0.02 * Math.PI + 0.03 * Math.Cos(2 * _idleClock)) * RadToRotateInput;
        float splay = idle + CrouchArmSplay * e * RadToRotateInput;
        float armLean = CrouchArmLean * e * RadToRotateInput;
        _skin.LeftArmRotate = new Vector3(splay, armLean, 0);
        _skin.RightArmRotate = new Vector3(-splay, armLean, 0);
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
                _skin.SetSkinTex(_skinBitmap);
                var type = SkinTypeChecker.GetTextType(_skinBitmap);
                _skin.SkinType = type != SkinType.Unkonw ? type : SkinType.NewSlim;
            }
            else
            {
                _skin.SkinType = SkinType.NewSlim;
            }

            _skin.OpenGlInit();
            _skin.ResetPos();
            // Position character up by 0.3 units so head sits at Y ≈ 36px and feet at Y ≈ 209px
            _skin.Pos(0, 0.3f);
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

    protected override unsafe void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_skin == null) return;

        var topLevel = TopLevel.GetTopLevel(this);
        double scaling = topLevel?.RenderScaling ?? 1.0;
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

        _skin.Tick(dt);
        UpdatePose();
        _skin.OpenGlRender(fb);
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
