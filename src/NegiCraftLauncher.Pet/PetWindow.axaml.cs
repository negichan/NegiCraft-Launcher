using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Material.Icons.Avalonia;

namespace NegiCraftLauncher.Pet;

public partial class PetWindow : Window
{
    private readonly double _baseWidth = 160;
    private readonly double _baseHeight = 320;
    private double _currentScale = 1.0;
    private readonly IPetHost? _host;

    // 操控 / 跟随 / 导航物理、鼠标视线追踪、身体自动转身、键位与导航状态机全在这里。
    // 它与 WPF 侧那个 PetWindow 用的是**同一份实现**（NegiCraftLauncher.Pet.Core），
    // 所以两边的速度、阈值、手感不可能再漂开。
    //
    // 注意内核只认 DIP，而这个窗口的 Position / WorkingArea 都是**物理像素** ——
    // 换算全部在下面那几个 BuildMotionContext / MoveWindowTo / DipPoint 里做。
    private readonly PetMotion _motion = new();

    public int RemainingWaypointCount => _motion.RemainingWaypointCount;

    /// <summary>
    /// The host this pet was built with, or null for a bare pet with no host at all. Exposed for
    /// tooling (the debug bridges read the effective/custom name and the account list through it).
    /// </summary>
    public IPetHost? Host => _host;

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
    private Services.GlobalMouseHook? _interactHook;
    private Services.GlobalKeyboardHook? _keyboardHook;
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
            _keyboardHook = new Services.GlobalKeyboardHook();
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
        Dispatcher.UIThread.Post(() =>
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
                _interactHook = new Services.GlobalMouseHook();
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

    private void OnGlobalLeftButtonDown(object? sender, Services.GlobalMouseHook.LeftClickEventArgs e)
    {
        if (!_isInteractMode) return;

        // 如果点击发生在桌宠窗口自身内部，不拦截，以便用户能正常拖拽桌宠或交互
        double scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        bool isOverPet = e.ScreenX >= Position.X && e.ScreenX < Position.X + Width * scaling &&
                         e.ScreenY >= Position.Y && e.ScreenY < Position.Y + Height * scaling;

        if (_interactSwallowClicks && !isOverPet)
        {
            e.Swallow = true;
        }

        Dispatcher.UIThread.Post(() => PetPreview.TriggerAttack());
    }

    private void UpdateInteractUi()
    {
        if (MenuInteractModeIcon != null) MenuInteractModeIcon.IsVisible = _isInteractMode;
        if (MenuInteractSwallowIcon != null) MenuInteractSwallowIcon.IsVisible = _interactSwallowClicks;
        if (MenuInteractSwallow != null) MenuInteractSwallow.IsEnabled = _isInteractMode;
        if (MenuInteractMode != null) MenuInteractMode.Header = "交互模式";
        if (MenuInteractSwallow != null)
        {
            MenuInteractSwallow.Header = "拦截模式（左Alt+CapsLock切换）";
        }
    }

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

    private DispatcherTimer? _physicsTimer;
    private readonly System.Diagnostics.Stopwatch _physicsStopwatch = new();

    public PetWindow()
    {
        InitializeComponent();

        // Register before the window is ever shown: the backend applies these styles on the next
        // property update, and Show() always triggers one. See PetShellStyle for why.
        Services.PetShellStyle.ApplyToolWindow(this);

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
            if (MenuOpenLauncher != null) MenuOpenLauncher.IsVisible = false;
            // With no launcher there is no "home page" to inherit a name from.
            if (MenuResetName != null) MenuResetName.Header = "恢复默认名字";
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
    /// Places the pet at the bottom-right of the primary screen's working area — its default resting
    /// spot. Hosts call this before showing the window; a host that wants a different spot can set
    /// <see cref="Window.Position"/> directly instead.
    /// </summary>
    public void PlaceAtDefaultCorner()
    {
        var screens = Screens;
        var primary = screens.Primary ?? (screens.All.Count > 0 ? screens.All[0] : null);
        if (primary is null) return;

        var workArea = primary.WorkingArea;
        Position = new PixelPoint(
            workArea.X + workArea.Width - (int)(190 * primary.Scaling),
            workArea.Y + workArea.Height - (int)(290 * primary.Scaling));
    }

    public void ApplySkin(byte[] skinBytes)
    {
        PetPreview.ApplySkin(skinBytes);
    }

    private void OnHostPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IPetHost.EffectiveName))
        {
            if (_host != null && !string.IsNullOrWhiteSpace(_host.EffectiveName))
            {
                PetPreview.PlayerName = _host.EffectiveName;
                UpdateResetNameMenuState();
            }
        }
    }

    private void UpdateResetNameMenuState()
    {
        bool hasCustom = !string.IsNullOrWhiteSpace(_host?.CustomName);
        if (MenuResetName != null)
        {
            MenuResetName.IsEnabled = hasCustom;
        }
    }

    private void PopulateAccountMenu()
    {
        if (_host == null || _host.AccountNames.Count == 0) return;

        foreach (var accName in _host.AccountNames)
        {
            var name = accName;
            var item = new MenuItem
            {
                Header = name
            };
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

    #region 心跳定时器与操控物理 (WASD+空格/Shift)

    private void StartShiftMonitoring()
    {
        TrackDebugInfo = "START_CALLED";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _physicsStopwatch.Restart();
            _physicsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (s, e) =>
            {
                OnShiftMonitorTick(s, e);
            });
            _physicsTimer.Start();
        }
    }

    private void StopShiftMonitoring()
    {
        _physicsTimer?.Stop();
        _physicsTimer = null;
        _physicsStopwatch.Stop();
    }

    private void OnShiftMonitorTick(object? sender, EventArgs e)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return;

        try
        {
            double dt = _physicsStopwatch.Elapsed.TotalSeconds;
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

    /// <summary>RenderScaling，兜底 1.0（0 会让下面所有换算变成除零）。</summary>
    private double Scaling => RenderScaling > 0 ? RenderScaling : 1.0;

    /// <summary>
    /// 把一帧物理需要的平台信息打包给内核。**内核只认 DIP**，而这个窗口的
    /// <c>Position</c> / <c>WorkingArea</c> 都是物理像素，所以这里统一除一次缩放。
    /// </summary>
    private PetMotionContext BuildMotionContext()
    {
        var scale = Scaling;
        var screen = Screens.ScreenFromVisual(this) ?? Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        var area = screen?.WorkingArea ?? default;

        return new PetMotionContext(
            new PetStage(Width, Height, _currentScale, PetPreview.StageOffsetY),
            new PetWorkArea(area.X / scale, area.Y / scale, area.Width / scale, area.Height / scale),
            new PetPoint(Position.X / scale, Position.Y / scale),
            CursorDip(),
            PetPreview.CurrentYawDeg);
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
        PetPreview.IsJumping = _motion.IsJumping;
        PetPreview.Sneaking = _motion.Sneaking;
        PetPreview.SetJumpOffset(_motion.JumpOffsetY);

        if (_motion.YawDelta != 0f) PetPreview.RotateModel(_motion.YawDelta);
        if (_motion.HasHeadLook) PetPreview.SetHeadLookAt(_motion.HeadPitch, _motion.HeadYaw);
        if (_motion.PositionDirty) MoveWindowTo(_motion.GroundX, _motion.GroundY);

        UpdateSneakMenuIcon();
    }

    /// <summary>把 DIP 的地面位置落到窗口上（Position 是物理像素，乘回去）。</summary>
    private void MoveWindowTo(double dipX, double dipY)
    {
        var scale = Scaling;
        var target = new PixelPoint(
            (int)Math.Round(dipX * scale),
            (int)Math.Round(dipY * scale));
        if (Position != target) Position = target;
    }

    /// <summary>诊断串，<c>pet-track</c> 动词读它。物理每帧往里写，宿主也会写（启动标记 / 异常）。</summary>
    public string TrackDebugInfo
    {
        get => _motion.DebugInfo;
        private set => _motion.DebugInfo = value;
    }

    private WinPoint _lastCursorPos = new WinPoint { X = -1, Y = -1 };
    private bool _hasVirtualCursor;
    private WinPoint _virtualCursor;

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
        var scale = Scaling;

        if (_hasVirtualCursor) return new PetPoint(_virtualCursor.X / scale, _virtualCursor.Y / scale);

        if (GetCursorPos(out var realCur))
        {
            _lastCursorPos = realCur;
            return new PetPoint(realCur.X / scale, realCur.Y / scale);
        }

        if (_lastCursorPos.X != -1)
        {
            return new PetPoint(_lastCursorPos.X / scale, _lastCursorPos.Y / scale);
        }

        return null;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ToMotionKey(e.Key) is not { } key) return;

        _motion.OnKeyDown(key);
        // Shift 会改下蹲，得立刻反映到预览控件上（不等下一帧心跳）。
        if (key == PetMotionKey.Shift) PetPreview.Sneaking = _motion.Sneaking;
    }

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
    {
        if (ToMotionKey(e.Key) is not { } key) return;

        _motion.OnKeyUp(key);
        if (key == PetMotionKey.Shift) PetPreview.Sneaking = _motion.Sneaking;
    }

    /// <summary>Avalonia 的键映射到物理内核的键。不关心的键回 <c>null</c>。</summary>
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

    public void SetSimulatedKey(string key, bool down)
    {
        _motion.SetSimulatedKey(key, down);
        if (key.Equals("shift", StringComparison.OrdinalIgnoreCase)) PetPreview.Sneaking = _motion.Sneaking;
    }

    private void UpdateSneakMenuIcon()
    {
        if (MenuSneakIcon != null)
        {
            MenuSneakIcon.IsVisible = _motion.ManualSneakToggle;
        }
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

    private void OnRootPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed)
        {
            if (_motion.Mode == PetInteractionMode.NavigateToCoord)
            {
                ClearNavigation();
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && GetCursorPos(out _leftPressCursor))
            {
                _isLeftPressed = true;
                _isLeftDragging = false;
                PetPreview.IsDangling = true; // 抓握瞬间立即触发挣扎摇晃头部动画！
                e.Pointer.Capture(sender as Control ?? this);
                e.Handled = true;
            }
        }
        else if (props.IsRightButtonPressed)
        {
            _isRightPressed = true;
            _isRightRotating = false;
            _rightPressPoint = e.GetPosition(this);
            _lastRightPoint = _rightPressPoint;
            e.Pointer.Capture(sender as Control ?? this);
            e.Handled = true;
        }
    }

    private void OnRootPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isLeftPressed)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && GetCursorPos(out var cur))
            {
                double dist = Math.Sqrt(Math.Pow(cur.X - _leftPressCursor.X, 2) + Math.Pow(cur.Y - _leftPressCursor.Y, 2));
                if (dist > 3.0 || _isLeftDragging)
                {
                    if (!_isLeftDragging)
                    {
                        _isLeftDragging = true;
                        PetPreview.IsDangling = true;
                        // 拖拽时变成拖拽手势
                        Cursor = new Cursor(StandardCursorType.SizeAll);
                        if (RootPanel != null) RootPanel.Cursor = new Cursor(StandardCursorType.SizeAll);
                    }

                    // 小人双手精确对齐抓握鼠标箭头
                    double scaling = RenderScaling > 0 ? RenderScaling : 1.0;
                    double gripDipX = 80.0 * _currentScale;
                    double gripDipY = (22.0 + PetPreview.StageOffsetY) * _currentScale;

                    int gripPixelX = (int)Math.Round(gripDipX * scaling);
                    int gripPixelY = (int)Math.Round(gripDipY * scaling);

                    int cursorGripX = cur.X;
                    int cursorGripY = cur.Y + (int)Math.Round(6.0 * scaling);

                    Position = new PixelPoint(cursorGripX - gripPixelX, cursorGripY - gripPixelY);
                }
            }
            e.Handled = true;
        }
        else if (_isRightPressed)
        {
            var curPos = e.GetPosition(this);
            double totalDist = Math.Sqrt(Math.Pow(curPos.X - _rightPressPoint.X, 2) + Math.Pow(curPos.Y - _rightPressPoint.Y, 2));
            if (totalDist > 6.0)
            {
                _isRightRotating = true;
                double dx = curPos.X - _lastRightPoint.X;
                _lastRightPoint = curPos;
                PetPreview.RotateModel((float)(dx * 0.7));
            }
            e.Handled = true;
        }
    }

    public void OpenPetContextMenu()
    {
        PetContextMenu?.Open(RootPanel);
    }

    /// <summary>Closes the pet context menu.</summary>
    public void ClosePetContextMenu() => PetContextMenu?.Close();

    /// <summary>Whether the pet context menu is currently open.</summary>
    public bool IsPetContextMenuOpen => PetContextMenu?.IsOpen ?? false;

    private void OnRootPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_isLeftPressed)
        {
            _isLeftPressed = false;
            PetPreview.IsDangling = false;
            if (_isLeftDragging)
            {
                _isLeftDragging = false;
                // 恢复为默认手势
                Cursor = new Cursor(StandardCursorType.Hand);
                if (RootPanel != null) RootPanel.Cursor = new Cursor(StandardCursorType.Hand);

                EndDragOnWindow();
            }
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        if (_isRightPressed)
        {
            _isRightPressed = false;
            e.Pointer.Capture(null);

            if (_isRightRotating)
            {
                _isRightRotating = false;
                e.Handled = true;
            }
            else
            {
                // 原地单击右键：打开上下文菜单
                PetContextMenu?.Open(RootPanel);
                e.Handled = true;
            }
        }
    }

    private void OnRootPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_isLeftPressed)
        {
            _isLeftPressed = false;
            _isLeftDragging = false;
            PetPreview.IsDangling = false;
            Cursor = new Cursor(StandardCursorType.Hand);
            if (RootPanel != null) RootPanel.Cursor = new Cursor(StandardCursorType.Hand);
            EndDragOnWindow();
        }
        if (_isRightPressed)
        {
            _isRightPressed = false;
            _isRightRotating = false;
        }
    }

    private void OnRootContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (!_isRightRotating)
        {
            PetContextMenu?.Open(RootPanel);
        }
        e.Handled = true;
    }

    private void OnRootDoubleTapped(object? sender, TappedEventArgs e)
    {
        _motion.ToggleManualSneak();
        PetPreview.Sneaking = _motion.Sneaking;
        UpdateSneakMenuIcon();
    }

    #endregion

    #region 右键菜单事件

    private void OnToggleTopmostClick(object? sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (MenuTopmost.Icon is MaterialIcon icon)
        {
            icon.IsVisible = Topmost;
        }
    }

    private void SetScale(double scale)
    {
        _currentScale = scale;
        Width = _baseWidth * scale;
        Height = _baseHeight * scale;
    }

    private void OnScale75Click(object? sender, RoutedEventArgs e) => SetScale(0.75);
    private void OnScale100Click(object? sender, RoutedEventArgs e) => SetScale(1.0);
    private void OnScale125Click(object? sender, RoutedEventArgs e) => SetScale(1.25);
    private void OnScale150Click(object? sender, RoutedEventArgs e) => SetScale(1.5);

    private void OnToggleSneakClick(object? sender, RoutedEventArgs e)
    {
        _motion.ToggleManualSneak();
        PetPreview.Sneaking = _motion.Sneaking;
        UpdateSneakMenuIcon();
    }

    /// <summary>松手落地：把地面位置对齐到窗口当前落点（Position 是物理像素，换算成 DIP）。</summary>
    private void EndDragOnWindow()
    {
        var scale = Scaling;
        _motion.EndDrag(Position.X / scale, Position.Y / scale);
        PetPreview.SetJumpOffset(0);
        PetPreview.IsJumping = false;
        // 下蹲状态由心跳里的物理 Shift 轮询 + 按键事件维护，下一帧（16ms）就同步上了。
    }

    /// <summary>调试用：设一个屏幕坐标（物理像素）作为导航目标。</summary>
    public void SetNavigationTarget(int screenX, int screenY)
    {
        var scale = Scaling;
        _motion.SetNavigationTarget(screenX / scale, screenY / scale);
    }

    /// <summary>调试用：追加一个途经点（物理像素）。</summary>
    public void AddNavigationTarget(int screenX, int screenY)
    {
        var scale = Scaling;
        _motion.AddNavigationTarget(screenX / scale, screenY / scale);
    }

    public void ClearNavigation() => _motion.ClearNavigation();

    /// <summary>
    /// 模式变了。菜单图标、切到操控模式时抢焦点、回自由待机时把窗口摆回地面位置 ——
    /// 这些都是 view 的事；丢途经点队列、松开按键那类纯状态清理在 <see cref="PetMotion.SetMode"/> 里做完了。
    /// </summary>
    private void OnMotionModeChanged(PetInteractionMode mode)
    {
        if (MenuFreeModeIcon != null) MenuFreeModeIcon.IsVisible = mode == PetInteractionMode.Free;
        if (MenuControlModeIcon != null) MenuControlModeIcon.IsVisible = mode == PetInteractionMode.Control;
        if (MenuFollowMouseIcon != null) MenuFollowMouseIcon.IsVisible = mode == PetInteractionMode.FollowMouse;

        switch (mode)
        {
            case PetInteractionMode.Control:
                // Position 是物理像素，内核只认 DIP。
                var scale = Scaling;
                _motion.BeginControlMode(Position.X / scale, Position.Y / scale);
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

    private void OnSelectFreeModeClick(object? sender, RoutedEventArgs e)
    {
        CurrentMode = PetInteractionMode.Free;
    }

    private void OnToggleControlModeClick(object? sender, RoutedEventArgs e)
    {
        IsControlMode = !IsControlMode;
    }

    private void OnToggleFollowMouseClick(object? sender, RoutedEventArgs e)
    {
        IsFollowMouseMode = !IsFollowMouseMode;
    }

    private void OnToggleInteractModeClick(object? sender, RoutedEventArgs e)
    {
        IsInteractMode = !IsInteractMode;
    }

    private void OnToggleInteractSwallowClick(object? sender, RoutedEventArgs e)
    {
        InteractSwallowClicks = !InteractSwallowClicks;
    }

    private CoordPickOverlayWindow ShowCoordPickOverlay()
    {
        var overlay = new CoordPickOverlayWindow((targetPoint, isContinuous) =>
        {
            AddNavigationTarget(targetPoint.X, targetPoint.Y);
        });
        overlay.Show();
        return overlay;
    }

    private void OnPickCoordClick(object? sender, RoutedEventArgs e) => ShowCoordPickOverlay();

    private void OnResetRotationClick(object? sender, RoutedEventArgs e)
    {
        PetPreview.ResetRotation();
    }

    private async void OnChangeNameClick(object? sender, RoutedEventArgs e)
    {
        string current = _host?.CustomName ?? PetPreview.PlayerName ?? "";
        var newName = await PromptPetNameDialog.ShowAsync(this, current);
        if (newName != null)
        {
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
    }

    private void OnResetNameToInheritedClick(object? sender, RoutedEventArgs e)
    {
        if (_host != null)
        {
            _host.CustomName = null;
            PetPreview.PlayerName = _host.EffectiveName;
            UpdateResetNameMenuState();
        }
    }

    private async void OnPickSkinFileClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择皮肤 PNG",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Minecraft 皮肤") { Patterns = new[] { "*.png" } }
            }
        });

        if (files.Count > 0)
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(files[0].Path.LocalPath);
                PetPreview.ApplySkin(bytes);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PetWindow] Failed to load skin: {ex.Message}");
            }
        }
    }

    private void OnOpenLauncherClick(object? sender, RoutedEventArgs e)
    {
        _host?.OpenLauncher();
    }

    private void OnClosePetClick(object? sender, RoutedEventArgs e)
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
    /// 构造函数里抛的异常会被当成未处理的调度器异常，调试桥就只能回 OK 而看不见错误。</para>
    /// </summary>
    public PromptPetNameDialog OpenNameDialogForDebug(string? shotPath = null)
    {
        string current = _host?.CustomName ?? PetPreview.PlayerName ?? "";
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
        if (Application.Current?.ApplicationLifetime is not
            Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) return;

        var targets = new List<Window>();
        foreach (var window in desktop.Windows)
        {
            if (window is PromptPetNameDialog or CoordPickOverlayWindow) targets.Add(window);
        }

        foreach (var window in targets) window.Close();
    }

    /// <summary>
    /// 把某个窗口的内容离屏渲染成 PNG。用来看"只有点菜单才出得来"的那两个窗口长什么样。
    /// 窗口刚 Show 出来，<c>Bounds</c> 可能还是 0，所以退回用 <c>Width</c>/<c>Height</c> 估。
    /// </summary>
    private static void SaveWindowContent(Window window, string path)
    {
        if (window.Content is not Control content) return;

        var size = content.Bounds.Size;
        if (size.Width <= 0 || size.Height <= 0) size = new Size(window.Width, window.Height);
        if (size.Width <= 0 || size.Height <= 0) return;

        using var rtb = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)),
            new Vector(96, 96));
        rtb.Render(content);
        rtb.Save(path, new PngBitmapEncoderOptions());
    }

    #endregion
}
