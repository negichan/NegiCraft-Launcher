using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Material.Icons.WPF;
using NegiCraftLauncher.Pet.Services;
using NegiCraftLauncher.Pet.Wpf.Services;
using NegiCraftLauncher.Skin.Wpf.Controls;

namespace NegiCraftLauncher.Pet.Wpf;

/// <summary>
/// WPF 版桌宠窗口。逐段对应 Avalonia 侧 <c>Pet/PetWindow.axaml.cs</c>（1301 行），
/// 物理常量一条不改，只换平台 API：
///
/// <list type="bullet">
/// <item><b>坐标一律用 DIP</b>。Avalonia 的 <c>Window.Position</c> 是 <c>PixelPoint</c>（物理像素），
/// 所以那边到处 <c>* RenderScaling</c>；WPF 的 <c>Left/Top/Width/Height</c> 都是 DIP，
/// 于是把光标从物理像素除一次 DPI 就统一了 —— 常量（180 DIP/s 等）数值不变，
/// 屏幕上的观感与 Avalonia 版一致。</item>
/// <item><c>PixelPoint</c> → <c>Left</c>/<c>Top</c>；<c>Screens.Primary.WorkingArea</c> →
/// <see cref="SystemParameters.WorkArea"/>（也是 DIP）。</item>
/// <item><c>PointerPressed/Moved/Released/CaptureLost</c> → <c>MouseLeftButtonDown</c>/<c>MouseMove</c>/…
/// 右键菜单不手动开：WPF 会自动弹，只要在 <c>ContextMenuOpening</c> 里挡掉"正在旋转"那一下。</item>
/// <item><c>Cursor(StandardCursorType.X)</c> → <see cref="Cursors"/>。</item>
/// <item><c>StorageProvider</c> → <see cref="Microsoft.Win32.OpenFileDialog"/>。</item>
/// <item><c>MinecraftSkinPreview</c> → <see cref="SkinPreviewControl"/>（软件光栅化）。</item>
/// </list>
/// </summary>
public partial class PetWindow : Window
{
    private readonly double _baseWidth = 160;
    private readonly double _baseHeight = 320;
    private double _currentScale = 1.0;
    private readonly IPetHost? _host;

    public enum PetInteractionMode
    {
        Free,
        Control,
        FollowMouse,
        NavigateToCoord,
    }

    private PetInteractionMode _currentMode = PetInteractionMode.Free;
    private readonly List<(double X, double Y)> _navQueue = new();
    private (double X, double Y) _currentNavTarget;
    private bool _hasNavTarget;

    public int RemainingWaypointCount => (_hasNavTarget ? 1 : 0) + _navQueue.Count;

    /// <summary>宿主契约，null 表示没有宿主（独立版就是给一个最小宿主）。调试桥通过它读名字与账号列表。</summary>
    public IPetHost? Host => _host;

    /// <summary>皮肤预览控件。调试动词都挂在它上面。</summary>
    public SkinPreviewControl Preview => PetPreview;

    public PetInteractionMode CurrentMode
    {
        get => _currentMode;
        set
        {
            if (_currentMode != value)
            {
                _currentMode = value;
                UpdateModeUi();
            }
        }
    }

    public bool IsControlMode
    {
        get => _currentMode == PetInteractionMode.Control;
        set => CurrentMode = value ? PetInteractionMode.Control : PetInteractionMode.Free;
    }

    public bool IsFollowMouseMode
    {
        get => _currentMode == PetInteractionMode.FollowMouse;
        set => CurrentMode = value ? PetInteractionMode.FollowMouse : PetInteractionMode.Free;
    }

    // 交互模式是叠加开关，不进 PetInteractionMode：它和操控/跟随同时生效，屏幕任意位置的
    // 左键都算一次 MC 左键。拦截时由左Alt+CapsLock全局切换开关，普通左键直接挥拳攻击且不传给下层窗口。
    private GlobalMouseHook? _interactHook;
    private GlobalKeyboardHook? _keyboardHook;
    private bool _isInteractMode;
    private bool _interactSwallowClicks = true;

    public bool IsInteractMode
    {
        get => _isInteractMode;
        set
        {
            if (_isInteractMode == value) return;
            _isInteractMode = value;
            UpdateInteractHook();
            UpdateInteractUi();
        }
    }

    public bool InteractSwallowClicks
    {
        get => _interactSwallowClicks;
        set
        {
            if (_interactSwallowClicks == value) return;
            _interactSwallowClicks = value;
            UpdateInteractUi();
        }
    }

    public bool IsInteractHookInstalled => _interactHook?.IsInstalled ?? false;

    private void StartKeyboardHook()
    {
        if (_keyboardHook == null)
        {
            _keyboardHook = new GlobalKeyboardHook();
            _keyboardHook.ToggleInterceptModePressed += OnToggleInterceptModePressed;
        }
        _keyboardHook.Install();
    }

    private void StopKeyboardHook()
    {
        if (_keyboardHook == null) return;
        _keyboardHook.ToggleInterceptModePressed -= OnToggleInterceptModePressed;
        _keyboardHook.Dispose();
        _keyboardHook = null;
    }

