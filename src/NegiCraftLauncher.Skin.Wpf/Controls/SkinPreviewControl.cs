using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;

namespace NegiCraftLauncher.Skin.Wpf.Controls;

/// <summary>
/// WPF 版的 3D 皮肤预览。取代 Avalonia 侧的 <c>MinecraftSkinPreview</c> ——
/// 那边内嵌的是 <c>SkinRenderControl</c>（<c>OpenGlControlBase</c>），
/// 而 WPF 的分层透明窗承载不了子 HWND，GL 控件在这边根本不成立。
///
/// <para>渲染走共享层的 <see cref="SkinRenderSoftware"/>（软件光栅化），结果直接写进
/// <see cref="WriteableBitmap"/>。姿势/动画由 <see cref="SkinPoseDriver"/> 驱动，
/// 与 Avalonia 版逐条对齐。取景尺寸也照抄：画布 160x250，模型 110x171、顶边距 14，
/// 阴影 56x9、顶边距 160，名牌顶边距 6。</para>
///
/// <para>两个宿主共用：启动器主页（160x250，头跟窗口内光标转、可拖动旋转）
/// 与桌宠（160x320 + <see cref="StageOffsetY"/>=75，关掉头跟光标、关掉拖动旋转，
/// 由桌宠窗口自己的物理驱动头部朝向与跳跃位移）。</para>
///
/// <para>红利：这条路是确定性的 —— 同一帧渲染两次逐像素相同，所以 <see cref="SaveSnapshot"/>
/// 不需要 Avalonia 那边"先上膛再开火"的两次往返。</para>
/// </summary>
public sealed class SkinPreviewControl : Grid
{
    private const double StageWidth = 160;
    private const double StageHeight = 250;
    private const double ModelWidth = 110;
    private const double ModelHeight = 171;
    private const double ShadowTop = 160;
    private const double ModelTop = 14;
    private const double NametagTop = 6;

    /// <summary>字体在**本程序集**里，所以要带 <c>;component</c>；写成裸 <c>/Assets/...</c>
    /// 会去入口程序集找，找不到就静默回退成系统字体。</summary>
    private const string FontUri =
        "pack://application:,,,/NegiCraftLauncher.Skin.Wpf;component/Assets/Fonts/#Jersey 10";

    // ==========================================================
    // 依赖属性
    // ==========================================================

    public static readonly DependencyProperty PlayerNameProperty =
        DependencyProperty.Register(
            nameof(PlayerName),
            typeof(string),
            typeof(SkinPreviewControl),
            new PropertyMetadata(string.Empty, OnPlayerNameChanged));

    public string PlayerName
    {
        get => (string)GetValue(PlayerNameProperty);
        set => SetValue(PlayerNameProperty, value);
    }

