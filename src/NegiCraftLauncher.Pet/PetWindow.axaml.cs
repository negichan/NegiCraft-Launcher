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

    public enum PetInteractionMode
    {
        Free,
        Control,
        FollowMouse,
        NavigateToCoord
    }

    private PetInteractionMode _currentMode = PetInteractionMode.Free;
    private readonly List<PixelPoint> _navQueue = new();
    private PixelPoint _currentNavTarget;
    private bool _hasNavTarget;

    public int RemainingWaypointCount => (_hasNavTarget ? 1 : 0) + _navQueue.Count;

    /// <summary>
    /// The host this pet was built with, or null for a bare pet with no host at all. Exposed for
    /// tooling (the debug bridges read the effective/custom name and the account list through it).
    /// </summary>
    public IPetHost? Host => _host;

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

        // Register before the window is ever shown: the backend applies these styles on the next
        // property update, and Show() always triggers one. See PetShellStyle for why.
        Services.PetShellStyle.ApplyToolWindow(this);
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
            if (dt < 0.002) return;
            if (dt > 0.04) dt = 0.04;

            // 被抓握或在空中拖拽时，以挣扎晃头动画为先
            if (_isLeftPressed || _isLeftDragging)
            {
                _isJumping = false;
                _jumpOffsetY = 0;
                _jumpVelocityY = 0;
                _groundPosX = Position.X;
                _groundPosY = Position.Y;
                PetPreview.IsWalking = false;
                PetPreview.IsJumping = false;
                PetPreview.IsSprinting = false;
                return;
            }

            if (_groundPosX == 0 && _groundPosY == 0 && (Position.X != 0 || Position.Y != 0))
            {
                _groundPosX = Position.X;
                _groundPosY = Position.Y;
            }

            // 1. Shift 下蹲状态检测
            bool isDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
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

            // 3. 鼠标视线追踪与身体自动平滑转向（比如模型朝向左边，头扭不到鼠标位置时，身体自动转过去）
            UpdateMouseLookAndBodyTurn(dt);
        }
        catch (Exception ex)
        {
            TrackDebugInfo = "EX: " + ex.GetType().Name + ": " + ex.Message;
        }
    }

    public string TrackDebugInfo { get; private set; } = "";
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

    private void UpdateMouseLookAndBodyTurn(double dt)
    {
        if (_isLeftDragging || _isLeftPressed)
        {
            TrackDebugInfo = "LOOK_DRAGGING";
            return;
        }

        WinPoint cur;
        if (_hasVirtualCursor)
        {
            cur = _virtualCursor;
        }
        else if (GetCursorPos(out var realCur))
        {
            cur = realCur;
            _lastCursorPos = realCur;
        }
        else if (_lastCursorPos.X != -1)
        {
            cur = _lastCursorPos;
        }
        else
        {
            // 兜底为屏幕正前方
            double s = RenderScaling > 0 ? RenderScaling : 1.0;
            cur = new WinPoint { X = (int)(_groundPosX + (Width / 2.0) * s), Y = (int)(_groundPosY - 100.0 * s) };
        }
        {
            double scaling = RenderScaling > 0 ? RenderScaling : 1.0;
            double headCenterX = _groundPosX + (Width / 2.0) * scaling;
            double headCenterY = _groundPosY + (52.0 + PetPreview.StageOffsetY) * _currentScale * scaling;

            double dx = cur.X - headCenterX;
            double dy = cur.Y - headCenterY;

            // 计算光标相对于小人的水平方位角与俯仰角
            float targetLookYaw = (float)(Math.Atan2(dx, 420.0 * scaling) * (180.0 / Math.PI));
            float targetPitchDeg = (float)Math.Clamp(Math.Atan2(dy, 380.0 * scaling) * (180.0 / Math.PI), -24.0, 24.0);

            float currentYaw = PetPreview.CurrentYawDeg;
            float diffYaw = targetLookYaw - currentYaw;
            while (diffYaw > 180f) diffYaw -= 360f;
            while (diffYaw < -180f) diffYaw += 360f;

            TrackDebugInfo = $"cursor=({cur.X},{cur.Y}) head=({(int)headCenterX},{(int)headCenterY}) dx={dx:F0} dy={dy:F0} targetYaw={targetLookYaw:F1} curYaw={currentYaw:F1} diffYaw={diffYaw:F1} isWalking={PetPreview.IsWalking}";

            // 身体自动平滑转身逻辑：
            // 当桌宠静止（非走路、非拖拽）且头部扭角超出舒适范围（|diffYaw| > 25°）时，
            // 身体自动平滑转向鼠标，直到身体正对鼠标，彻底解决“头扭不到鼠标位置，自己转过去”！
            if (!PetPreview.IsWalking)
            {
                float absDiff = Math.Abs(diffYaw);
                if (absDiff > 25f)
                {
                    float deficit = absDiff - 15f;
                    float maxTurn = 260f * (float)dt; // 最大角速度 260 deg/s
                    float turnStep = Math.Clamp(deficit * 7f * (float)dt, -maxTurn, maxTurn) * Math.Sign(diffYaw);
                    PetPreview.RotateModel(turnStep);
                }
            }

            PetPreview.SetHeadLookAt(targetPitchDeg, targetLookYaw);
        }
    }

    private void UpdateControlPhysics(double dt)
    {

        bool wDown = (GetAsyncKeyState(VK_W) & 0x8000) != 0 || _keyW;
        bool aDown = (GetAsyncKeyState(VK_A) & 0x8000) != 0 || _keyA;
        bool sDown = (GetAsyncKeyState(VK_S) & 0x8000) != 0 || _keyS;
        bool dDown = (GetAsyncKeyState(VK_D) & 0x8000) != 0 || _keyD;
        bool spaceDown = (GetAsyncKeyState(VK_SPACE) & 0x8000) != 0 || _keySpace;
        bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 || _keyCtrl;

        double moveX = 0;
        double moveY = 0;
        if (dDown) moveX += 1;
        if (aDown) moveX -= 1;
        if (sDown) moveY += 1;
        if (wDown) moveY -= 1;

        bool isMoving = moveX != 0 || moveY != 0;
        PetPreview.IsWalking = isMoving;

        // 疾跑状态：按住 Ctrl 或双击 W，且不在下蹲
        bool isSprinting = isMoving && (ctrlDown || _isSprintLocked) && !PetPreview.Sneaking;
        PetPreview.IsSprinting = isSprinting;

        double scaling = RenderScaling > 0 ? RenderScaling : 1.0;

        if (isMoving)
        {
            double len = Math.Sqrt(moveX * moveX + moveY * moveY);
            double dirX = moveX / len;
            double dirY = moveY / len;

            // 桌面环境适配速度 (DIP/s):
            // 潜行 (Sneak): ~80 DIP/s
            // 行走 (Walk): ~180 DIP/s
            // 疾跑 (Sprint): ~270 DIP/s
            // 跳跃中移动 (Jump while moving): ~200 DIP/s (行走跳) / ~290 DIP/s (疾跑跳)
            double speed;
            if (PetPreview.Sneaking)
            {
                speed = 80.0 * scaling;
            }
            else if (isSprinting)
            {
                speed = (_isJumping ? 290.0 : 270.0) * scaling;
            }
            else
            {
                speed = (_isJumping ? 200.0 : 180.0) * scaling;
            }

            double dx = dirX * speed * dt;
            double dy = dirY * speed * dt;

            _groundPosX += dx;
            _groundPosY += dy;
            ClampToScreenBounds(scaling);

            // 平滑旋转至前进方向（最大转速 720 deg/s）
            float targetYaw = (float)(Math.Atan2(dirX, dirY) * (180.0 / Math.PI));
            float diff = targetYaw - PetPreview.CurrentYawDeg;
            while (diff > 180f) diff -= 360f;
            while (diff < -180f) diff += 360f;
            float maxTurn = 720f * (float)dt;
            float turnStep = Math.Clamp(diff * 14f * (float)dt, -maxTurn, maxTurn);
            PetPreview.RotateModel(turnStep);
        }

        // Minecraft 风格起跳模拟 (桌面视口适配: 初速度 -460 DIP/s, 重力 2400 DIP/s², 滞空约 0.38s, 高度约 44 DIP)
        if (spaceDown && !_isJumping)
        {
            _isJumping = true;
            _jumpVelocityY = -460.0;
            PetPreview.IsJumping = true;
        }

        if (_isJumping)
        {
            double gravity = 2400.0;
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
        int targetPixelX = (int)Math.Round(_groundPosX);
        int targetPixelY = (int)Math.Round(_groundPosY);
        if (Position.X != targetPixelX || Position.Y != targetPixelY)
        {
            Position = new PixelPoint(targetPixelX, targetPixelY);
        }
    }

    private void UpdateFollowMousePhysics(double dt)
    {
        WinPoint cur;
        if (_hasVirtualCursor)
        {
            cur = _virtualCursor;
        }
        else if (GetCursorPos(out var realCur))
        {
            cur = realCur;
            _lastCursorPos = realCur;
        }
        else if (_lastCursorPos.X != -1)
        {
            cur = _lastCursorPos;
        }
        else
        {
            return;
        }

        double scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        double petFeetX = _groundPosX + (Width / 2.0) * scaling;
        double petFeetY = _groundPosY + (160.0 + PetPreview.StageOffsetY) * _currentScale * scaling;

        double dx = cur.X - petFeetX;
        double dy = cur.Y - petFeetY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // 停靠半径：鼠标在身旁 ~85px 内时停下脚步
        double dockRadius = 85.0 * scaling;

        if (dist <= dockRadius)
        {
            PetPreview.IsWalking = false;
            PetPreview.IsSprinting = false;
            return;
        }

        PetPreview.IsWalking = true;
        bool isSprint = dist > 360.0 * scaling;
        PetPreview.IsSprinting = isSprint;

        double speed = (isSprint ? 270.0 : 180.0) * scaling;
        double dirX = dx / dist;
        double dirY = dy / dist;

        float targetYaw = (float)(Math.Atan2(dirX, dirY) * (180.0 / Math.PI));
        float diff = targetYaw - PetPreview.CurrentYawDeg;
        while (diff > 180f) diff -= 360f;
        while (diff < -180f) diff += 360f;
        float maxTurn = 720f * (float)dt;
        float turnStep = Math.Clamp(diff * 14f * (float)dt, -maxTurn, maxTurn);
        PetPreview.RotateModel(turnStep);

        double moveDist = Math.Min(speed * dt, dist - dockRadius + 2.0);
        _groundPosX += dirX * moveDist;
        _groundPosY += dirY * moveDist;

        ClampToScreenBounds(scaling);
        int targetPixelX = (int)Math.Round(_groundPosX);
        int targetPixelY = (int)Math.Round(_groundPosY);
        if (Position.X != targetPixelX || Position.Y != targetPixelY)
        {
            Position = new PixelPoint(targetPixelX, targetPixelY);
        }
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

        double scaling = RenderScaling > 0 ? RenderScaling : 1.0;
        double petFeetX = _groundPosX + (Width / 2.0) * scaling;
        double petFeetY = _groundPosY + (160.0 + PetPreview.StageOffsetY) * _currentScale * scaling;

        double dx = _currentNavTarget.X - petFeetX;
        double dy = _currentNavTarget.Y - petFeetY;
        double dist = Math.Sqrt(dx * dx + dy * dy);

        // 到达当前航点（8px 内）
        if (dist <= 8.0 * scaling)
        {
            if (_navQueue.Count > 0)
            {
                // 还有后续途经点：出队下一个点继续移动
                _currentNavTarget = _navQueue[0];
                _navQueue.RemoveAt(0);
                dx = _currentNavTarget.X - petFeetX;
                dy = _currentNavTarget.Y - petFeetY;
                dist = Math.Sqrt(dx * dx + dy * dy);
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
        bool isSprint = dist > 350.0 * scaling;
        PetPreview.IsSprinting = isSprint;

        double speed = (isSprint ? 270.0 : 180.0) * scaling;
        double dirX = dx / dist;
        double dirY = dy / dist;

        float targetYaw = (float)(Math.Atan2(dirX, dirY) * (180.0 / Math.PI));
        float diff = targetYaw - PetPreview.CurrentYawDeg;
        while (diff > 180f) diff -= 360f;
        while (diff < -180f) diff += 360f;
        float maxTurn = 720f * (float)dt;
        float turnStep = Math.Clamp(diff * 14f * (float)dt, -maxTurn, maxTurn);
        PetPreview.RotateModel(turnStep);

        double moveDist = Math.Min(speed * dt, dist);
        _groundPosX += dirX * moveDist;
        _groundPosY += dirY * moveDist;

        ClampToScreenBounds(scaling);
        int targetPixelX = (int)Math.Round(_groundPosX);
        int targetPixelY = (int)Math.Round(_groundPosY);
        if (Position.X != targetPixelX || Position.Y != targetPixelY)
        {
            Position = new PixelPoint(targetPixelX, targetPixelY);
        }
    }

    private void ClampToScreenBounds(double scaling)
    {
        var screen = Screens.ScreenFromVisual(this) ?? Screens.ScreenFromPoint(Position) ?? Screens.Primary;
        if (screen != null)
        {
            var workArea = screen.WorkingArea;
            int petWidth = (int)Math.Round(Width * (screen.Scaling > 0 ? screen.Scaling : scaling));
            int petHeight = (int)Math.Round(Height * (screen.Scaling > 0 ? screen.Scaling : scaling));

            int minX = workArea.X;
            int maxX = workArea.X + workArea.Width - petWidth;
            int minY = workArea.Y;
            int maxY = workArea.Y + workArea.Height - petHeight;

            _groundPosX = Math.Clamp(_groundPosX, minX, maxX);
            _groundPosY = Math.Clamp(_groundPosY, minY, maxY);
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
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

    private void OnWindowKeyUp(object? sender, KeyEventArgs e)
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
        bool shouldSneak = _manualSneakToggle || _isKeyShiftHeld || _isPhysicalShiftDown;
        if (PetPreview.Sneaking != shouldSneak)
        {
            PetPreview.Sneaking = shouldSneak;
        }
        UpdateSneakMenuIcon();
    }

    private void UpdateSneakMenuIcon()
    {
        if (MenuSneakIcon != null)
        {
            MenuSneakIcon.IsVisible = _manualSneakToggle;
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
            if (_currentMode == PetInteractionMode.NavigateToCoord)
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

                _groundPosX = Position.X;
                _groundPosY = Position.Y;
                _jumpOffsetY = 0;
                _jumpVelocityY = 0;
                _isJumping = false;
                PetPreview.SetJumpOffset(0);

                // 落地时检查是否处于按 Shift 状态
                UpdateSneakState();
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
            _groundPosX = Position.X;
            _groundPosY = Position.Y;
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
        _manualSneakToggle = !_manualSneakToggle;
        UpdateSneakState();
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
        _manualSneakToggle = !_manualSneakToggle;
        UpdateSneakState();
    }

    public void SetNavigationTarget(int screenX, int screenY)
    {
        _navQueue.Clear();
        _currentNavTarget = new PixelPoint(screenX, screenY);
        _hasNavTarget = true;
        CurrentMode = PetInteractionMode.NavigateToCoord;
    }

    public void AddNavigationTarget(int screenX, int screenY)
    {
        var target = new PixelPoint(screenX, screenY);
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
        if (MenuFreeModeIcon != null) MenuFreeModeIcon.IsVisible = (_currentMode == PetInteractionMode.Free);
        if (MenuControlModeIcon != null) MenuControlModeIcon.IsVisible = (_currentMode == PetInteractionMode.Control);
        if (MenuFollowMouseIcon != null) MenuFollowMouseIcon.IsVisible = (_currentMode == PetInteractionMode.FollowMouse);

        if (_currentMode != PetInteractionMode.NavigateToCoord)
        {
            _navQueue.Clear();
            _hasNavTarget = false;
        }

        if (_currentMode == PetInteractionMode.Control)
        {
            _groundPosX = Position.X;
            _groundPosY = Position.Y;
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
                Position = new PixelPoint((int)Math.Round(_groundPosX), (int)Math.Round(_groundPosY));
            }
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