    private void OnToggleInterceptModePressed(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            // 拦截模式隶属于交互模式：只有开启了交互模式才能切换拦截状态
            if (!_isInteractMode) return;

            InteractSwallowClicks = !InteractSwallowClicks;
            if (InteractSwallowClicks)
            {
                PetPreview.TriggerAttack();
            }
        });
    }

    private void UpdateInteractHook()
    {
        if (_isInteractMode)
        {
            if (_interactHook == null)
            {
                _interactHook = new GlobalMouseHook();
                _interactHook.LeftButtonDown += OnGlobalLeftButtonDown;
            }

            if (!_interactHook.Install())
            {
                Console.WriteLine("[PetWindow] 全局鼠标钩子安装失败，交互模式不可用");
                DropInteractHook();
                StopKeyboardHook();
                _isInteractMode = false;
                return;
            }

            StartKeyboardHook();
            return;
        }

        DropInteractHook();
        StopKeyboardHook();
    }

    private void DropInteractHook()
    {
        if (_interactHook == null) return;

        _interactHook.LeftButtonDown -= OnGlobalLeftButtonDown;
        _interactHook.Dispose();
        _interactHook = null;
    }

    private void OnGlobalLeftButtonDown(object? sender, GlobalMouseHook.LeftClickEventArgs e)
    {
        if (!_isInteractMode) return;

        // 如果点击发生在桌宠窗口自身内部，不拦截，以便用户能正常拖拽桌宠或交互。
        // 钩子给的是**物理像素**，而 WPF 的 Left/Top/Width/Height 是 DIP —— Avalonia 那边
        // 乘 RenderScaling，这边反过来除，效果一样。
        var (clickDipX, clickDipY) = ToDip(new WinPoint { X = e.ScreenX, Y = e.ScreenY });
        var isOverPet = clickDipX >= Left && clickDipX < Left + Width &&
                        clickDipY >= Top && clickDipY < Top + Height;

        if (_interactSwallowClicks && !isOverPet)
        {
            e.Swallow = true;
        }

        Dispatcher.BeginInvoke(() => PetPreview.TriggerAttack());
    }

    private void UpdateInteractUi()
    {
        MenuInteractModeIcon.Visibility = Vis(_isInteractMode);
        MenuInteractSwallowIcon.Visibility = Vis(_interactSwallowClicks);
        MenuInteractSwallow.IsEnabled = _isInteractMode;
        MenuInteractMode.Header = "交互模式";
        MenuInteractSwallow.Header = "拦截模式（左Alt+CapsLock切换）";
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    // ==========================================================
    // 原生输入
    // ==========================================================

    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_SPACE = 0x20;
    private const int VK_A = 0x41;
    private const int VK_D = 0x44;
    private const int VK_S = 0x53;
    private const int VK_W = 0x57;

    private DispatcherTimer? _physicsTimer;
    private readonly System.Diagnostics.Stopwatch _physicsStopwatch = new();
    private DateTime _lastWPressTime = DateTime.MinValue;
    private bool _isSprintLocked;
    private bool _manualSneakToggle;
    private bool _isKeyShiftHeld;
    private bool _isPhysicalShiftDown;

    private double _groundPosX;
    private double _groundPosY;
    private double _jumpOffsetY;
    private double _jumpVelocityY;
    private bool _isJumping;

    private bool _keyW;
    private bool _keyA;
    private bool _keyS;
    private bool _keyD;
    private bool _keySpace;
    private bool _keyCtrl;

    public PetWindow()
    {
        InitializeComponent();

        // 窗口还没上屏时 HWND 不存在，ApplyToolWindow 会挂到 SourceInitialized 上。
        PetShellStyle.ApplyToolWindow(this);
    }

    public PetWindow(string initialPlayerName, IPetHost? host = null) : this()
    {
        _host = host;

        var effectiveName = !string.IsNullOrWhiteSpace(_host?.EffectiveName)
            ? _host.EffectiveName
            : initialPlayerName;

        if (!string.IsNullOrWhiteSpace(effectiveName))
        {
            PetPreview.PlayerName = effectiveName;
        }

        if (_host != null)
        {
            _host.PropertyChanged += OnHostPropertyChanged;
        }

        // Launcher-only affordances disappear when nothing can host them (standalone build).
        if (_host is null || !_host.CanOpenLauncher)
        {
            MenuOpenLauncher.Visibility = Visibility.Collapsed;
            // With no launcher there is no "home page" to inherit a name from.
            MenuResetName.Header = "恢复默认名字";
        }

        UpdateResetNameMenuState();
        PopulateAccountMenu();
        StartShiftMonitoring();
        UpdateInteractHook();
        UpdateInteractUi();

        Closed += (_, _) =>
        {
            StopShiftMonitoring();
            StopKeyboardHook();
            DropInteractHook();
            if (_host != null)
            {
                _host.PropertyChanged -= OnHostPropertyChanged;
            }
        };
    }

    public void SetPlayerName(string name)
    {
        PetPreview.PlayerName = name;
    }

    /// <summary>
    /// 放到主屏工作区的右下角 —— 默认落脚点。宿主在 Show 之前调；
    /// 想放别处的宿主自己设 <see cref="Window.Left"/>/<see cref="Window.Top"/>。
    /// </summary>
    public void PlaceAtDefaultCorner()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - 190;
        Top = workArea.Bottom - 290;
    }

    public void ApplySkin(byte[] skinBytes)
    {
        PetPreview.ApplySkin(skinBytes);
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IPetHost.EffectiveName)) return;

        if (_host != null && !string.IsNullOrWhiteSpace(_host.EffectiveName))
        {
            PetPreview.PlayerName = _host.EffectiveName;
            UpdateResetNameMenuState();
        }
    }

    private void UpdateResetNameMenuState()
    {
        MenuResetName.IsEnabled = !string.IsNullOrWhiteSpace(_host?.CustomName);
    }

    private void PopulateAccountMenu()
    {
        if (_host == null || _host.AccountNames.Count == 0) return;

        foreach (var accName in _host.AccountNames)
        {
            var name = accName;
            var item = new System.Windows.Controls.MenuItem { Header = name };
            item.Click += (_, _) =>
            {
                // 手动选择账户皮肤并修改桌宠名字
                if (_host != null)
                {
                    _host.CustomName = name;
                }
                PetPreview.PlayerName = name;
                UpdateResetNameMenuState();
            };
            MenuAccountsParent.Items.Add(item);
        }
    }

    // ==========================================================
    // DPI / 光标换算
    // ==========================================================

    /// <summary>窗口当前的 DPI 缩放。桌宠是单屏应用，X/Y 视为相同。</summary>
    private double DpiScale
    {
        get
        {
            var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return scale > 0 ? scale : 1.0;
        }
    }

    /// <summary>把屏幕物理像素换算成窗口用的 DIP。</summary>
    private (double X, double Y) ToDip(WinPoint p) => ToDip(p.X, p.Y);

    /// <summary>把屏幕物理像素换算成窗口用的 DIP（重载：坐标本来就是 double 时用，避免截断）。</summary>
    private (double X, double Y) ToDip(double x, double y)
    {
        var scale = DpiScale;
        return (x / scale, y / scale);
    }

    #region 心跳定时器与操控物理 (WASD+空格/Shift)

    private void StartShiftMonitoring()
    {
        TrackDebugInfo = "START_CALLED";
        _physicsStopwatch.Restart();
        _physicsTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        _physicsTimer.Tick += OnShiftMonitorTick;
        _physicsTimer.Start();
    }

    private void StopShiftMonitoring()
    {
        if (_physicsTimer != null)
        {
            _physicsTimer.Tick -= OnShiftMonitorTick;
            _physicsTimer.Stop();
            _physicsTimer = null;
        }
        _physicsStopwatch.Stop();
    }

    private void OnShiftMonitorTick(object? sender, EventArgs e)
    {
        try
        {
            var dt = _physicsStopwatch.Elapsed.TotalSeconds;
            _physicsStopwatch.Restart();
            if (dt < 0.002) return;
            if (dt > 0.04) dt = 0.04;

            // 被抓握或在空中拖拽时，以挣扎晃头动画为先
            if (_isLeftPressed || _isLeftDragging)
            {
                _isJumping = false;
                _jumpOffsetY = 0;
                _jumpVelocityY = 0;
                _groundPosX = Left;
                _groundPosY = Top;
                PetPreview.IsWalking = false;
                PetPreview.IsJumping = false;
                PetPreview.IsSprinting = false;
                return;
            }

            if (_groundPosX == 0 && _groundPosY == 0 && (Left != 0 || Top != 0))
            {
                _groundPosX = Left;
                _groundPosY = Top;
            }

            // 1. Shift 下蹲状态检测
            var isDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            if (isDown != _isPhysicalShiftDown)
            {
                _isPhysicalShiftDown = isDown;
                UpdateSneakState();
            }

            // 2. 模式检测与物理模拟
            switch (_currentMode)
            {
                case PetInteractionMode.Control:
                    UpdateControlPhysics(dt);
                    break;
                case PetInteractionMode.FollowMouse:
                    UpdateFollowMousePhysics(dt);
                    break;
                case PetInteractionMode.NavigateToCoord:
                    UpdateCoordNavigationPhysics(dt);
                    break;
                case PetInteractionMode.Free:
                default:
                    break;
            }

            // 3. 鼠标视线追踪与身体自动平滑转向
            UpdateMouseLookAndBodyTurn(dt);
        }
        catch (Exception ex)
        {
            TrackDebugInfo = "EX: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    public string TrackDebugInfo { get; private set; } = "";
    private WinPoint _lastCursorPos = new() { X = -1, Y = -1 };
    private bool _hasVirtualCursor;
    private WinPoint _virtualCursor;

    /// <summary>调试用：喂一个假的屏幕光标（物理像素），免得验证时要真去动鼠标。</summary>
    public void SetVirtualCursor(int x, int y)
    {
        _hasVirtualCursor = true;
        _virtualCursor = new WinPoint { X = x, Y = y };
    }

    public void ClearVirtualCursor()
    {
        _hasVirtualCursor = false;
    }

    /// <summary>取当前光标（DIP）。虚拟光标优先，其次是真实光标，最后退回上一次的位置。</summary>
    private (double X, double Y) CursorDip()
    {
        if (_hasVirtualCursor) return ToDip(_virtualCursor);

        if (GetCursorPos(out var realCur))
        {
            _lastCursorPos = realCur;
            return ToDip(realCur);
        }

        if (_lastCursorPos.X != -1) return ToDip(_lastCursorPos);

        // 兜底为屏幕正前方
        return (_groundPosX + (Width / 2.0), _groundPosY - 100.0);
    }

    private void UpdateMouseLookAndBodyTurn(double dt)
    {
        if (_isLeftDragging || _isLeftPressed)
        {
            TrackDebugInfo = "LOOK_DRAGGING";
            return;
        }

        var (curX, curY) = CursorDip();

        var headCenterX = _groundPosX + (Width / 2.0);
        var headCenterY = _groundPosY + ((52.0 + PetPreview.StageOffsetY) * _currentScale);

        var dx = curX - headCenterX;
        var dy = curY - headCenterY;

        // 计算光标相对于小人的水平方位角与俯仰角
        var targetLookYaw = (float)(Math.Atan2(dx, 420.0) * (180.0 / Math.PI));
        var targetPitchDeg = (float)Math.Clamp(Math.Atan2(dy, 380.0) * (180.0 / Math.PI), -24.0, 24.0);

        var currentYaw = PetPreview.CurrentYawDeg;
        var diffYaw = targetLookYaw - currentYaw;
        while (diffYaw > 180f) diffYaw -= 360f;
        while (diffYaw < -180f) diffYaw += 360f;

        TrackDebugInfo =
            $"cursor=({curX:F0},{curY:F0}) head=({(int)headCenterX},{(int)headCenterY}) dx={dx:F0} dy={dy:F0} " +
            $"targetYaw={targetLookYaw:F1} curYaw={currentYaw:F1} diffYaw={diffYaw:F1} isWalking={PetPreview.IsWalking}";

        // 身体自动平滑转身：静止且头部扭角超出舒适范围（|diffYaw| > 25°）时，身体平滑转向鼠标。
        if (!PetPreview.IsWalking)
        {
            var absDiff = Math.Abs(diffYaw);
            if (absDiff > 25f)
            {
                var deficit = absDiff - 15f;
                var maxTurn = 260f * (float)dt; // 最大角速度 260 deg/s
                var turnStep = Math.Clamp(deficit * 7f * (float)dt, -maxTurn, maxTurn) * Math.Sign(diffYaw);
                PetPreview.RotateModel(turnStep);
            }
        }

        PetPreview.SetHeadLookAt(targetPitchDeg, targetLookYaw);
    }

    private void UpdateControlPhysics(double dt)
    {
        var wDown = (GetAsyncKeyState(VK_W) & 0x8000) != 0 || _keyW;
        var aDown = (GetAsyncKeyState(VK_A) & 0x8000) != 0 || _keyA;
        var sDown = (GetAsyncKeyState(VK_S) & 0x8000) != 0 || _keyS;
        var dDown = (GetAsyncKeyState(VK_D) & 0x8000) != 0 || _keyD;
        var spaceDown = (GetAsyncKeyState(VK_SPACE) & 0x8000) != 0 || _keySpace;
        var ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || _keyCtrl;

        double moveX = 0;
        double moveY = 0;
        if (dDown) moveX += 1;
        if (aDown) moveX -= 1;
        if (sDown) moveY += 1;
        if (wDown) moveY -= 1;

        var isMoving = moveX != 0 || moveY != 0;
        PetPreview.IsWalking = isMoving;

        // 疾跑状态：按住 Ctrl 或双击 W，且不在下蹲
        var isSprinting = isMoving && (ctrlDown || _isSprintLocked) && !PetPreview.Sneaking;
        PetPreview.IsSprinting = isSprinting;

        if (isMoving)
        {
            var len = Math.Sqrt((moveX * moveX) + (moveY * moveY));
            var dirX = moveX / len;
            var dirY = moveY / len;

            // 桌面环境适配速度 (DIP/s):
            // 潜行 ~80 / 行走 ~180 / 疾跑 ~270 / 跳跃中移动 200(走) 290(疾跑)
            double speed;
            if (PetPreview.Sneaking)
            {
                speed = 80.0;
            }
            else if (isSprinting)
            {
                speed = _isJumping ? 290.0 : 270.0;
            }
            else
            {
                speed = _isJumping ? 200.0 : 180.0;
            }

            _groundPosX += dirX * speed * dt;
            _groundPosY += dirY * speed * dt;
            ClampToScreenBounds();

            // 平滑旋转至前进方向（最大转速 720 deg/s）
            var targetYaw = (float)(Math.Atan2(dirX, dirY) * (180.0 / Math.PI));
            var diff = targetYaw - PetPreview.CurrentYawDeg;
            while (diff > 180f) diff -= 360f;
            while (diff < -180f) diff += 360f;
            var maxTurn = 720f * (float)dt;
            var turnStep = Math.Clamp(diff * 14f * (float)dt, -maxTurn, maxTurn);
            PetPreview.RotateModel(turnStep);
        }

        // Minecraft 风格起跳模拟 (初速度 -460 DIP/s, 重力 2400 DIP/s², 滞空约 0.38s, 高度约 44 DIP)
        if (spaceDown && !_isJumping)
        {
            _isJumping = true;
            _jumpVelocityY = -460.0;
            PetPreview.IsJumping = true;
        }

        if (_isJumping)
        {
            const double gravity = 2400.0;
            _jumpOffsetY += _jumpVelocityY * dt;
            _jumpVelocityY += gravity * dt;

            if (_jumpOffsetY >= 0)
            {
                _jumpOffsetY = 0;
                _jumpVelocityY = 0;
                _isJumping = false;
                PetPreview.IsJumping = false;

                // 连跳检测（按住空格落地继续跳）
                if (spaceDown)
                {
                    _isJumping = true;
                    _jumpVelocityY = -460.0;
                    PetPreview.IsJumping = true;
                }
            }
        }

        PetPreview.SetJumpOffset(_jumpOffsetY);
        MoveWindowTo(_groundPosX, _groundPosY);
    }

    private void UpdateFollowMousePhysics(double dt)
    {
        var (curX, curY) = CursorDip();

        var petFeetX = _groundPosX + (Width / 2.0);
        var petFeetY = _groundPosY + ((160.0 + PetPreview.StageOffsetY) * _currentScale);

        var dx = curX - petFeetX;
        var dy = curY - petFeetY;
        var dist = Math.Sqrt((dx * dx) + (dy * dy));

        // 停靠半径：鼠标在身旁 ~85px 内时停下脚步
        const double dockRadius = 85.0;

        if (dist <= dockRadius)
        {
            PetPreview.IsWalking = false;
            PetPreview.IsSprinting = false;
            return;
        }

        PetPreview.IsWalking = true;
        var isSprint = dist > 360.0;
        PetPreview.IsSprinting = isSprint;

        var speed = isSprint ? 270.0 : 180.0;
        var dirX = dx / dist;
        var dirY = dy / dist;

        var targetYaw = (float)(Math.Atan2(dirX, dirY) * (180.0 / Math.PI));
        var diff = targetYaw - PetPreview.CurrentYawDeg;
        while (diff > 180f) diff -= 360f;
        while (diff < -180f) diff += 360f;
        var maxTurn = 720f * (float)dt;
        var turnStep = Math.Clamp(diff * 14f * (float)dt, -maxTurn, maxTurn);
        PetPreview.RotateModel(turnStep);

        var moveDist = Math.Min(speed * dt, dist - dockRadius + 2.0);
        _groundPosX += dirX * moveDist;
        _groundPosY += dirY * moveDist;

        ClampToScreenBounds();
        MoveWindowTo(_groundPosX, _groundPosY);
    }

    private void UpdateCoordNavigationPhysics(double dt)
    {
        if (!_hasNavTarget)
        {
            PetPreview.IsWalking = false;
            PetPreview.IsSprinting = false;
            CurrentMode = PetInteractionMode.Free;
            return;
        }

        var petFeetX = _groundPosX + (Width / 2.0);
        var petFeetY = _groundPosY + ((160.0 + PetPreview.StageOffsetY) * _currentScale);

        var dx = _currentNavTarget.X - petFeetX;
        var dy = _currentNavTarget.Y - petFeetY;
        var dist = Math.Sqrt((dx * dx) + (dy * dy));

        // 到达当前航点（8px 内）
        if (dist <= 8.0)
        {
            if (_navQueue.Count > 0)
            {
                // 还有后续途经点：出队下一个点继续移动
                _currentNavTarget = _navQueue[0];
                _navQueue.RemoveAt(0);
                dx = _currentNavTarget.X - petFeetX;
                dy = _currentNavTarget.Y - petFeetY;
                dist = Math.Sqrt((dx * dx) + (dy * dy));
            }
            else
            {
                // 全部途经点到达完成，停下脚步，自动切回自由待机模式
                _hasNavTarget = false;
                PetPreview.IsWalking = false;
                PetPreview.IsSprinting = false;
                CurrentMode = PetInteractionMode.Free;
                return;
            }
        }

        PetPreview.IsWalking = true;
        var isSprint = dist > 350.0;
        PetPreview.IsSprinting = isSprint;

        var speed = isSprint ? 270.0 : 180.0;
        var dirX = dx / dist;
        var dirY = dy / dist;

        var targetYaw = (float)(Math.Atan2(dirX, dirY) * (180.0 / Math.PI));
        var diff = targetYaw - PetPreview.CurrentYawDeg;
        while (diff > 180f) diff -= 360f;
        while (diff < -180f) diff += 360f;
        var maxTurn = 720f * (float)dt;
        var turnStep = Math.Clamp(diff * 14f * (float)dt, -maxTurn, maxTurn);
        PetPreview.RotateModel(turnStep);

        var moveDist = Math.Min(speed * dt, dist);
        _groundPosX += dirX * moveDist;
        _groundPosY += dirY * moveDist;

        ClampToScreenBounds();
        MoveWindowTo(_groundPosX, _groundPosY);
    }

    private void MoveWindowTo(double dipX, double dipY)
    {
        var roundedX = Math.Round(dipX);
        var roundedY = Math.Round(dipY);
        if (Math.Abs(Left - roundedX) < 0.5 && Math.Abs(Top - roundedY) < 0.5) return;

        Left = roundedX;
        Top = roundedY;
    }

    private void ClampToScreenBounds()
    {
        var workArea = SystemParameters.WorkArea;

        var minX = workArea.X;
        var maxX = workArea.X + workArea.Width - Width;
        var minY = workArea.Y;
        var maxY = workArea.Y + workArea.Height - Height;

        _groundPosX = Math.Clamp(_groundPosX, minX, maxX);
        _groundPosY = Math.Clamp(_groundPosY, minY, maxY);
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _isKeyShiftHeld = true;
            UpdateSneakState();
        }
        else if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _keyCtrl = true;
        }
        else if (e.Key == Key.W)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastWPressTime).TotalMilliseconds < 350)
            {
                _isSprintLocked = true;
            }
            _lastWPressTime = now;
            _keyW = true;
        }
        else if (e.Key == Key.A) _keyA = true;
        else if (e.Key == Key.S) _keyS = true;
        else if (e.Key == Key.D) _keyD = true;
        else if (e.Key == Key.Space) _keySpace = true;
        else if (e.Key == Key.Escape)
        {
            ClearNavigation();
            if (_currentMode != PetInteractionMode.Free)
            {
                CurrentMode = PetInteractionMode.Free;
            }
        }
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift)
        {
            _isKeyShiftHeld = false;
            UpdateSneakState();
        }
        else if (e.Key is Key.LeftCtrl or Key.RightCtrl)
        {
            _keyCtrl = false;
        }
        else if (e.Key == Key.W)
        {
            _keyW = false;
            _isSprintLocked = false;
        }
        else if (e.Key == Key.A) _keyA = false;
        else if (e.Key == Key.S) _keyS = false;
        else if (e.Key == Key.D) _keyD = false;
        else if (e.Key == Key.Space) _keySpace = false;
    }

    /// <summary>调试用：模拟按键，绕开"窗口拿不到焦点"这件事。</summary>
    public void SetSimulatedKey(string key, bool down)
    {
        switch (key.ToLowerInvariant())
        {
            case "w": _keyW = down; break;
            case "a": _keyA = down; break;
            case "s": _keyS = down; break;
            case "d": _keyD = down; break;
            case "space": _keySpace = down; break;
            case "ctrl": _keyCtrl = down; break;
            case "shift": _isKeyShiftHeld = down; UpdateSneakState(); break;
        }
    }

    private void UpdateSneakState()
    {
        var shouldSneak = _manualSneakToggle || _isKeyShiftHeld || _isPhysicalShiftDown;
        if (PetPreview.Sneaking != shouldSneak)
        {
            PetPreview.Sneaking = shouldSneak;
        }
        MenuSneakIcon.Visibility = Vis(_manualSneakToggle);
    }

    #endregion

    #region 鼠标拖拽、抓取手势与旋转

    private bool _isLeftPressed;
    private bool _isLeftDragging;
    private WinPoint _leftPressCursor;

    private bool _isRightPressed;
    private Point _rightPressPoint;
    private Point _lastRightPoint;
    private bool _isRightRotating;

    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击切换手动潜行（Avalonia 是 DoubleTapped；WPF 的 Grid 没有 DoubleClick 事件，看 ClickCount）。
        if (e.ClickCount == 2)
        {
            _manualSneakToggle = !_manualSneakToggle;
            UpdateSneakState();
        }

        if (_currentMode == PetInteractionMode.NavigateToCoord)
        {
            ClearNavigation();
        }

        if (!GetCursorPos(out _leftPressCursor)) return;

        _isLeftPressed = true;
        _isLeftDragging = false;
        PetPreview.IsDangling = true; // 抓握瞬间立即触发挣扎摇晃头部动画！
        RootPanel.CaptureMouse();
        e.Handled = true;
    }

    private void OnRootMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isRightPressed = true;
        _isRightRotating = false;
        _rightPressPoint = e.GetPosition(this);
        _lastRightPoint = _rightPressPoint;
        RootPanel.CaptureMouse();
        e.Handled = true;
    }

    private void OnRootMouseMove(object sender, MouseEventArgs e)
    {
        if (_isLeftPressed)
        {
            if (GetCursorPos(out var cur))
            {
                var dist = Math.Sqrt(Math.Pow(cur.X - _leftPressCursor.X, 2) + Math.Pow(cur.Y - _leftPressCursor.Y, 2));
                if (dist > 3.0 || _isLeftDragging)
                {
                    if (!_isLeftDragging)
                    {
                        _isLeftDragging = true;
                        PetPreview.IsDangling = true;
                        // 拖拽时变成拖拽手势
                        Cursor = Cursors.SizeAll;
                        RootPanel.Cursor = Cursors.SizeAll;
                    }

                    // 小人双手精确对齐抓握鼠标箭头
                    var (curDipX, curDipY) = ToDip(cur);
                    var gripDipX = 80.0 * _currentScale;
                    var gripDipY = (22.0 + PetPreview.StageOffsetY) * _currentScale;

                    Left = curDipX - gripDipX;
                    Top = curDipY + 6.0 - gripDipY;
                }
            }
            e.Handled = true;
        }
        else if (_isRightPressed)
        {
            var curPos = e.GetPosition(this);
            var totalDist = Math.Sqrt(Math.Pow(curPos.X - _rightPressPoint.X, 2) + Math.Pow(curPos.Y - _rightPressPoint.Y, 2));
            if (totalDist > 6.0)
            {
                _isRightRotating = true;
                var dx = curPos.X - _lastRightPoint.X;
                _lastRightPoint = curPos;
                PetPreview.RotateModel((float)(dx * 0.7));
            }
            e.Handled = true;
        }
    }

    private void OnRootMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isLeftPressed) return;

        _isLeftPressed = false;
        PetPreview.IsDangling = false;
        if (_isLeftDragging)
        {
            _isLeftDragging = false;
            // 恢复为默认手势
            Cursor = Cursors.Hand;
            RootPanel.Cursor = Cursors.Hand;

            _groundPosX = Left;
            _groundPosY = Top;
            _jumpOffsetY = 0;
            _jumpVelocityY = 0;
            _isJumping = false;
            PetPreview.SetJumpOffset(0);

            // 落地时检查是否处于按 Shift 状态
            UpdateSneakState();
        }
        RootPanel.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnRootMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRightPressed) return;

        _isRightPressed = false;
        RootPanel.ReleaseMouseCapture();

        if (_isRightRotating)
        {
            _isRightRotating = false;
            // 兜底：万一 ContextMenuOpening 比这里晚跑（WPF 的类处理器顺序），
            // 菜单已经被自动弹出来了，这里再关掉一次。
            PetContextMenu.IsOpen = false;
            e.Handled = true;
        }
    }

    private void OnRootContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // 右键拖动是在转模型，不该弹菜单。
        if (_isRightRotating) e.Handled = true;
    }

    private void OnRootLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isLeftPressed)
        {
            _isLeftPressed = false;
            _isLeftDragging = false;
            PetPreview.IsDangling = false;
            Cursor = Cursors.Hand;
            RootPanel.Cursor = Cursors.Hand;
            _groundPosX = Left;
            _groundPosY = Top;
            _jumpOffsetY = 0;
            _jumpVelocityY = 0;
            _isJumping = false;
            PetPreview.SetJumpOffset(0);
            UpdateSneakState();
        }
        if (_isRightPressed)
        {
            _isRightPressed = false;
            _isRightRotating = false;
        }
    }

    /// <summary>调试用：程序化打开右键菜单（WPF 没有 Avalonia 的 <c>ContextMenu.Open(target)</c>）。</summary>
    public void OpenPetContextMenu()
    {
        PetContextMenu.PlacementTarget = RootPanel;
        PetContextMenu.IsOpen = true;
    }

    /// <summary>调试用：程序化关闭右键菜单。</summary>
    public void ClosePetContextMenu() => PetContextMenu.IsOpen = false;

    /// <summary>右键菜单当前是否开着。</summary>
    public bool IsPetContextMenuOpen => PetContextMenu.IsOpen;

    #endregion

    #region 右键菜单事件

    private void OnToggleTopmostClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (MenuTopmost.Icon is MaterialIcon icon)
        {
            icon.Visibility = Vis(Topmost);
        }
    }

    private void SetScale(double scale)
    {
        _currentScale = scale;
        Width = _baseWidth * scale;
        Height = _baseHeight * scale;
    }

    private void OnScale75Click(object sender, RoutedEventArgs e) => SetScale(0.75);

    private void OnScale100Click(object sender, RoutedEventArgs e) => SetScale(1.0);

    private void OnScale125Click(object sender, RoutedEventArgs e) => SetScale(1.25);

    private void OnScale150Click(object sender, RoutedEventArgs e) => SetScale(1.5);

    private void OnToggleSneakClick(object sender, RoutedEventArgs e)
    {
        _manualSneakToggle = !_manualSneakToggle;
        UpdateSneakState();
    }

    /// <summary>调试用：设一个屏幕坐标（物理像素）作为导航目标。</summary>
    public void SetNavigationTarget(double screenX, double screenY)
    {
        _navQueue.Clear();
        _currentNavTarget = ToDip(screenX, screenY);
        _hasNavTarget = true;
        CurrentMode = PetInteractionMode.NavigateToCoord;
    }

    /// <summary>调试用：追加一个途经点。</summary>
    public void AddNavigationTarget(double screenX, double screenY)
    {
        var target = ToDip(screenX, screenY);
        if (_currentMode != PetInteractionMode.NavigateToCoord || !_hasNavTarget)
        {
            _navQueue.Clear();
            _currentNavTarget = target;
            _hasNavTarget = true;
            CurrentMode = PetInteractionMode.NavigateToCoord;
        }
        else
        {
            _navQueue.Add(target);
        }
    }

    public void ClearNavigation()
    {
        _navQueue.Clear();
        _hasNavTarget = false;
        if (_currentMode == PetInteractionMode.NavigateToCoord)
        {
            CurrentMode = PetInteractionMode.Free;
        }
    }

    private void UpdateModeUi()
    {
        MenuFreeModeIcon.Visibility = Vis(_currentMode == PetInteractionMode.Free);
        MenuControlModeIcon.Visibility = Vis(_currentMode == PetInteractionMode.Control);
        MenuFollowMouseIcon.Visibility = Vis(_currentMode == PetInteractionMode.FollowMouse);

        if (_currentMode != PetInteractionMode.NavigateToCoord)
        {
            _navQueue.Clear();
            _hasNavTarget = false;
        }

        if (_currentMode == PetInteractionMode.Control)
        {
            _groundPosX = Left;
            _groundPosY = Top;
            _jumpOffsetY = 0;
            _jumpVelocityY = 0;
            _isJumping = false;
            Activate();
            Focus();
        }
        else
        {
            _keyW = false;
            _keyA = false;
            _keyS = false;
            _keyD = false;
            _keySpace = false;
            _keyCtrl = false;
            _isSprintLocked = false;
            if (_currentMode == PetInteractionMode.Free)
            {
                PetPreview.IsWalking = false;
                PetPreview.IsSprinting = false;
                PetPreview.IsJumping = false;
                _isJumping = false;
                _jumpOffsetY = 0;
                PetPreview.SetJumpOffset(0);
                Left = Math.Round(_groundPosX);
                Top = Math.Round(_groundPosY);
            }
        }
    }

    private void OnSelectFreeModeClick(object sender, RoutedEventArgs e) =>
        CurrentMode = PetInteractionMode.Free;

    private void OnToggleControlModeClick(object sender, RoutedEventArgs e) =>
        IsControlMode = !IsControlMode;

    private void OnToggleFollowMouseClick(object sender, RoutedEventArgs e) =>
        IsFollowMouseMode = !IsFollowMouseMode;

    private void OnToggleInteractModeClick(object sender, RoutedEventArgs e) =>
        IsInteractMode = !IsInteractMode;

    private void OnToggleInteractSwallowClick(object sender, RoutedEventArgs e) =>
        InteractSwallowClicks = !InteractSwallowClicks;

    private CoordPickOverlayWindow ShowCoordPickOverlay()
    {
        var overlay = new CoordPickOverlayWindow((targetPoint, _) =>
        {
            AddNavigationTarget(targetPoint.X, targetPoint.Y);
        });
        overlay.Show();
        return overlay;
    }

    private void OnPickCoordClick(object sender, RoutedEventArgs e) => ShowCoordPickOverlay();

    private void OnResetRotationClick(object sender, RoutedEventArgs e) =>
        PetPreview.ResetRotation();

    private async void OnChangeNameClick(object sender, RoutedEventArgs e)
    {
        var current = _host?.CustomName ?? PetPreview.PlayerName ?? "";
        var newName = await PromptPetNameDialog.ShowAsync(this, current);
        if (newName is null) return;

        var trimmed = newName.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            // 输入为空代表恢复继承
            if (_host != null)
            {
                _host.CustomName = null;
                PetPreview.PlayerName = _host.EffectiveName;
            }
        }
        else
        {
            if (_host != null)
            {
                _host.CustomName = trimmed;
            }
            PetPreview.PlayerName = trimmed;
        }
        UpdateResetNameMenuState();
    }

    private void OnResetNameToInheritedClick(object sender, RoutedEventArgs e)
    {
        if (_host == null) return;

        _host.CustomName = null;
        PetPreview.PlayerName = _host.EffectiveName;
        UpdateResetNameMenuState();
    }

    private void OnPickSkinFileClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择皮肤 PNG",
            Filter = "Minecraft 皮肤|*.png|所有文件|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            PetPreview.ApplySkin(File.ReadAllBytes(dialog.FileName));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PetWindow] Failed to load skin: {ex.Message}");
        }
    }

    private void OnOpenLauncherClick(object sender, RoutedEventArgs e)
    {
        _host?.OpenLauncher();
    }

    private void OnClosePetClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    #endregion

    #region 调试动词专用（pet-dialog）

    /// <summary>调试用：程序化打开「前往指定坐标」的全屏选点遮罩。<paramref name="shotPath"/> 给了就顺手出图。</summary>
    public CoordPickOverlayWindow OpenCoordPickForDebug(string? shotPath = null)
    {
        var overlay = ShowCoordPickOverlay();
        if (shotPath is not null) SaveWindowContent(overlay, shotPath);
        return overlay;
    }

    /// <summary>
    /// 调试用：程序化打开「修改桌宠名字」对话框。<paramref name="shotPath"/> 给了就顺手出图。
    ///
    /// <para>刻意**直接构造**而不是复用 <c>OnChangeNameClick</c>：后者是 <c>async void</c>，
    /// 构造函数里抛的异常会被当成未处理的调度器异常，调试桥就只能回 OK 而看不见错误。
    /// 这样写异常能一路传回 <c>pet-dialog</c> 的调用方。</para>
    /// </summary>
    public PromptPetNameDialog OpenNameDialogForDebug(string? shotPath = null)
    {
        var current = _host?.CustomName ?? PetPreview.PlayerName ?? "";
        var dialog = new PromptPetNameDialog(current);
        dialog.Show();
        if (shotPath is not null) SaveWindowContent(dialog, shotPath);
        return dialog;
    }

    /// <summary>
    /// 调试用：关掉上面两个动词打开的窗口。
    /// 先收集再关 —— 关窗会把窗口从集合里摘掉，边遍历边关会踩到集合变更。
    /// </summary>
    public void CloseDebugDialogs()
    {
        var targets = new List<Window>();
        foreach (Window window in Application.Current.Windows)
        {
            if (window is PromptPetNameDialog or CoordPickOverlayWindow) targets.Add(window);
        }

        foreach (var window in targets) window.Close();
    }

    /// <summary>
    /// 把某个窗口的内容离屏渲染成 PNG。用来看"只有点菜单才出得来"的那两个窗口长什么样。
    /// 窗口刚 Show 出来，<c>ActualWidth</c> 可能还是 0，所以退回用 <c>Width</c> 估。
    /// </summary>
    private static void SaveWindowContent(Window window, string path)
    {
        window.UpdateLayout();
        if (window.Content is not FrameworkElement content) return;

        var width = (int)Math.Ceiling(content.ActualWidth > 0 ? content.ActualWidth : window.Width);
        var height = (int)Math.Ceiling(content.ActualHeight > 0 ? content.ActualHeight : window.Height);
        if (width <= 0 || height <= 0) return;

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(content);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    #endregion
}
