using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NegiCraftLauncher.Skin.Avalonia.Controls;

public class MinecraftSkinPreview : Panel
{
    [StructLayout(LayoutKind.Sequential)]
    private struct WinPoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out WinPoint lpPoint);

    public static readonly StyledProperty<string> PlayerNameProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, string>(nameof(PlayerName), string.Empty);

    public string PlayerName
    {
        get => GetValue(PlayerNameProperty);
        set => SetValue(PlayerNameProperty, value);
    }

    public static readonly StyledProperty<bool> SneakingProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(Sneaking));

    public bool Sneaking
    {
        get => GetValue(SneakingProperty);
        set => SetValue(SneakingProperty, value);
    }

    public static readonly StyledProperty<bool> IsGlobalTrackingProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(IsGlobalTracking), false);

    public bool IsGlobalTracking
    {
        get => GetValue(IsGlobalTrackingProperty);
        set => SetValue(IsGlobalTrackingProperty, value);
    }

    public static readonly StyledProperty<bool> IsDanglingProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(IsDangling), false);

    public bool IsDangling
    {
        get => GetValue(IsDanglingProperty);
        set => SetValue(IsDanglingProperty, value);
    }

    public static readonly StyledProperty<bool> IsWalkingProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(IsWalking), false);

    public bool IsWalking
    {
        get => GetValue(IsWalkingProperty);
        set => SetValue(IsWalkingProperty, value);
    }

    public static readonly StyledProperty<bool> IsJumpingProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(IsJumping), false);

    public bool IsJumping
    {
        get => GetValue(IsJumpingProperty);
        set => SetValue(IsJumpingProperty, value);
    }

    public static readonly StyledProperty<bool> IsSprintingProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(IsSprinting), false);

    public bool IsSprinting
    {
        get => GetValue(IsSprintingProperty);
        set => SetValue(IsSprintingProperty, value);
    }

    public static readonly StyledProperty<bool> CanDragRotateProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(CanDragRotate), true);

    public bool CanDragRotate
    {
        get => GetValue(CanDragRotateProperty);
        set => SetValue(CanDragRotateProperty, value);
    }

    public static readonly StyledProperty<MouseButton> RotateButtonProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, MouseButton>(nameof(RotateButton), MouseButton.Left);

    /// <summary>哪只键负责旋转。默认左键（既有宿主的握法）；主页设成右键，好把左键腾给 <see cref="CanDragPosition"/>。与 WPF 侧 <c>SkinPreviewControl.RotateButton</c> 同名同义。</summary>
    public MouseButton RotateButton
    {
        get => GetValue(RotateButtonProperty);
        set => SetValue(RotateButtonProperty, value);
    }

    public static readonly StyledProperty<bool> CanDragPositionProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(CanDragPosition), false);

    /// <summary>左键是否改用来挪位置：控件不动，只把位移报给 <see cref="PositionDragged"/>，摆哪儿、怎么存由宿主定。</summary>
    public bool CanDragPosition
    {
        get => GetValue(CanDragPositionProperty);
        set => SetValue(CanDragPositionProperty, value);
    }

    /// <summary>位置拖动中的增量，单位 DIP（见 <see cref="CanDragPosition"/>）。</summary>
    public event Action<double, double>? PositionDragged;

    public static readonly StyledProperty<bool> HitFootprintOnlyProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(HitFootprintOnly), false);

    /// <summary>
    /// 命中区是否只留身体那一块（<see cref="Footprint"/>）。默认 false —— 整块舞台都吃点击，
    /// 桌宠那 160x320 的舞台就靠它拎起来。主页要 true：小人脚下白留 81 DIP，那块要是还能吃点击，
    /// 摆位绕开「启动游戏」就只是看着没压住 —— 按钮其实被透明的那块盖着。与 WPF 侧同名同义。
    /// </summary>
    public bool HitFootprintOnly
    {
        get => GetValue(HitFootprintOnlyProperty);
        set => SetValue(HitFootprintOnlyProperty, value);
    }

    /// <summary>
    /// 身体在舞台里真正占的那一块：名牌顶（6）到阴影底（160+9），横向就是渲染视口那 110。
    /// 摆位夹取与命中区都按这块算，不按整块舞台。
    /// </summary>
    public Rect Footprint => new(FootInsetLeft, 6 + StageOffsetY, 110, 163);

    /// <summary>舞台左右各空 25（渲染视口只有 110 宽），脚下空 81 —— 这些都没有像素。</summary>
    private const double FootInsetLeft = 25;

    public static readonly StyledProperty<double> StageOffsetYProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, double>(nameof(StageOffsetY), 0.0);

    public double StageOffsetY
    {
        get => GetValue(StageOffsetYProperty);
        set => SetValue(StageOffsetYProperty, value);
    }

    private readonly SkinRenderControl _skinRender;
    private readonly TextBlock _nameText;
    private readonly Border _nametag;
    private readonly Ellipse _shadow;

    /// <summary>唯一那块"能点着"的面板，尺寸 = <see cref="Footprint"/>，只在 <see cref="HitFootprintOnly"/> 开着时亮着。</summary>
    private readonly Rectangle _hitArea = new()
    {
        Fill = Brushes.Transparent,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };
    private readonly TranslateTransform _shadowTransform = new();
    private readonly TranslateTransform _characterTransform = new();
    private readonly TranslateTransform _nametagTransform = new();
    private DispatcherTimer? _globalTrackingTimer;
    private bool _isDragging;
    private bool _isMoving;
    private Point _lastMousePos;

    /// <summary>挪位置时的坐标参考：顶层而不是控件自身 —— 控件跟着光标走时相对坐标会自己缩掉。</summary>
    private Visual? _dragRef;

    private double _currentJumpOffsetY = 0.0;

    public MinecraftSkinPreview()
    {
        Width = 160;
        Height = 250;
        ClipToBounds = false;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);

        // 1. Soft shadow directly beneath character's feet (feet at Y ≈ 164px)
        _shadow = new Ellipse
        {
            Width = 56,
            Height = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 160, 0, 0),
            IsHitTestVisible = false,
            RenderTransform = _shadowTransform,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.0),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.7)
                }
            }
        };

        // 2. Open-source 3D Minecraft Skin Renderer.
        // The renderer fits the model to ~75% of the viewport height, so to match the
        // mockup's 64x128 model inside the 160x250 stage (~51%) we render into a smaller
        // 110x171 viewport centred on the mockup model position (centre 80,100).
        _skinRender = new SkinRenderControl
        {
            Width = 110,
            Height = 171,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0),
            IsHitTestVisible = false,
            RenderTransform = _characterTransform
        };

        // 3. Floating nametag directly above character's head (head top at Y ≈ 36.6px)
        // Jersey 10 draws on a 75/1400 em grid, so 12.5 DIP at RenderScaling 1.5 lands each
        // design pixel on exactly one device pixel. Alias keeps ClearType from smearing it.
        _nameText = new TextBlock
        {
            Text = PlayerName,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("avares://NegiCraftLauncher.Skin.Avalonia/Assets/Fonts#Jersey 10"),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        TextOptions.SetTextRenderingMode(_nameText, TextRenderingMode.Alias);

        _nametag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(107, 0, 0, 0)), // rgba(0,0,0,.42)
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            IsHitTestVisible = false,
            RenderTransform = _nametagTransform,
            Transitions = new Transitions
            {
                new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(160) }
            },
            Child = _nameText
        };

        Children.Add(_hitArea);
        Children.Add(_shadow);
        Children.Add(_skinRender);
        Children.Add(_nametag);

        ApplyHitArea();
    }

    /// <summary>
    /// 把命中面摆到身体那一块。开着 <see cref="HitFootprintOnly"/> 就把整块舞台的
    /// <c>Background</c> 撤掉 —— 透明的 <c>Background</c> 才是"这块元素能被点着"的开关，
    /// 撤掉之后只剩那块矩形吃点击。
    /// </summary>
    private void ApplyHitArea()
    {
        var f = Footprint;
        _hitArea.Width = f.Width;
        _hitArea.Height = f.Height;
        _hitArea.Margin = new Thickness(f.X, f.Y, 0, 0);
        _hitArea.IsVisible = HitFootprintOnly;
        Background = HitFootprintOnly ? null : Brushes.Transparent;
    }

    private TopLevel? _subscribedTopLevel;

    /// <summary>Loads a skin PNG directly, bypassing the username lookup.</summary>
    public void ApplySkin(byte[] pngBytes, MinecraftSkinRender.SkinType? explicitType = null) =>
        _skinRender.SetSkin(pngBytes, explicitType);

    public string? CurrentLoadedUser => _skinRender.CurrentLoadedUser;

    public MinecraftSkinRender.SkinType? LiveSkinType => _skinRender.LiveSkinType;

    public bool? LiveTopLayer => _skinRender.LiveTopLayer;

    public string RenderStats => _skinRender.RenderStats;

    public void SaveSnapshot(string filePath) => _skinRender.SaveSnapshot(filePath);

    public float CurrentYawDeg => _skinRender.CurrentYawDeg;

    public void ResetRotation()
    {
        _skinRender?.RotateModel(-_skinRender.CurrentYawDeg);
    }

    public void RotateModel(float deltaYawDeg)
    {
        _skinRender?.RotateModel(deltaYawDeg);
    }

    public void SetHeadLookAt(float pitchDeg, float yawDeg)
    {
        _skinRender?.SetHeadLookAt(pitchDeg, yawDeg);
    }

    public void TriggerAttack(double? parkAt = null) => _skinRender?.TriggerAttack(parkAt);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (!string.IsNullOrEmpty(PlayerName))
        {
            if (_nameText != null)
            {
                _nameText.Text = PlayerName;
            }
            _skinRender?.LoadFromUsername(PlayerName);
        }
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
        {
            _subscribedTopLevel = topLevel;
            // Use Tunnel strategy so mouse moves across any child controls in the window are intercepted
            topLevel.AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
        UpdateGlobalTrackingState();
        Loaded += (_, _) => UpdateGlobalTrackingState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribedTopLevel != null)
        {
            _subscribedTopLevel.RemoveHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved);
            _subscribedTopLevel = null;
        }
        StopGlobalTracking();
    }

    private void UpdateGlobalTrackingState()
    {
        if (IsGlobalTracking)
        {
            if (_globalTrackingTimer == null)
            {
                _globalTrackingTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
                _globalTrackingTimer.Tick += OnGlobalTrackingTick;
                _globalTrackingTimer.Start();
            }
        }
        else
        {
            StopGlobalTracking();
        }
    }

    private void StopGlobalTracking()
    {
        _globalTrackingTimer?.Stop();
        _globalTrackingTimer = null;
    }

    public void SetJumpOffset(double jumpOffsetY)
    {
        _currentJumpOffsetY = jumpOffsetY;

        // 对齐到整数像素，防止文字栅格化与材质过滤产生亚像素微颤
        double snappedY = Math.Round(jumpOffsetY);

        // 1. 人物与名字在跳跃时内部垂直位移（jumpOffsetY <= 0 向上起跳）
        _characterTransform.Y = snappedY;
        _nametagTransform.Y = snappedY;

        // 2. 阴影完全留在地面：绝对不移动！在视口和桌面物理坐标上绝对静止
        _shadowTransform.Y = 0;

        // 3. 离地高度越高，地面阴影越小越淡（模拟真实 Minecraft 物理光影投影）
        double height = Math.Max(0, -jumpOffsetY);
        double ratio = Math.Clamp(height / 60.0, 0.0, 1.0);
        _shadow.Width = 56.0 * (1.0 - 0.35 * ratio);
        _shadow.Opacity = 1.0 - 0.60 * ratio;
    }

    public string TrackDebugInfo { get; private set; } = "";

    private Point GetHeadScreenPoint()
    {
        if (VisualRoot is Window win)
        {
            double scale = win.RenderScaling > 0 ? win.RenderScaling : 1.0;
            double w = win.Bounds.Width > 0 ? win.Bounds.Width : 160.0;
            double headCenterX = w / 2.0;
            double headCenterY = 52.0 + StageOffsetY;
            return new Point(win.Position.X + headCenterX * scale, win.Position.Y + headCenterY * scale);
        }

        try
        {
            var pt = this.PointToScreen(new Point(Bounds.Width > 0 ? Bounds.Width / 2 : 80, 52 + StageOffsetY));
            return new Point(pt.X, pt.Y);
        }
        catch
        {
            return new Point(0, 0);
        }
    }

    private void OnGlobalTrackingTick(object? sender, EventArgs e)
    {
        if (!IsGlobalTracking || _isDragging) return;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (GetCursorPos(out var winPt))
            {
                var headScreenPt = GetHeadScreenPoint();
                if (headScreenPt.X == 0 && headScreenPt.Y == 0) return;

                // 角色头部在屏幕上的地面基准点（滤除跳跃偏移，保证跳跃时视角平稳不抽搐）
                double anchorHeadY = headScreenPt.Y - _currentJumpOffsetY;
                double dx = winPt.X - headScreenPt.X;
                double dy = winPt.Y - anchorHeadY;

                // 计算鼠标相对于小人正前方的绝对偏角
                float targetLookYaw = (float)(Math.Atan2(dx, 420.0) * (180.0 / Math.PI));
                float targetPitchDeg = (float)Math.Clamp(Math.Atan2(dy, 380.0) * (180.0 / Math.PI), -24.0, 24.0);

                // 计算小人身体当前朝向与目标方位的夹角差
                float currentYaw = CurrentYawDeg;
                float diffYaw = targetLookYaw - currentYaw;
                while (diffYaw > 180f) diffYaw -= 360f;
                while (diffYaw < -180f) diffYaw += 360f;

                TrackDebugInfo = $"cursor=({winPt.X},{winPt.Y}) head=({(int)headScreenPt.X},{(int)headScreenPt.Y}) dx={dx:F0} dy={dy:F0} targetYaw={targetLookYaw:F1} curYaw={currentYaw:F1} diffYaw={diffYaw:F1} isWalking={IsWalking}";

                // 身体自动平滑转身逻辑：
                // 当桌宠静止（非走路、非拖拽）且头部扭角超出舒适范围（|diffYaw| > 25°）时，
                // 驱动身体平滑转向目标，直到身体正对鼠标，彻底解决“比如模型朝向左边了，他头扭不到鼠标位置，他要能自己转过去”！
                if (!IsWalking && !_isDragging)
                {
                    float absDiff = Math.Abs(diffYaw);
                    if (absDiff > 25f)
                    {
                        float deficit = absDiff - 15f;
                        // 步长在 0.8° ~ 4.5° 之间平滑自适应，既灵动自然又无任何剧烈突变
                        float turnStep = Math.Clamp(deficit * 0.12f, 0.8f, 4.5f) * Math.Sign(diffYaw);
                        RotateModel(turnStep);
                    }
                }

                _skinRender.SetHeadLookAt(targetPitchDeg, targetLookYaw);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var props = e.GetCurrentPoint(this).Properties;
        var pressed = props.IsLeftButtonPressed ? MouseButton.Left
                    : props.IsRightButtonPressed ? MouseButton.Right
                    : (MouseButton?)null;
        if (pressed is not { } button) return;

        // 旋转先判：宿主把 RotateButton 也设成左键、又开了挪位置时转的要赢 —— 那是所有既有宿主
        // 一直的握法。与 WPF 侧 SkinPreviewControl.BeginDrag 同一条规则。
        var rotates = CanDragRotate && button == RotateButton;
        var moves = !rotates && CanDragPosition && button == MouseButton.Left;
        if (!rotates && !moves) return;

        _isDragging = rotates;
        _isMoving = moves;
        _dragRef = TopLevel.GetTopLevel(this);
        _lastMousePos = moves ? e.GetPosition(_dragRef) : e.GetPosition(this);
        Cursor = new Cursor(rotates ? StandardCursorType.SizeWestEast : StandardCursorType.SizeAll);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
        {
            var pos = e.GetPosition(this);
            double dx = pos.X - _lastMousePos.X;
            _lastMousePos = pos;

            // ONLY horizontal rotation (Yaw) around vertical Y-axis: rotY += dx * 0.6 deg
            _skinRender.RotateModel((float)(dx * 0.6));
            e.Handled = true;
        }
        else if (_isMoving)
        {
            var pos = e.GetPosition(_dragRef);
            PositionDragged?.Invoke(pos.X - _lastMousePos.X, pos.Y - _lastMousePos.Y);
            _lastMousePos = pos;
            e.Handled = true;
        }
        else
        {
            UpdateLookAtFromPointer(e);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isDragging && !_isMoving) return;

        _isDragging = false;
        _isMoving = false;
        _dragRef = null;
        Cursor = new Cursor(StandardCursorType.Hand);
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
        _isMoving = false;
        _dragRef = null;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging && !_isMoving)
        {
            UpdateLookAtFromPointer(e);
        }
    }

    private void UpdateLookAtFromPointer(PointerEventArgs e)
    {
        var topLevel = _subscribedTopLevel ?? TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        // Stage width is 160, head centre is at X ≈ 80, Y ≈ 52 + StageOffsetY in MinecraftSkinPreview
        Point? headInWindow = this.TranslatePoint(new Point(80, 52 + StageOffsetY), topLevel);
        if (!headInWindow.HasValue) return;

        Point mousePos = e.GetPosition(topLevel);

        // Avalonia keeps reporting the cursor position after it leaves the window (and once on
        // activation), which would otherwise leave the head staring at a point off-screen.
        var bounds = topLevel.Bounds;
        if (mousePos.X < 0 || mousePos.Y < 0 || mousePos.X > bounds.Width || mousePos.Y > bounds.Height)
        {
            _skinRender.SetHeadLookAt(0, 0);
            return;
        }

        double dx = mousePos.X - headInWindow.Value.X;
        double dy = mousePos.Y - headInWindow.Value.Y;

        // Target look angles smoothly proportioned to window dimensions (1180x720)
        // dx > 0 (mouse right) -> targetYaw > 0 (head turns right towards mouse)
        // dy > 0 (mouse below) -> targetPitch > 0 (head tilts down towards mouse)
        float targetYawDeg = (float)Math.Clamp(Math.Atan2(dx, 450.0) * (180.0 / Math.PI), -45.0, 45.0);
        float targetPitchDeg = (float)Math.Clamp(Math.Atan2(dy, 350.0) * (180.0 / Math.PI), -24.0, 24.0);

        _skinRender.SetHeadLookAt(targetPitchDeg, targetYawDeg);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PlayerNameProperty && change.NewValue is string newName)
        {
            if (_nameText != null)
            {
                _nameText.Text = newName;
            }
            _skinRender?.LoadFromUsername(newName);
        }
        else if (change.Property == SneakingProperty && change.NewValue is bool sneaking)
        {
            _skinRender.SetSneak(sneaking);
        }
        else if (change.Property == IsGlobalTrackingProperty)
        {
            UpdateGlobalTrackingState();
        }
        else if (change.Property == IsDanglingProperty && change.NewValue is bool isDangling)
        {
            if (_skinRender != null)
            {
                _skinRender.IsDangling = isDangling;
            }
            if (_shadow != null)
            {
                _shadow.Opacity = isDangling ? 0.32 : 1.0;
                _shadow.Width = isDangling ? 36 : 56;
            }
            if (_nametag != null)
            {
                _nametag.Opacity = isDangling ? 0.0 : 1.0;
            }
        }
        else if (change.Property == IsWalkingProperty && change.NewValue is bool isWalking)
        {
            if (_skinRender != null)
            {
                _skinRender.IsWalking = isWalking;
            }
        }
        else if (change.Property == IsJumpingProperty && change.NewValue is bool isJumping)
        {
            if (_skinRender != null)
            {
                _skinRender.IsJumping = isJumping;
            }
        }
        else if (change.Property == IsSprintingProperty && change.NewValue is bool isSprinting)
        {
            if (_skinRender != null)
            {
                _skinRender.IsSprinting = isSprinting;
            }
        }
        else if (change.Property == StageOffsetYProperty && change.NewValue is double stageOffset)
        {
            UpdateStagePositions(stageOffset);
        }
        else if (change.Property == HitFootprintOnlyProperty)
        {
            ApplyHitArea();
        }
    }

    private void UpdateStagePositions(double offset)
    {
        if (_shadow != null) _shadow.Margin = new Thickness(0, 160 + offset, 0, 0);
        if (_skinRender != null) _skinRender.Margin = new Thickness(0, 14 + offset, 0, 0);
        if (_nametag != null) _nametag.Margin = new Thickness(0, 6 + offset, 0, 0);

        ApplyHitArea();   // 身体跟着偏移量往下走，命中面得跟着挪
    }

    public void RequestRender() => _skinRender.RequestNextFrameRendering();
}