    public static readonly DependencyProperty SneakingProperty =
        DependencyProperty.Register(
            nameof(Sneaking),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnPoseFlagChanged));

    public bool Sneaking
    {
        get => (bool)GetValue(SneakingProperty);
        set => SetValue(SneakingProperty, value);
    }

    public static readonly DependencyProperty IsDanglingProperty =
        DependencyProperty.Register(
            nameof(IsDangling),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnDanglingChanged));

    /// <summary>被拎起来的挣扎姿势：摇头、胳膊举过头顶、阴影缩小变淡、名牌隐藏。</summary>
    public bool IsDangling
    {
        get => (bool)GetValue(IsDanglingProperty);
        set => SetValue(IsDanglingProperty, value);
    }

    public static readonly DependencyProperty IsWalkingProperty =
        DependencyProperty.Register(
            nameof(IsWalking),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnPoseFlagChanged));

    public bool IsWalking
    {
        get => (bool)GetValue(IsWalkingProperty);
        set => SetValue(IsWalkingProperty, value);
    }

    public static readonly DependencyProperty IsJumpingProperty =
        DependencyProperty.Register(
            nameof(IsJumping),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnPoseFlagChanged));

    public bool IsJumping
    {
        get => (bool)GetValue(IsJumpingProperty);
        set => SetValue(IsJumpingProperty, value);
    }

    public static readonly DependencyProperty IsSprintingProperty =
        DependencyProperty.Register(
            nameof(IsSprinting),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnPoseFlagChanged));

    public bool IsSprinting
    {
        get => (bool)GetValue(IsSprintingProperty);
        set => SetValue(IsSprintingProperty, value);
    }

    public static readonly DependencyProperty StageOffsetYProperty =
        DependencyProperty.Register(
            nameof(StageOffsetY),
            typeof(double),
            typeof(SkinPreviewControl),
            new PropertyMetadata(0.0, OnStageOffsetChanged));

    /// <summary>整台"舞台"（阴影/模型/名牌）往下挪多少。桌宠用它把小人压到窗口底部。</summary>
    public double StageOffsetY
    {
        get => (double)GetValue(StageOffsetYProperty);
        set => SetValue(StageOffsetYProperty, value);
    }

    public static readonly DependencyProperty CanDragRotateProperty =
        DependencyProperty.Register(
            nameof(CanDragRotate),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(true));

    /// <summary>左键拖动是否绕竖轴旋转。桌宠要 false —— 那边左键是"拎起来"。</summary>
    public bool CanDragRotate
    {
        get => (bool)GetValue(CanDragRotateProperty);
        set => SetValue(CanDragRotateProperty, value);
    }

    public static readonly DependencyProperty HeadFollowsHostMouseProperty =
        DependencyProperty.Register(
            nameof(HeadFollowsHostMouse),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(true));

    /// <summary>
    /// 头是否跟着**窗口内**的光标转。启动器主页要 true；
    /// 桌宠要 false —— 它由 <c>PetWindow.UpdateMouseLookAndBodyTurn</c> 按**全局**光标
    /// 每 16ms 算一次，两条路一起开会互相打架。
    /// </summary>
    public bool HeadFollowsHostMouse
    {
        get => (bool)GetValue(HeadFollowsHostMouseProperty);
        set => SetValue(HeadFollowsHostMouseProperty, value);
    }

    // ==========================================================
    // 可视树
    // ==========================================================

    private readonly SkinRenderSoftware _renderer = new();
    private readonly SkinPoseDriver _pose;
    private readonly Image _model;
    private readonly Ellipse _shadow;
    private readonly Border _nametag;
    private readonly TextBlock _nameText;

    private readonly TranslateTransform _modelShift = new();
    private readonly TranslateTransform _nametagShift = new();
    private readonly TranslateTransform _shadowShift = new();

    private WriteableBitmap? _bitmap;
    private PixelBuffer? _buffer;
    private int _pixelWidth;
    private int _pixelHeight;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    private DateTime _lastFrame;
    private double _lastFrameMs;
    private bool _loopAttached;
    private bool _dragging;
    private Point _lastMouse;
    private Window? _hostWindow;
    private string _loadedUser = string.Empty;

    public SkinPreviewControl()
    {
        _renderer.EnableTop = true;
        _renderer.SampleScale = 2;
        _pose = new SkinPoseDriver(_renderer);

        Width = StageWidth;
        Height = StageHeight;
        ClipToBounds = false;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        // 1. 脚底下的柔和阴影（脚在 Y≈164px）。
        _shadow = new Ellipse
        {
            Width = 56,
            Height = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, ShadowTop, 0, 0),
            IsHitTestVisible = false,
            RenderTransform = _shadowShift,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.0),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.7),
                },
            },
        };

        // 2. 软件光栅化的模型位图。
        // 渲染器把模型适配到视口高度的约 75%，所以想在 160x250 的舞台上得到设计稿里
        // 那种"64x128 的模型占 51%"的比例，就得渲染进一个更小的 110x171 视口。
        _model = new Image
        {
            Width = ModelWidth,
            Height = ModelHeight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, ModelTop, 0, 0),
            IsHitTestVisible = false,
            Stretch = Stretch.Fill,
            RenderTransform = _modelShift,
        };

        // 最近邻：像素画放大要硬边，不能让 WPF 插值糊掉。
        RenderOptions.SetBitmapScalingMode(_model, BitmapScalingMode.NearestNeighbor);

        // 3. 头顶浮动的名牌（头顶在 Y≈36.6px）。
        _nameText = new TextBlock
        {
            Text = PlayerName,
            Foreground = Brushes.White,
            FontFamily = new FontFamily(new Uri("pack://application:,,,/"), FontUri),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        TextOptions.SetTextRenderingMode(_nameText, TextRenderingMode.Aliased);

        _nametag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(107, 0, 0, 0)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 2, 9, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, NametagTop, 0, 0),
            IsHitTestVisible = false,
            RenderTransform = _nametagShift,
            Child = _nameText,
        };

        Children.Add(_shadow);
        Children.Add(_model);
        Children.Add(_nametag);

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>当前正在渲染的玩家名（诊断用）。</summary>
    public string? CurrentLoadedUser => _loadedUser;

    /// <summary>当前生效的皮肤格式（诊断用）。与 Avalonia 侧 <c>MinecraftSkinPreview.LiveSkinType</c> 同名同义。</summary>
    public MinecraftSkinRender.SkinType LiveSkinType => _renderer.SkinType;

    /// <summary>是否画了第二层覆盖贴图（诊断用）。老皮肤（64x32）应当是 false。</summary>
    public bool LiveTopLayer => _renderer.EnableTop;

    /// <summary>渲染诊断串：视口像素尺寸 + 上一帧耗时（对齐 Avalonia 侧的 <c>RenderStats</c> 字段位置）。</summary>
    public string RenderStats => $"{_pixelWidth}x{_pixelHeight} {_lastFrameMs:F2}ms";

    /// <summary>模型当前朝向（度）。</summary>
    public float CurrentYawDeg => _pose.CurrentYawDeg;

    public void RotateModel(float deltaYawDeg) => _pose.RotateModel(deltaYawDeg);

    public void ResetRotation() => _pose.RotateModel(-_pose.CurrentYawDeg);

    public void SetHeadLookAt(float pitchDeg, float yawDeg) => _pose.SetHeadLookAt(pitchDeg, yawDeg);

    public void TriggerAttack(double? parkAt = null) => _pose.TriggerAttack(parkAt);

    /// <summary>
    /// 跳跃的垂直位移（负值向上）。只动**人物与名牌**，阴影钉死在地面上，
    /// 并按离地高度缩小变淡 —— 与 Avalonia 版 <c>MinecraftSkinPreview.SetJumpOffset</c> 一致。
    /// </summary>
    public void SetJumpOffset(double jumpOffsetY)
    {
        // 对齐到整数像素：否则材质过滤与文字栅格化会产生亚像素微颤。
        var snapped = Math.Round(jumpOffsetY);
        _modelShift.Y = snapped;
        _nametagShift.Y = snapped;
        _shadowShift.Y = 0;

        var height = Math.Max(0, -jumpOffsetY);
        var ratio = Math.Clamp(height / 60.0, 0.0, 1.0);
        _shadow.Width = 56.0 * (1.0 - (0.35 * ratio));
        _shadow.Opacity = 1.0 - (0.60 * ratio);
    }

    /// <summary>直接喂一张皮肤 PNG，跳过用户名查找。</summary>
    public void ApplySkin(byte[] pngBytes)
    {
        var texture = SkinTexture.Decode(pngBytes);
        if (texture is null) return;

        _renderer.SetSkin(texture);
        _pose.Reset();
        RenderFrame();
    }

    /// <summary>
    /// 把当前这一帧按真实像素尺寸写盘。<b>与 Avalonia 版不同，这里不需要先"上膛"</b> ——
    /// 软件渲染是同步且确定性的，调用即出图。
    /// </summary>
    public void SaveSnapshot(string filePath)
    {
        EnsureTarget();
        if (_buffer is null) return;

        RenderFrame();
        File.WriteAllBytes(filePath, PngCodec.Encode(_buffer));
    }

    // ==========================================================
    // 属性变更
    // ==========================================================

    private static void OnPlayerNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        var name = e.NewValue as string ?? string.Empty;
        preview._nameText.Text = name;
        preview.LoadFromUsername(name);
    }

    /// <summary>四个纯姿势开关（蹲/走/跳/跑）都是直接转发给 <see cref="SkinPoseDriver"/>。</summary>
    private static void OnPoseFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        var on = e.NewValue is true;
        if (e.Property == SneakingProperty) preview._pose.Sneaking = on;
        else if (e.Property == IsWalkingProperty) preview._pose.Walking = on;
        else if (e.Property == IsJumpingProperty) preview._pose.Jumping = on;
        else if (e.Property == IsSprintingProperty) preview._pose.Sprinting = on;
    }

    private static void OnDanglingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        var dangling = e.NewValue is true;
        preview._pose.Dangling = dangling;

        // 被拎起来：影子缩到 36 宽并变淡（脚离地了），名牌淡出（腾不出位置）。
        preview._shadow.Width = dangling ? 36 : 56;
        preview._shadow.Opacity = dangling ? 0.32 : 1.0;
        preview._nametag.Opacity = dangling ? 0.0 : 1.0;
    }

    private static void OnStageOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        var offset = e.NewValue is double v ? v : 0.0;
        preview._shadow.Margin = new Thickness(0, ShadowTop + offset, 0, 0);
        preview._model.Margin = new Thickness(0, ModelTop + offset, 0, 0);
        preview._nametag.Margin = new Thickness(0, NametagTop + offset, 0, 0);
    }

    // ==========================================================
    // 渲染循环
    // ==========================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachLoop();
        if (_loadedUser.Length == 0 && !string.IsNullOrWhiteSpace(PlayerName)) LoadFromUsername(PlayerName);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachLoop();

    private void AttachLoop()
    {
        if (_loopAttached) return;
        _loopAttached = true;
        _lastFrame = DateTime.UtcNow;
        CompositionTarget.Rendering += OnRendering;

        if (Window.GetWindow(this) is { } window)
        {
            _hostWindow = window;
            // 头跟着光标转：挂窗口级的 PreviewMouseMove，鼠标在任意子控件上都能收到。
            window.PreviewMouseMove += OnHostMouseMove;
        }
    }

    private void DetachLoop()
    {
        if (!_loopAttached) return;
        _loopAttached = false;
        CompositionTarget.Rendering -= OnRendering;

        if (_hostWindow is not null)
        {
            _hostWindow.PreviewMouseMove -= OnHostMouseMove;
            _hostWindow = null;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // 主页被切走（面板 Collapsed）或窗口最小化时不用画。
        if (!IsVisible || ActualWidth <= 0) return;

        var now = DateTime.UtcNow;
        var dt = (now - _lastFrame).TotalSeconds;
        _lastFrame = now;
        if (dt <= 0) return;
        if (dt > 0.1) dt = 0.1;

        _pose.Update(dt);
        RenderFrame();
    }

    private void EnsureTarget()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        _dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        // 与 Avalonia 的 GL 控件一致：按设备像素渲染（150% 缩放下就是 165x257），
        // 再让 WPF 按 DPI 缩回 110x171 DIP 显示。
        var width = Math.Max(1, (int)Math.Round(ModelWidth * _dpiX));
        var height = Math.Max(1, (int)Math.Round(ModelHeight * _dpiY));
        if (_buffer is not null && _pixelWidth == width && _pixelHeight == height) return;

        _pixelWidth = width;
        _pixelHeight = height;
        _buffer = new PixelBuffer(width, height);
        _bitmap = new WriteableBitmap(width, height, 96 * _dpiX, 96 * _dpiY, PixelFormats.Pbgra32, null);
        _model.Source = _bitmap;
    }

    private void RenderFrame()
    {
        EnsureTarget();
        if (_buffer is null || _bitmap is null) return;
        if (!_renderer.HaveSkin) return;

        _renderer.Width = _pixelWidth;
        _renderer.Height = _pixelHeight;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _renderer.RenderTo(_buffer);
        _lastFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight), _buffer.Pixels, _pixelWidth * 4, 0);
    }

    private void LoadFromUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return;

        _loadedUser = username;
        var requested = username;

        _ = Task.Run(async () =>
        {
            byte[] bytes;
            try
            {
                var data = await SkinRepository.GetOrFetchAsync(requested);
                bytes = data is { Bytes.Length: > 0 }
                    ? data.Bytes
                    : DefaultSkins.Bytes(SkinRepository.IsSlimForPlayerName(requested));
            }
            catch (Exception)
            {
                bytes = DefaultSkins.Bytes(SkinRepository.IsSlimForPlayerName(requested));
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                // 期间又换了名字就丢掉这批。
                if (!string.Equals(_loadedUser, requested, StringComparison.Ordinal)) return;
                ApplySkin(bytes);
            });
        });
    }

    private void OnHostMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _hostWindow is null) return;
        if (!HeadFollowsHostMouse) return;
        if (_pose.Walking) return;

        var headInWindow = TranslatePoint(new Point(StageWidth / 2, 52 + StageOffsetY), _hostWindow);
        var mouse = e.GetPosition(_hostWindow);

        // 鼠标移出窗口时别再盯着窗口外的一个点看。
        if (mouse.X < 0 || mouse.Y < 0 || mouse.X > _hostWindow.ActualWidth || mouse.Y > _hostWindow.ActualHeight)
        {
            _pose.SetHeadLookAt(0, 0);
            return;
        }

        var dx = mouse.X - headInWindow.X;
        var dy = mouse.Y - headInWindow.Y;

        // 角度按窗口尺寸（1180x720）铺开：光标在右 → yaw 为正 → 头往右转。
        var targetYaw = (float)Math.Clamp(Math.Atan2(dx, 450.0) * (180.0 / Math.PI), -45.0, 45.0);
        var targetPitch = (float)Math.Clamp(Math.Atan2(dy, 350.0) * (180.0 / Math.PI), -24.0, 24.0);

        _pose.SetHeadLookAt(targetPitch, targetYaw);
    }

    // ==========================================================
    // 拖动旋转
    // ==========================================================

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!CanDragRotate) return;

        _dragging = true;
        _lastMouse = e.GetPosition(this);
        Cursor = Cursors.SizeWE;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        var pos = e.GetPosition(this);
        var dx = pos.X - _lastMouse.X;
        _lastMouse = pos;

        // 只绕竖直轴转（yaw）：rotY += dx * 0.6 度。
        _pose.RotateModel((float)(dx * 0.6));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging) return;

        _dragging = false;
        Cursor = Cursors.Hand;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_dragging) return;

        _dragging = false;
        Cursor = Cursors.Hand;
    }
}
