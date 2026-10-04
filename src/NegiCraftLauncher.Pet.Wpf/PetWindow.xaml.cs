using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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

    // 操控 / 跟随 / 导航物理、鼠标视线追踪、身体自动转身、键位与导航状态机全在这里。
    // 它与 Avalonia 侧那个 PetWindow 用的是**同一份实现**（NegiCraftLauncher.Pet.Core），
    // 所以两边的速度、阈值、手感不可能再漂开。
    private readonly PetMotion _motion = new();

    public int RemainingWaypointCount => _motion.RemainingWaypointCount;

    /// <summary>宿主契约，null 表示没有宿主（独立版就是给一个最小宿主）。调试桥通过它读名字与账号列表。</summary>
    public IPetHost? Host => _host;

    /// <summary>皮肤预览控件。调试动词都挂在它上面。</summary>
    public SkinPreviewControl Preview => PetPreview;

    /// <summary>
    /// 内核上一帧真正用来夹取的工作区（DIP）。<c>pet-area</c> 动词读它 ——
    /// 报内核看到的那个，而不是宿主以为的。
    /// </summary>
    public PetWorkArea CurrentWorkArea => _motion.LastWorkArea;

    /// <summary>内核上一帧看到的窗口左上角（DIP）。</summary>
    public PetPoint CurrentWindow => _motion.LastWindow;

    /// <summary>诊断串，<c>pet-motion</c> 动词读它（跳跃偏移 / 移动标志 / 地面位置）。</summary>
    public string MotionDebugInfo => _motion.MotionDebugInfo;

    public PetInteractionMode CurrentMode
    {
        get => _motion.Mode;
        // 状态清理（丢途经点队列、松开按键）在 PetMotion.SetMode 里做，
        // 清完触发 ModeChanged → OnMotionModeChanged 更新菜单与焦点。
        set => _motion.SetMode(value);
    }

    public bool IsControlMode
    {
        get => _motion.Mode == PetInteractionMode.Control;
        set => CurrentMode = value ? PetInteractionMode.Control : PetInteractionMode.Free;
    }

    public bool IsFollowMouseMode
    {
        get => _motion.Mode == PetInteractionMode.FollowMouse;
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

    // 只留光标查询。WASD / 空格 / Shift / Ctrl 的按住状态走 Pet.Core 的
    // Services.PetNativeKeys（那份 GetAsyncKeyState + VK 常量两端共用，不用各写一遍）。
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint lpPoint);

    // 多屏工作区查询。WPF 没有"取某块屏工作区"的托管 API（SystemParameters.WorkArea
    // 只给主屏），只能自己问 Win32。
    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect32 rcMonitor;
        public Rect32 rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MonitorInfo lpmi);

    private DispatcherTimer? _physicsTimer;
    private readonly System.Diagnostics.Stopwatch _physicsStopwatch = new();

    public PetWindow()
    {
        InitializeComponent();

        // 窗口还没上屏时 HWND 不存在，ApplyToolWindow 会挂到 SourceInitialized 上。
        PetShellStyle.ApplyToolWindow(this);

        // 菜单图标、焦点、回自由待机时把窗口摆回去 —— 这些是 view 的事，物理内核不管。
        _motion.ModeChanged += OnMotionModeChanged;
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
        ApplyGpuSettingFromHost();
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
        // 设置页里改了 GPU 开关 —— 已经开着的桌宠要立刻换后端，不能等重开。
        if (e.PropertyName == nameof(IPetHost.UseGpu))
        {
            ApplyGpuSettingFromHost();
            return;
        }

        if (e.PropertyName != nameof(IPetHost.EffectiveName)) return;

        if (_host != null && !string.IsNullOrWhiteSpace(_host.EffectiveName))
        {
            PetPreview.PlayerName = _host.EffectiveName;
            UpdateResetNameMenuState();
        }
    }

    // ==========================================================
    // 渲染后端（GPU / 软件）
    // ==========================================================

    /// <summary>
    /// 把宿主存着的 GPU 开关应用到预览控件。宿主为 null（裸构造，只有 XAML 设计器会走）时
    /// 保持默认的软件后端。
    ///
    /// <para>值本身不存在窗口里 —— <see cref="SkinPreviewControl.UseGpu"/> 就是唯一真相，
    /// 免得菜单勾选状态和实际后端漂开。</para>
    /// </summary>
    private void ApplyGpuSettingFromHost()
    {
        PetPreview.UseGpu = _host?.UseGpu ?? false;
        MenuGpuRenderIcon.Visibility = Vis(PetPreview.UseGpu);
    }

    /// <summary>
    /// 切换 GPU / 软件渲染后端。值写回宿主（进程内托管 → 启动器设置；独立版 → pet.json），
    /// 下次开桌宠沿用。设置页那边改的话由 <see cref="OnHostPropertyChanged"/> 兜住。
    /// </summary>
    private void OnToggleGpuRenderClick(object sender, RoutedEventArgs e)
    {
        var useGpu = !PetPreview.UseGpu;
        PetPreview.UseGpu = useGpu;
        MenuGpuRenderIcon.Visibility = Vis(useGpu);

        if (_host is not null) _host.UseGpu = useGpu;
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

            // 抓握 / 拖拽时物理让位给挣扎晃头动画（判断在窗口这边，物理只是别乱动）。
            _motion.IsDragging = _isLeftPressed || _isLeftDragging;

            _motion.Tick(dt, BuildMotionContext());
            ApplyMotionToView();
        }
        catch (Exception ex)
        {
            TrackDebugInfo = "EX: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    /// <summary>把一帧物理需要的平台信息打包给内核。**全是 DIP** —— WPF 这边本来就是，不用换算。</summary>
    private PetMotionContext BuildMotionContext()
    {
        return new PetMotionContext(
            new PetStage(Width, Height, _currentScale, PetPreview.StageOffsetY),
            WorkAreaDip(),
            new PetPoint(Left, Top),
            CursorDip(),
            PetPreview.CurrentYawDeg);
    }

    /// <summary>
    /// 桌宠当前所在显示器的工作区（不含任务栏），**已换算成窗口用的 DIP**。
    ///
    /// <para><b>不能用 <see cref="SystemParameters.WorkArea"/></b> —— 那个永远只报**主屏**
    /// （映射的是 <c>SPI_GETWORKAREA</c>）。桌宠被拖到副屏后，内核下一帧就按主屏工作区夹取，
    /// 把窗口弹回主屏。拖拽期间 <c>IsDragging</c> 会绕过夹取，所以**松手那一下才发作**，
    /// 很隐蔽。Avalonia 侧走 <c>Screens.ScreenFromVisual(this).WorkingArea</c>，
    /// 两端行为必须对齐 —— 见 app.manifest 里那句"桌宠会跟着鼠标在屏幕间移动"。</para>
    ///
    /// <para><c>GetMonitorInfo</c> 给的是物理像素；PerMonitorV2 下 WPF 的 <c>Left/Top</c>
    /// 换算用的也正是**窗口当前所在显示器**的 DPI（dotnet/wpf#4127），
    /// 所以除一次 <see cref="DpiScale"/> 就落回同一个 DIP 空间。</para>
    /// </summary>
    private PetWorkArea WorkAreaDip()
    {
        var scale = DpiScale;
        var hwnd = new WindowInteropHelper(this).Handle;

        if (hwnd != IntPtr.Zero)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != IntPtr.Zero)
            {
                var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
                if (GetMonitorInfoW(monitor, ref info))
                {
                    var r = info.rcWork;
                    return new PetWorkArea(
                        r.Left / scale,
                        r.Top / scale,
                        (r.Right - r.Left) / scale,
                        (r.Bottom - r.Top) / scale);
                }
            }
        }

        // 窗口还没上屏（HWND 不存在）或查监视器失败时退回主屏工作区 —— 也就是旧行为，别更差。
        var fallback = SystemParameters.WorkArea;
        return new PetWorkArea(fallback.X, fallback.Y, fallback.Width, fallback.Height);
    }

    /// <summary>
    /// 把物理算出来的东西推给预览控件与窗口。内核不碰 UI，这一步是宿主的活。
    /// 朝向只吃增量 —— 右键拖拽旋转、<c>ResetRotation</c>、<c>pet-yaw</c> 都直接改预览控件的 yaw，
    /// 两边各记一份迟早会漂开。
    /// </summary>
    private void ApplyMotionToView()
    {
        PetPreview.IsWalking = _motion.Walking;
        PetPreview.IsSprinting = _motion.Sprinting;
        // 跳跃与抛出都是"在空中"，同一个姿势 —— 桌宠飞着的时候不该还在迈腿。
        PetPreview.IsJumping = _motion.IsAirborne;
        PetPreview.Sneaking = _motion.Sneaking;
        PetPreview.SetJumpOffset(_motion.JumpOffsetY);

        if (_motion.YawDelta != 0f) PetPreview.RotateModel(_motion.YawDelta);
        if (_motion.HasHeadLook) PetPreview.SetHeadLookAt(_motion.HeadPitch, _motion.HeadYaw);
        if (_motion.PositionDirty) MoveWindowTo(_motion.GroundX, _motion.GroundY);

        MenuSneakIcon.Visibility = Vis(_motion.ManualSneakToggle);
    }

    /// <summary>诊断串，<c>pet-track</c> 动词读它。物理每帧往里写，宿主也会写（启动标记 / 异常）。</summary>
    public string TrackDebugInfo
    {
        get => _motion.DebugInfo;
        private set => _motion.DebugInfo = value;
    }

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

    /// <summary>
    /// 取当前光标（DIP）。虚拟光标优先，其次是真实光标，再次是上一次的位置。
    /// 三者都没有就回 <c>null</c>，由物理内核自己决定怎么兜底（看方向时退到"屏幕正前方"，
    /// 跟随鼠标时干脆不动）。
    /// </summary>
    private PetPoint? CursorDip()
    {
        if (_hasVirtualCursor) return DipPoint(_virtualCursor);

        if (GetCursorPos(out var realCur))
        {
            _lastCursorPos = realCur;
            return DipPoint(realCur);
        }

        if (_lastCursorPos.X != -1) return DipPoint(_lastCursorPos);

        return null;
    }

    private PetPoint DipPoint(WinPoint p)
    {
        var (x, y) = ToDip(p);
        return new PetPoint(x, y);
    }

    private void MoveWindowTo(double dipX, double dipY)
    {
        var roundedX = Math.Round(dipX);
        var roundedY = Math.Round(dipY);
        if (Math.Abs(Left - roundedX) < 0.5 && Math.Abs(Top - roundedY) < 0.5) return;

        Left = roundedX;
        Top = roundedY;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (ToMotionKey(e.Key) is { } key)
        {
            _motion.OnKeyDown(key);
            // Shift 会改下蹲，得立刻反映到预览控件上（不等下一帧心跳）。
            if (key == PetMotionKey.Shift) PetPreview.Sneaking = _motion.Sneaking;
        }
    }

    private void OnWindowKeyUp(object sender, KeyEventArgs e)
    {
        if (ToMotionKey(e.Key) is { } key)
        {
            _motion.OnKeyUp(key);
            if (key == PetMotionKey.Shift) PetPreview.Sneaking = _motion.Sneaking;
        }
    }

    /// <summary>WPF 的键映射到物理内核的键。不关心的键回 <c>null</c>。</summary>
    private static PetMotionKey? ToMotionKey(Key key) => key switch
    {
        Key.LeftShift or Key.RightShift => PetMotionKey.Shift,
        Key.LeftCtrl or Key.RightCtrl => PetMotionKey.Ctrl,
        Key.W => PetMotionKey.W,
        Key.A => PetMotionKey.A,
        Key.S => PetMotionKey.S,
        Key.D => PetMotionKey.D,
        Key.Space => PetMotionKey.Space,
        Key.Escape => PetMotionKey.Escape,
        _ => null,
    };

    /// <summary>调试用：模拟按键，绕开"窗口拿不到焦点"这件事。</summary>
    public void SetSimulatedKey(string key, bool down)
    {
        _motion.SetSimulatedKey(key, down);
        if (key.Equals("shift", StringComparison.OrdinalIgnoreCase)) PetPreview.Sneaking = _motion.Sneaking;
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
            _motion.ToggleManualSneak();
            PetPreview.Sneaking = _motion.Sneaking;
            MenuSneakIcon.Visibility = Vis(_motion.ManualSneakToggle);
        }

        if (_motion.Mode == PetInteractionMode.NavigateToCoord)
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

            _motion.EndDrag(Left, Top);
            PetPreview.SetJumpOffset(0);
            PetPreview.IsJumping = false;
            // 下蹲状态由心跳里的物理 Shift 轮询 + 按键事件维护，下一帧（16ms）就同步上了。
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
            _motion.EndDrag(Left, Top);
            PetPreview.SetJumpOffset(0);
            PetPreview.IsJumping = false;
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
        _motion.ToggleManualSneak();
        PetPreview.Sneaking = _motion.Sneaking;
        MenuSneakIcon.Visibility = Vis(_motion.ManualSneakToggle);
    }

    /// <summary>调试用：设一个屏幕坐标（物理像素）作为导航目标。</summary>
    public void SetNavigationTarget(double screenX, double screenY)
    {
        var (x, y) = ToDip(screenX, screenY);
        _motion.SetNavigationTarget(x, y);
    }

    /// <summary>调试用：追加一个途经点（物理像素）。</summary>
    public void AddNavigationTarget(double screenX, double screenY)
    {
        var (x, y) = ToDip(screenX, screenY);
        _motion.AddNavigationTarget(x, y);
    }

    public void ClearNavigation() => _motion.ClearNavigation();

    /// <summary>
    /// 模式变了。菜单图标、切到操控模式时抢焦点、回自由待机时把窗口摆回地面位置 ——
    /// 这些都是 view 的事；丢途经点队列、松开按键那类纯状态清理在 <see cref="PetMotion.SetMode"/> 里做完了。
    /// </summary>
    private void OnMotionModeChanged(PetInteractionMode mode)
    {
        MenuFreeModeIcon.Visibility = Vis(mode == PetInteractionMode.Free);
        MenuControlModeIcon.Visibility = Vis(mode == PetInteractionMode.Control);
        MenuFollowMouseIcon.Visibility = Vis(mode == PetInteractionMode.FollowMouse);

        switch (mode)
        {
            case PetInteractionMode.Control:
                _motion.BeginControlMode(Left, Top);
                Activate();
                Focus();
                break;

            case PetInteractionMode.Free:
                _motion.EnterFreeIdle();
                PetPreview.IsWalking = false;
                PetPreview.IsSprinting = false;
                PetPreview.IsJumping = false;
                PetPreview.SetJumpOffset(0);
                MoveWindowTo(_motion.GroundX, _motion.GroundY);
                break;
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
