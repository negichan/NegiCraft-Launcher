using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MinecraftSkinRender;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;
using NegiCraftLauncher.Skin.Rendering;

namespace NegiCraftLauncher.Skin.Controls;

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
///
/// <para><b><see cref="UseGpu"/>：可选换成 WPF <c>Viewport3D</c> 的硬件后端</b>。
/// 只影响"显示"这一层 —— 姿势数学、阴影、名牌、跳跃位移全都照旧。两条后端的出图已用
/// <c>--gpu</c> 探针逐像素对齐过（真实皮肤平均绝对差 R1.1/G1.5/B1.2）。</para>
///
/// <para>为什么开 GPU 时软件后端<b>也</b>留着：<see cref="SaveSnapshot"/> 与调试桥的
/// <c>pet-skinsnap</c> 都靠它出图 —— WPF 抓不到离屏的 3D 内容（<c>RenderTargetBitmap</c>
/// 走软件管线，抓出来恒为全透明）。软件后端不参与显示、只跟着同一个
/// <see cref="SkinPoseDriver"/> 走，状态与显示的那一份逐帧一致。</para>
/// </summary>
public sealed class SkinPreviewControl : Grid
{
    private const double StageWidth = 160;
    private const double StageHeight = 250;
    private const double ModelWidth = 110;
    private const double ModelHeight = 171;
    private const double ShadowTop = 160;
    private const double ShadowHeight = 9;
    private const double ModelTop = 14;
    private const double NametagTop = 6;

    /// <summary>
    /// 渲染节流：桌面宠物按 <b>60fps</b> 就够了（计划 §9 的性能预算就是"稳定 60fps"）。
    ///
    /// <para><b>为什么必须封顶</b>：<see cref="CompositionTarget.Rendering"/> 的频率是 WPF 合成
    /// 决定的，不保证等于显示器刷新率 —— 软件合成 / 没有 DWM 的环境下实测能到 <b>~250 次/秒</b>。
    /// 不封顶除了白烧 CPU，更糟的是<b>帧节奏会乱</b>：出图比屏幕能显示的还快时，DWM 只能不规则地
    /// 丢帧并帧，画面看着就是一顿一顿。</para>
    ///
    /// <para><b>两条后端都封</b>：GPU 模式（<see cref="UseGpu"/>）以前跳过封顶，理由是"每帧只更新
    /// 十几次矩阵，跟着合成节奏走就好"。实测它跑 186.7 次/秒，而屏是 159Hz ⇒ 正是上面那种丢帧，
    /// 桌宠的窗口位移很顺、人物小动作却卡。所以限速对两条后端一视同仁。</para>
    ///
    /// <para><c>dt</c> 是从墙钟算的，所以节流只改"多久画一次"，不改动画速度。
    /// 显示器刷新率更高时想跟着提，把这个常数改小即可。</para>
    /// </summary>
    private const double MinFrameSeconds = 1.0 / 60.0;

    /// <summary><see cref="MinFrameSeconds"/> 的 TimeSpan 版：封顶那边每出一帧只把截止点推进一格。</summary>
    private static readonly TimeSpan MinFrame = TimeSpan.FromSeconds(MinFrameSeconds);

    /// <summary>字体在**本程序集**里，所以要带 <c>;component</c>；写成裸 <c>/Assets/...</c>
    /// 会去入口程序集找，找不到就静默回退成系统字体。</summary>
    private const string FontUri =
        "pack://application:,,,/NegiCraftLauncher.Skin;component/Assets/Fonts/#Jersey 10";

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

    public static readonly DependencyProperty SwayingProperty =
        DependencyProperty.Register(
            nameof(Swaying),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnPoseFlagChanged));

    /// <summary>
    /// 叉着腰扭胯：双手叉腰 + 两脚分开站的静态姿势，叠上骨盆左右顶 / 起伏 / 脊椎反向补偿的摆动。
    /// 它动的是骨盆 / 胸椎两节总关节，所以四肢是真的被带着走；开着时不会停渲染（本来就该一直动）。
    /// 叉腰那部分要 <see cref="LimbJoints"/> 开着才弯得出肘。
    /// </summary>
    public bool Swaying
    {
        get => (bool)GetValue(SwayingProperty);
        set => SetValue(SwayingProperty, value);
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

    public static readonly DependencyProperty UseGpuProperty =
        DependencyProperty.Register(
            nameof(UseGpu),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnUseGpuChanged));

    /// <summary>
    /// 用 WPF <c>Viewport3D</c> 的硬件后端显示，而不是软件光栅化位图。
    ///
    /// <para>默认 false —— 软件后端是回归基线（确定性、可离屏出图）。GPU 模式是可选加速项，
    /// 桌面宠物上才值得开（启动器主页预览就一个小人，软件后端足够了）。</para>
    ///
    /// <para>切到 true 时才惰性建 <c>Viewport3D</c>：它一旦进了可视树就会让这个窗口参与 3D 合成，
    /// 不用的时候不该付这个代价。</para>
    /// </summary>
    public bool UseGpu
    {
        get => (bool)GetValue(UseGpuProperty);
        set => SetValue(UseGpuProperty, value);
    }

    public static readonly DependencyProperty SpineFlexibleProperty =
        DependencyProperty.Register(
            nameof(SpineFlexible),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnSpineFlexibleChanged));

    /// <summary>
    /// 躯干换成细分网格、按顶点做脊椎形变（扭腰 / 弯腰时上身会拧出弧度，而不是一块盒子原地转）。
    /// 和 <see cref="LimbJoints"/> 一样只影响 WPF 两个后端；关掉时躯干就是原来的整盒。
    /// </summary>
    public bool SpineFlexible
    {
        get => (bool)GetValue(SpineFlexibleProperty);
        set => SetValue(SpineFlexibleProperty, value);
    }

    public static readonly DependencyProperty CanDragRotateProperty =
        DependencyProperty.Register(
            nameof(CanDragRotate),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(true));

    /// <summary>用 <see cref="RotateButton"/> 拖动是否绕竖轴旋转。桌宠要 false —— 那边拖拽是整窗位移。</summary>
    public bool CanDragRotate
    {
        get => (bool)GetValue(CanDragRotateProperty);
        set => SetValue(CanDragRotateProperty, value);
    }

    public static readonly DependencyProperty RotateButtonProperty =
        DependencyProperty.Register(
            nameof(RotateButton),
            typeof(MouseButton),
            typeof(SkinPreviewControl),
            new PropertyMetadata(MouseButton.Left));

    /// <summary>
    /// 哪只键负责旋转。默认左键 —— 桌宠设置窗那个预览一直是这么握的。
    /// 主页把它设成右键，好把左键腾给 <see cref="CanDragPosition"/>。
    /// </summary>
    public MouseButton RotateButton
    {
        get => (MouseButton)GetValue(RotateButtonProperty);
        set => SetValue(RotateButtonProperty, value);
    }

    public static readonly DependencyProperty CanDragPositionProperty =
        DependencyProperty.Register(
            nameof(CanDragPosition),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false));

    /// <summary>
    /// 左键是否改用来挪位置：控件本身不动，只把每一段位移（DIP）从
    /// <see cref="PositionDragged"/> 报给宿主 —— 摆在哪、怎么存是宿主的事
    /// （主页存的是首页可用区的比例）。
    ///
    /// <para>默认 false：桌宠那边左键是"拎起来"，位移归 <c>PetWindow</c> 自己算，
    /// 这里插一脚就成打架了。</para>
    /// </summary>
    public bool CanDragPosition
    {
        get => (bool)GetValue(CanDragPositionProperty);
        set => SetValue(CanDragPositionProperty, value);
    }

    public static readonly DependencyProperty HitFootprintOnlyProperty =
        DependencyProperty.Register(
            nameof(HitFootprintOnly),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnHitFootprintOnlyChanged));

    /// <summary>
    /// 命中区是否只留身体那一块（<see cref="Footprint"/>）。默认 false —— 整块舞台都吃点击，
    /// 桌宠那 160×320 的舞台就靠它拎起来。
    ///
    /// <para>启动器主页要 true：小人脚下白留 81 DIP，那块要是还能吃点击，摆位绕开「启动游戏」
    /// 就只是**看着**没压住 —— 按钮其实被那块透明的空档盖着，点下去没反应。</para>
    /// </summary>
    public bool HitFootprintOnly
    {
        get => (bool)GetValue(HitFootprintOnlyProperty);
        set => SetValue(HitFootprintOnlyProperty, value);
    }

    private static void OnHitFootprintOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((SkinPreviewControl)d).ApplyHitArea();

    /// <summary>
    /// 身体在舞台里真正占的那一块：名牌顶（6）到阴影底（160+9），横向就是渲染视口那 110。
    /// 舞台另外那些是空的 —— 摆位夹取与命中区都按这块算，不按整块舞台。
    /// </summary>
    public Rect Footprint => new(
        (StageWidth - ModelWidth) / 2,
        NametagTop + StageOffsetY,
        ModelWidth,
        ShadowTop + ShadowHeight - NametagTop);

    /// <summary>位置拖动中的增量，单位 DIP（见 <see cref="CanDragPosition"/>）。</summary>
    public event Action<double, double>? PositionDragged;

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

    public static readonly DependencyProperty LimbJointsProperty =
        DependencyProperty.Register(
            nameof(LimbJoints),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnLimbJointsChanged));

    /// <summary>
    /// 把胳膊和腿各切成上下两段（肘 / 膝），这样才弯得出"手叉腰"这类姿势。
    ///
    /// <para>默认 false —— 关掉时几何和位姿矩阵和以前**逐字节一样**，主页预览的像素基准不动。
    /// 只影响 WPF 两个后端；Avalonia 的 OpenGL 后端按名字建 VAO，只认单段四肢。</para>
    /// </summary>
    public bool LimbJoints
    {
        get => (bool)GetValue(LimbJointsProperty);
        set => SetValue(LimbJointsProperty, value);
    }

    public static readonly DependencyProperty LimbFlexibleProperty =
        DependencyProperty.Register(
            nameof(LimbFlexible),
            typeof(bool),
            typeof(SkinPreviewControl),
            new PropertyMetadata(false, OnLimbFlexibleChanged));

    /// <summary>
    /// 四肢整根网格竖切细分并在关节处平滑自由弯曲。
    /// 替代两段式刚性切割，使肘部和膝盖在弯曲时形成自然的连续曲面。
    /// </summary>
    public bool LimbFlexible
    {
        get => (bool)GetValue(LimbFlexibleProperty);
        set => SetValue(LimbFlexibleProperty, value);
    }

    // ==========================================================
    // 可视树
    // ==========================================================

    private readonly SkinRenderSoftware _software = new();

    /// <summary>GPU 后端的几何/贴图来源。惰性建 —— 只有 <see cref="UseGpu"/> 为 true 时才存在。</summary>
    private SkinRenderGpu? _gpu;

    /// <summary>把 <see cref="_gpu"/> 搬进可视树的那个 <c>Viewport3D</c> 宿主。</summary>
    private SkinGpuViewport? _viewport;

    /// <summary>姿势驱动。开 GPU 后它会同时驱动软件与 GPU 两个后端（见 <see cref="EnsureGpuBackend"/>）。</summary>
    private SkinPoseDriver _pose;

    private bool _useGpu;

    /// <summary>
    /// <see cref="StageOffsetY"/> 的本地副本。必须存一份 —— GPU 视口是惰性建的，
    /// 建的时候变更回调早就跑过了，拿不到当时的偏移量（见 <see cref="ApplyStageOffset"/>）。
    /// </summary>
    private double _stageOffsetY;

    /// <summary>最近一次设进来的皮肤。惰性建 GPU 后端时要拿它把两边的贴图补齐。</summary>
    private SkinTexture? _lastTexture;

    private readonly Image _model;
    private readonly Ellipse _shadow;
    private readonly Border _nametag;
    private readonly TextBlock _nameText;

    /// <summary>
    /// 唯一那块"能点着"的面板，尺寸 = <see cref="Footprint"/>。只在
    /// <see cref="HitFootprintOnly"/> 开着时亮着（见 <see cref="ApplyHitArea"/>）。
    /// </summary>
    private readonly Rectangle _hitArea = new()
    {
        Fill = Brushes.Transparent,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

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
    private DateTime _nextDraw;                     // 60fps 封顶用的截止点（余量结转，见 OnRendering）
    private double _lastFrameMs;
    private bool _loopAttached;
    private bool _dragging;
    private bool _moving;
    private Point _lastMouse;

    /// <summary>
    /// 位置拖动的坐标参考窗口。必须拿窗口坐标算增量：拿控件自身坐标的话，控件被拖着走时
    /// 光标相对它的距离会跟着缩，位移喂不饱、手感像掉速。
    /// </summary>
    private Window? _dragRef;
    private Window? _hostWindow;
    private string _loadedUser = string.Empty;

    // ---- 「静止时停渲染」用（计划 §9 的性能预算：idle 占用 ≈ 0）----
    // 姿势那边的脏标记归 SkinPoseDriver 管（旋转 / 转头 / 姿势开关 / 挥击）。
    // 视图这边还有三件事不归它管，各记一份：
    private bool _viewDirty = true;                 // 可见性 / DPI / 加载完成这类"视图层"变化
    private double _lastJumpOffset = double.NaN;    // 跳跃位移（它只是把位图往上挪，不动姿势）
    private long _frames;                           // 真正光栅化过的帧数（诊断 / 验收用）
    private bool _wasVisible;

    public SkinPreviewControl()
    {
        _software.EnableTop = true;
        // 不超采样：这是个像素人，而超采样的 resolve 是把 2x2 个采样**平均**掉
        // （SoftwareRenderer 里那段 Average），等于给每个纹素边缘抹一圈软边 —— 像素画最怕这个。
        // 关掉之后顺带省掉 4 倍光栅量（标准档 1.2ms → 约 0.4ms）。
        _software.SampleScale = 1;
        _pose = new SkinPoseDriver(_software);

        Width = StageWidth;
        Height = StageHeight;
        ClipToBounds = false;
        Background = Brushes.Transparent;
        Cursor = Cursors.Hand;

        // 1. 脚底下的柔和阴影（脚在 Y≈164px）。
        _shadow = new Ellipse
        {
            Width = 56,
            Height = ShadowHeight,
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

        Children.Add(_hitArea);
        Children.Add(_shadow);
        Children.Add(_model);
        Children.Add(_nametag);

        ApplyHitArea();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <summary>
    /// 把命中面摆到身体那一块。开着 <see cref="HitFootprintOnly"/> 就把整块舞台的
    /// <c>Background</c> 撤掉 —— 透明的 <c>Background</c> 才是 WPF 里"这块元素能被点着"的开关，
    /// 撤掉之后只剩那块矩形吃点击，桌宠那边不动它、握法照旧。
    /// </summary>
    private void ApplyHitArea()
    {
        var f = Footprint;
        _hitArea.Width = f.Width;
        _hitArea.Height = f.Height;
        _hitArea.Margin = new Thickness(f.X, f.Y, 0, 0);
        _hitArea.Visibility = HitFootprintOnly ? Visibility.Visible : Visibility.Collapsed;
        Background = HitFootprintOnly ? null : Brushes.Transparent;
    }

    /// <summary>当前正在渲染的玩家名（诊断用）。</summary>
    public string? CurrentLoadedUser => _loadedUser;

    /// <summary>当前生效的皮肤格式（诊断用）。与 Avalonia 侧 <c>MinecraftSkinPreview.LiveSkinType</c> 同名同义。</summary>
    public MinecraftSkinRender.SkinType LiveSkinType => _software.SkinType;

    /// <summary>是否画了第二层覆盖贴图（诊断用）。老皮肤（64x32）应当是 false。</summary>
    public bool LiveTopLayer => _software.EnableTop;

    /// <summary>当前显示后端（诊断用）：<c>software</c> 或 <c>gpu</c>。</summary>
    public string LiveBackend => _useGpu && _viewport is not null ? "gpu" : "software";

    /// <summary>
    /// 渲染诊断串：视口像素尺寸 + 上一帧耗时 + 累计渲染帧数 + 后端。
    /// <c>frames=</c> 是"静止时停渲染"的验收锚点 —— 不动的时候它应当停止增长。
    /// </summary>
    public string RenderStats =>
        $"{_pixelWidth}x{_pixelHeight} {_lastFrameMs:F2}ms frames={_frames} backend={LiveBackend}";

    /// <summary>模型当前朝向（度）。</summary>
    public float CurrentYawDeg => _pose.CurrentYawDeg;

    public void RotateModel(float deltaYawDeg) => _pose.RotateModel(deltaYawDeg);

    public void ResetRotation() => _pose.RotateModel(-_pose.CurrentYawDeg);

    public void SetHeadLookAt(float pitchDeg, float yawDeg) => _pose.SetHeadLookAt(pitchDeg, yawDeg);

    public void TriggerAttack(double? parkAt = null) => _pose.TriggerAttack(parkAt);

    /// <summary>调试用：把扭胯动画定格在某个进度相位（0..1）。传 null 解除定格恢复正常播放。</summary>
    public void ParkSway(double? phase = null)
    {
        _pose.ParkSway(phase);
        RenderFrame();
    }

    /// <summary>
    /// 音频驱动扭胯：宿主每帧喂进来音频能量（0..1）、节拍率（Hz）与连续节拍相位（0..1），分别控制摆动的幅度、速度与软锁相对齐。
    /// 传 null 就回到自走的固定频率与满幅。
    /// </summary>
    public void DriveSwayFromAudio(double? energy, double? beatHz, double? beatPhase = null)
    {
        _pose.SwayEnergy = energy;
        _pose.SwayBeatHz = beatHz;
        _pose.SwayBeatPhase = beatPhase;
    }

    /// <summary>
    /// 调试用：往某个关节的角度上加一份偏移（度），用来把叉腰那组骨架角边看边调。
    /// 见 <see cref="SkinPoseDriver.TweakJoint"/>。
    /// </summary>
    public void TweakJoint(ModelPartType part, float xDeg, float yDeg, float zDeg) =>
        _pose.TweakJoint(part, xDeg, yDeg, zDeg);

    /// <summary>
    /// 跳跃的垂直位移（负值向上）。只动**人物与名牌**，阴影钉死在地面上，
    /// 并按离地高度缩小变淡 —— 与 Avalonia 版 <c>MinecraftSkinPreview.SetJumpOffset</c> 一致。
    /// </summary>
    public void SetJumpOffset(double jumpOffsetY)
    {
        // 对齐到整数像素：否则材质过滤与文字栅格化会产生亚像素微颤。
        var snapped = Math.Round(jumpOffsetY);
        // 只在这一格真的动了时才标脏 —— 桌宠心跳每 16ms 喂一次同样的值，不挡的话
        // "静止时停渲染"就永远生效不了。
        if (snapped != _lastJumpOffset)
        {
            _lastJumpOffset = snapped;
            _viewDirty = true;
        }

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

        _lastTexture = texture;
        _software.SetSkin(texture);
        _gpu?.SetSkin(texture);
        _pose.Reset();
        RenderFrame();
    }

    /// <summary>
    /// 把当前这一帧按真实像素尺寸写盘。<b>与 Avalonia 版不同，这里不需要先"上膛"</b> ——
    /// 软件渲染是同步且确定性的，调用即出图。
    ///
    /// <para><b>GPU 模式下也走软件后端</b>：显示的那一份在显卡上，WPF 抓不到离屏的 3D 内容
    /// （<c>RenderTargetBitmap</c> 走软件管线，抓出来恒为全透明）。而软件后端一直被同一个
    /// <see cref="SkinPoseDriver"/> 驱动着，姿势状态与显示的那一份逐帧一致 ——
    /// 所以这张图和屏幕上看到的模型是同一个姿势。</para>
    /// </summary>
    public void SaveSnapshot(string filePath)
    {
        EnsureTarget();
        if (_buffer is null) return;

        _software.Width = _pixelWidth;
        _software.Height = _pixelHeight;
        _software.RenderTo(_buffer);

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

    /// <summary>几个纯姿势开关（蹲/走/跳/跑/叉腰/摆动）都是直接转发给 <see cref="SkinPoseDriver"/>。</summary>
    private static void OnPoseFlagChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        var on = e.NewValue is true;
        if (e.Property == SneakingProperty) preview._pose.Sneaking = on;
        else if (e.Property == IsWalkingProperty) preview._pose.Walking = on;
        else if (e.Property == IsJumpingProperty) preview._pose.Jumping = on;
        else if (e.Property == IsSprintingProperty) preview._pose.Sprinting = on;
        else if (e.Property == SwayingProperty) preview._pose.Swaying = on;
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

    /// <summary>
    /// 分段开关要铺到**两个**后端上，而且 GPU 那份是惰性建的 —— 建的时候要按当前值补一次，
    /// 否则会出现"软件那份切了、GPU 那份没切"，而屏幕上看到的是 GPU 那份。
    /// </summary>
    private static void OnLimbJointsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        var jointed = e.NewValue is true;
        preview._software.LimbJoints = jointed;

        if (preview._gpu is { } gpu) gpu.LimbJoints = jointed;

        preview._viewDirty = true;
    }

    private static void OnSpineFlexibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        var flexible = e.NewValue is true;
        preview._software.SpineFlexible = flexible;

        if (preview._gpu is { } gpu) gpu.SpineFlexible = flexible;

        preview._viewDirty = true;
    }

    private static void OnLimbFlexibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        var flexible = e.NewValue is true;
        preview._software.LimbFlexible = flexible;

        if (preview._gpu is { } gpu) gpu.LimbFlexible = flexible;

        preview._viewDirty = true;
    }

    private static void OnStageOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        preview._stageOffsetY = e.NewValue is double v ? v : 0.0;
        preview.ApplyStageOffset();
    }

    /// <summary>
    /// 把"整台舞台往下挪多少"铺到三个可视元素 + GPU 视口上。
    ///
    /// <para><b>GPU 视口必须一起挪</b>：它是**惰性**建的，<see cref="EnsureGpuBackend"/> 跑的时候
    /// <see cref="StageOffsetY"/> 早就设好了（桌宠在 XAML 里写死 75），不会再触发一次变更回调。
    /// 漏掉的后果是 GPU 模式下整台模型比软件模式**高 75 像素** —— 看着像"GPU 渲染是坏的"，
    /// 其实是取景位置错了（踩过：桌宠两模式的抓屏差 29%）。</para>
    /// </summary>
    private void ApplyStageOffset()
    {
        _shadow.Margin = new Thickness(0, ShadowTop + _stageOffsetY, 0, 0);
        _model.Margin = new Thickness(0, ModelTop + _stageOffsetY, 0, 0);
        _nametag.Margin = new Thickness(0, NametagTop + _stageOffsetY, 0, 0);

        if (_viewport is not null)
        {
            _viewport.View.Margin = new Thickness(0, ModelTop + _stageOffsetY, 0, 0);
        }

        ApplyHitArea();   // 身体跟着偏移量往下走，命中面得跟着挪
    }

    // ==========================================================
    // 渲染循环
    // ==========================================================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewDirty = true;
        AttachLoop();
        if (_loadedUser.Length == 0 && !string.IsNullOrWhiteSpace(PlayerName)) LoadFromUsername(PlayerName);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => DetachLoop();

    private void AttachLoop()
    {
        if (_loopAttached) return;
        _loopAttached = true;
        _lastFrame = DateTime.UtcNow;
        _nextDraw = _lastFrame;
        CompositionTarget.Rendering += OnRendering;

        if (Window.GetWindow(this) is { } window)
        {
            _hostWindow = window;
            // 头跟着光标转：挂窗口级的 PreviewMouseMove，鼠标在任意子控件上都能收到。
            window.PreviewMouseMove += OnHostMouseMove;
            // 窗口被拖到缩放率不同的显示器上时位图尺寸要跟着变 —— 静止时也得重画一次。
            window.DpiChanged += OnHostDpiChanged;
            // 桌宠换大小改的就是宿主窗口的尺寸（外层 Viewbox 把它转成缩放）。不补这一刀，
            // 静止时换了大小缓冲还是旧分辨率，要等下一次姿势变化才变清晰。
            window.SizeChanged += OnHostSizeChanged;
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
            _hostWindow.DpiChanged -= OnHostDpiChanged;
            _hostWindow.SizeChanged -= OnHostSizeChanged;
            _hostWindow = null;
        }
    }

    private void OnHostDpiChanged(object sender, DpiChangedEventArgs e) => _viewDirty = true;

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e) => _viewDirty = true;

    private void OnRendering(object? sender, EventArgs e)
    {
        // 主页被切走（面板 Collapsed）或窗口最小化时不用画。
        if (!IsVisible || ActualWidth <= 0)
        {
            _wasVisible = false;
            return;
        }

        // 从"看不见"回到"看得见"要补一帧 —— 否则隐藏期间攒下的变化永远画不出来。
        if (!_wasVisible)
        {
            _wasVisible = true;
            _viewDirty = true;
        }

        // ★ 静止时**不重画**：这是计划 §9 的性能预算那条"idle 占用：静止时 CPU ≈ 0（停渲染）"。
        //   桌面上的桌宠大部分时间是不动的，让它每秒白跑几十次软件光栅化毫无意义。
        //   代价只有一个：待机呼吸会停在当前相位（±1.7°，约 1 像素）。
        //   只要姿势那边有动画（走/跑/跳/蹲/被拎/挥击）或视图这边有变化（跳跃位移、旋转、
        //   转头、换肤、DPI、重新可见），立刻恢复逐帧重画。
        if (!_pose.NeedsRepaint && !_viewDirty) return;

        var now = DateTime.UtcNow;
        var dt = (now - _lastFrame).TotalSeconds;

        // ★ 封顶 60fps（见 MinFrameSeconds）。**脏标记故意不清** —— 这一帧只是"还没到时候"，
        //   不是"不用画"；下一帧一到点就会画出来。
        //   判据要拿"截止点"比，不能拿"距上次真出图多久"比：合成回调本身到达得并不均匀（实测
        //   间隔从 6ms 一直铺到 46ms），拿恰好等于目标周期去比就会把该画的帧判掉 —— 桌宠走路时
        //   数 frames= 只有 43fps。改成截止点每次只推进一格、踩过头的余量结转给下一格之后 58fps。
        //   GPU 后端以前故意跳过这个封顶（"每帧只更新十几次矩阵，跟着合成节奏走就好"），实测那是
        //   错的：它跑到 186.7 次/秒，比这块屏的 159Hz 还快，DWM 只能不规则地丢帧并帧 —— 窗口位置
        //   是顺的，人物的小动作却一顿一顿。两条后端限速到同一个节拍，节奏才是整的。
        if (now < _nextDraw) return;
        _nextDraw += MinFrame;
        // Keep a small overshoot; resetting on every late callback skips the following frame.
        if (now - _nextDraw >= MinFrame) _nextDraw = now + MinFrame;

        _lastFrame = now;
        _viewDirty = false;
        if (dt > 0.1) dt = 0.1;

        _pose.Update(dt);
        RenderFrame();
    }

    private void EnsureTarget()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        _dpiY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        // 缓冲要按"最终在屏幕上占多少设备像素"开，只按 DPI 算不够：桌宠整棵子树套在 Viewbox 里，
        // 换大小只改窗口尺寸 ⇒ 拉伸发生在 Viewbox 那一层，位图会被 resample（特大档 165px 的缓冲
        // 要铺到约 248px，像素画踩成 1px/2px 交替；75% 档又被合并糊掉）。
        // 舞台 DIP → 设备像素 = 到窗口根的变换（把 Viewbox 那层算进来）× DPI。
        var (x, y) = StageToDeviceScale();
        var width = Math.Max(1, (int)Math.Round(ModelWidth * x));
        var height = Math.Max(1, (int)Math.Round(ModelHeight * y));
        if (_buffer is not null && _pixelWidth == width && _pixelHeight == height) return;

        _pixelWidth = width;
        _pixelHeight = height;
        _buffer = new PixelBuffer(width, height);
        // 位图自己的 DPI 跟着一起报，DIP 尺寸才仍然是 110x171：布局不动，只有像素变密。
        _bitmap = new WriteableBitmap(width, height, 96 * x, 96 * y, PixelFormats.Pbgra32, null);
        _model.Source = _bitmap;
    }

    /// <summary>
    /// 一单位"舞台 DIP"等于多少设备像素。没连上布局时退回 DPI（也就是当作没有外层缩放）。
    /// </summary>
    private (double X, double Y) StageToDeviceScale()
    {
        var root = _hostWindow ?? Window.GetWindow(this);
        if (root is null) return (_dpiX, _dpiY);

        try
        {
            // 只取变换的两个轴向量差当缩放：GeneralTransform 拿矩阵要绕，而这里确定没有旋转。
            var t = TransformToVisual(root);
            var o = t.Transform(new Point(0, 0));
            var sx = Math.Abs(t.Transform(new Point(1, 0)).X - o.X) * _dpiX;
            var sy = Math.Abs(t.Transform(new Point(0, 1)).Y - o.Y) * _dpiY;
            return sx > 0 && sy > 0 ? (sx, sy) : (_dpiX, _dpiY);
        }
        catch (InvalidOperationException)
        {
            return (_dpiX, _dpiY);   // 两个 visual 还没连到同一棵树
        }
    }

    private void RenderFrame()
    {
        EnsureTarget();
        if (_pixelWidth <= 0 || _pixelHeight <= 0) return;

        // GPU 模式：真正的光栅化在显卡上，CPU 这边一帧只有十几次矩阵乘法。
        // 投影矩阵的宽高比取自 Width/Height，所以必须和画布同尺寸 —— 否则取景会被拉歪。
        if (_useGpu && _gpu is not null && _viewport is not null)
        {
            if (!_gpu.HaveSkin) return;

            _gpu.Width = _pixelWidth;
            _gpu.Height = _pixelHeight;
            var gpuStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            _viewport.Sync(_gpu);
            _lastFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(gpuStarted).TotalMilliseconds;
            _frames++;
            return;
        }

        if (_buffer is null || _bitmap is null) return;
        if (!_software.HaveSkin) return;

        _software.Width = _pixelWidth;
        _software.Height = _pixelHeight;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        _software.RenderTo(_buffer);
        _lastFrameMs = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        _frames++;

        _bitmap.WritePixels(new Int32Rect(0, 0, _pixelWidth, _pixelHeight), _buffer.Pixels, _pixelWidth * 4, 0);
    }

    // ==========================================================
    // GPU 后端
    // ==========================================================

    private static void OnUseGpuChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not SkinPreviewControl preview) return;

        preview._useGpu = e.NewValue is true;
        if (preview._useGpu) preview.EnsureGpuBackend();

        // 两个可视元素都常驻（建好之后），靠 Visibility 切 —— 这样来回切设置不用重建后端，
        // 姿势 / 朝向也不会丢。
        if (preview._viewport is not null)
        {
            preview._viewport.View.Visibility = preview._useGpu ? Visibility.Visible : Visibility.Collapsed;
        }

        preview._model.Visibility = preview._useGpu ? Visibility.Collapsed : Visibility.Visible;
        preview._viewDirty = true;
    }

    /// <summary>
    /// 惰性建 GPU 后端。建好之后 <see cref="_pose"/> 会同时驱动软件与 GPU 两个后端 ——
    /// 软件那份不参与显示，只服务于 <see cref="SaveSnapshot"/>。
    /// </summary>
    private void EnsureGpuBackend()
    {
        if (_gpu is not null) return;

        _gpu = new SkinRenderGpu
        {
            // WPF 的 3D 贴图采样固定是双线性，而 RenderOptions.BitmapScalingMode 只管 2D 画刷、
            // 管不到 Media3D —— 所以只能把纹素先放大到"一个烘焙纹素 ≪ 一个屏幕像素"，让插值来不及
            // 在块内抹出中间色。默认那档 2 倍不够：标准档一个原始纹素约 2.5 设备像素，烘完一个烘焙
            // 纹素还有 1.25 像素宽 ⇒ 渐变带正好铺满一个像素，看着就是"糊一层 + 方块交界一条细线"。
            // 8 倍：64x64 → 512x512（1 MB / 层），渐变带缩到 0.3 像素，肉眼没了。
            TextureUpscale = 8,
        };
        _viewport = new SkinGpuViewport();

        var view = _viewport.View;
        view.Width = ModelWidth;
        view.Height = ModelHeight;
        view.HorizontalAlignment = HorizontalAlignment.Center;
        view.VerticalAlignment = VerticalAlignment.Top;
        // 与软件后端那张位图同一个槽位 —— 含 StageOffsetY（桌宠是 75）。
        view.Margin = new Thickness(0, ModelTop + _stageOffsetY, 0, 0);
        // 与模型位图共用同一个位移变换 —— 跳跃时"人物往上飘、影子留在地上"的效果两边一致。
        view.RenderTransform = _modelShift;
        view.IsHitTestVisible = false;
        view.Visibility = Visibility.Collapsed;

        // 阴影(0) / 位图(1) / 名牌(2) —— 插在中间，与位图同一个槽位。
        Children.Insert(1, view);

        _pose = new SkinPoseDriver(_software, _gpu);

        // 惰性建的时候补一次当前值 —— 变更回调早就跑过了，不补这份会一直是默认的 false。
        _gpu.LimbJoints = LimbJoints;
        _gpu.SpineFlexible = SpineFlexible;
        _gpu.LimbFlexible = LimbFlexible;

        if (_lastTexture is not null)
        {
            _gpu.SetSkin(_lastTexture);
            _pose.Reset();
        }
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
        if (_dragging || _moving || _hostWindow is null) return;
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
    // 拖动：旋转（<see cref="RotateButton"/>）与挪位置（左键，见 <see cref="CanDragPosition"/>）
    // ==========================================================

    private void BeginDrag(MouseButton button, MouseEventArgs e)
    {
        // 旋转先判：万一宿主把 RotateButton 也设成左键、又开了挪位置，转的要赢 ——
        // 那是所有既有宿主一直的握法，挪位置是主页新加的那一路。
        var rotates = CanDragRotate && button == RotateButton;
        var moves = !rotates && CanDragPosition && button == MouseButton.Left;
        if (!rotates && !moves) return;

        _dragging = rotates;
        _moving = moves;
        _dragRef = Window.GetWindow(this);
        _lastMouse = moves ? e.GetPosition(_dragRef) : e.GetPosition(this);
        Cursor = rotates ? Cursors.SizeWE : Cursors.SizeAll;
        CaptureMouse();
    }

    private bool EndDrag()
    {
        if (!_dragging && !_moving) return false;

        _dragging = false;
        _moving = false;
        _dragRef = null;
        Cursor = Cursors.Hand;
        ReleaseMouseCapture();
        return true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        BeginDrag(MouseButton.Left, e);
        if (_dragging || _moving) e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        BeginDrag(MouseButton.Right, e);
        if (_dragging || _moving) e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging && !_moving) return;

        var pos = _moving ? e.GetPosition(_dragRef) : e.GetPosition(this);
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;
        _lastMouse = pos;

        if (_dragging)
        {
            // 只绕竖直轴转（yaw）：rotY += dx * 0.6 度。
            _pose.RotateModel((float)(dx * 0.6));
        }
        else
        {
            PositionDragged?.Invoke(dx, dy);
        }

        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (EndDrag()) e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (EndDrag()) e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        EndDrag();
    }
}
