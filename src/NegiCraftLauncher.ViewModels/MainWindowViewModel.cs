using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NegiCraftLauncher.Core;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Java;
using NegiCraftLauncher.Core.Launch;
using NegiCraftLauncher.Core.Modrinth;
using NegiCraftLauncher.Core.Net;
using NegiCraftLauncher.Core.Versions;
using NegiCraftLauncher.Core.WallpaperEngine;
using NegiCraftLauncher.Raster;
using SourceKind = NegiCraftLauncher.Core.Settings.DownloadSource;
using GameFolder = NegiCraftLauncher.Core.Settings.GameFolder;
using DetectedRegion = NegiCraftLauncher.Core.Settings.DetectedRegion;

namespace NegiCraftLauncher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    /// <summary>
    /// 方块图标的 key 清单 —— 每个 key 对应 <c>Raster/Assets/block_&lt;key&gt;.png</c>
    /// 一张内嵌的 <b>1024x1024 原图</b>等距渲染图（见 <see cref="BlockAssets"/>）。
    ///
    /// <para>整张表有两个用处：原版实例按 id 散列挑一块；「实例图标」里给用户挑的预设。</para>
    ///
    /// <para><b>挑的都是 40px 下还认得出、且彼此颜色/造型差得开</b>的：土石木做底色，
    /// 带图案的那几块最认得出，矿物块饱和度高、适合拉开实例之间的差异。宁可少几块，
    /// 也别让实例列表出现一排分不清的灰方块。</para>
    ///
    /// <para><b>加一块方块</b>：往 <c>Raster/Assets/</c> 丢一张 <c>block_&lt;key&gt;.png</c>，
    /// 再把 key 加进这张表 + 在 <see cref="BlockNames"/> 里补个中文名
    /// （csproj 用的是 <c>Assets\block_*.png</c> 通配，不用改工程文件）。
    /// </para>
    /// </summary>
    private static readonly string[] BlockIconKeys =
    {
        // 土石木：颜色最收敛的一组，做底色。
        "grass", "dirt", "stone", "cobble", "planks", "log", "sand", "brick",

        // 带「图案」的几块：40px 下最认得出的就是它们。
        "bookshelf", "crafting", "furnace", "tnt",

        // 矿物块：饱和度高，一眼能区分开，适合拉开实例之间的差异。
        "gold", "diamond", "emerald", "redstone", "glowstone",

        // 只给加载器专用的（不进散列池，见 ForgeBlockType）。
        "anvil",
    };

    /// <summary>
    /// 原版实例按 id 散列挑方块时的候选池 = 上面整张表，<b>减掉加载器专用的那几块</b>。
    /// 直接引用它而不是另抄一份，省得两边走神（加一块方块要改两个地方是 bug 的温床）。
    /// </summary>
    private static readonly string[] BlockIcons =
        BlockIconKeys.Where(t => t != ForgeBlockType).ToArray();

    /// <summary>
    /// 方块 key → 中文名，「实例图标」那排预设的 tooltip 用它。
    /// 缺一个名字不会崩（<see cref="BlockDisplayName"/> 会退回 key 本身），但挑图时认不出是哪块 ——
    /// 加方块时顺手补上。
    /// </summary>
    private static readonly Dictionary<string, string> BlockNames = new(StringComparer.Ordinal)
    {
        ["grass"] = "草方块",
        ["dirt"] = "泥土",
        ["stone"] = "石头",
        ["cobble"] = "圆石",
        ["planks"] = "橡木木板",
        ["log"] = "橡木原木",
        ["sand"] = "沙子",
        ["brick"] = "砖块",
        ["bookshelf"] = "书架",
        ["crafting"] = "工作台",
        ["furnace"] = "熔炉",
        ["tnt"] = "TNT",
        ["gold"] = "金块",
        ["diamond"] = "钻石块",
        ["emerald"] = "绿宝石块",
        ["redstone"] = "红石块",
        ["glowstone"] = "萤石",
        ["anvil"] = "铁砧",
    };

    private static string BlockDisplayName(string key) =>
        BlockNames.TryGetValue(key, out var name) ? name : key;
    private readonly Launcher _launcher;
    private readonly ModrinthClient _modrinth = new();
    private ModrinthInstaller _modrinthInstaller = new();
    private readonly Dictionary<string, PixelBuffer> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceModel> _gameResources = new();

    private bool _applyingSettings;
    private CancellationTokenSource? _saveDebounce;
    private CancellationTokenSource? _resourceLoad;
    private CancellationTokenSource? _bannerTimer;
    private TaskCompletionSource<string?>? _dialogTcs;

    /// <summary>The view hides and restores itself; the VM only says when.</summary>
    public event Action? HideWindowRequested;
    public event Action? ShowWindowRequested;
    public event Action? OpenPetRequested;
    public event Action? RecallPetRequested;

    /// <summary>
    /// Raised whenever the light/dark choice changes. The VM does not know how a given UI
    /// framework switches themes, so the view subscribes and applies it.
    /// </summary>
    public event Action<bool>? ThemeChanged;

    [ObservableProperty]
    private bool _isPetActive;

    public string? PetCustomName
    {
        get => _launcher.Settings.PetCustomName;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_launcher.Settings.PetCustomName != trimmed)
            {
                _launcher.Settings.PetCustomName = trimmed;
                PersistSettings();
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectivePetName));
            }
        }
    }

    public string EffectivePetName =>
        !string.IsNullOrWhiteSpace(PetCustomName)
            ? PetCustomName
            : (CurrentAccount?.Name ?? "pingplus");

    /// <summary>
    /// 桌宠是否用 GPU 硬件渲染。设置页与桌宠右键菜单改的是同一个值（走 <c>IPetHost.UseGpu</c>）。
    ///
    /// <para>只有 Windows(WPF) 侧有这条后端；Avalonia 侧读到了也只是忽略，但值照存不误 ——
    /// 两边共用一份 settings.json，不存的话在 mac/Linux 上跑一次就会把 Windows 侧的开关抹掉。</para>
    /// </summary>
    public bool PetUseGpu
    {
        get => _launcher.Settings.PetUseGpu;
        set
        {
            if (_launcher.Settings.PetUseGpu == value) return;

            _launcher.Settings.PetUseGpu = value;
            PersistSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void OpenPet() => OpenPetRequested?.Invoke();

    [RelayCommand]
    private void RecallPet() => RecallPetRequested?.Invoke();

    public MainWindowViewModel(IBitmapDecoder? decoder = null)
    {
        // ⚠️ 必须是构造参数，不能是 init 属性：对象初始化器跑在构造函数**之后**，
        //    而下面的 ApplySettingsToUi 就会去解保存的壁纸 —— 用 init 的话那次解码永远
        //    拿到 null 解码器，用户重启后自选壁纸静默变回内置场景（踩过，两端都中）。
        Decoder = decoder;

        _launcher = new Launcher();
        _launcher.Initialize();

        _bgArt = PixelArt.CreateBackground(_launcher.Settings.IsDark);

        ApplySettingsToUi(_launcher.Settings);
        RefreshAccounts();
        RefreshInstances();
        RefreshJavaOptions();

        _ = LoadGameVersionsAsync();
        StartRegionProbe();
    }

    public string LauncherVersion => _launcher.Version;

    // ==========================================================
    // Page
    // ==========================================================

    [ObservableProperty]
    private string _currentPage = "home";

    public bool IsOnHome => CurrentPage == "home";

    public bool IsInstancesActive => CurrentPage is "instances" or "instance_manage";

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsOnHome));
        OnPropertyChanged(nameof(IsInstancesActive));
        CloseAllPopovers();
    }

    [RelayCommand]
    private void Go(string page)
    {
        CurrentPage = page;

        // 从游戏回来「最近游玩」该变了：进实例页顺手重读一遍模型，
        // 比在启动流程里到处插通知省事（LastPlayed 是 GameLauncher 写的，这边只读）。
        if (page == "instances")
        {
            foreach (var i in Instances) i.Refresh();
        }
    }

    // ==========================================================
    // Background & appearance
    // ==========================================================

    /// <summary>The generated pixel scene, platform-neutral pixels. Null once a photo is picked.</summary>
    [ObservableProperty]
    private PixelBuffer? _bgArt;

    // Drives which background is shown: the generated pixel scene is stretched to fill, a picked
    // photo keeps its own aspect and is cropped instead. The photo itself is decoded by the view,
    // so the VM stays free of any image codec.
    [ObservableProperty]
    private string? _customBackgroundPath;

    public bool IsCustomBackground => CustomBackgroundPath is not null;

    /// <summary>
    ///     背景是否被用户改过（选了图或选了视频）。"调整 / 恢复默认"两个按钮看的是它 ——
    ///     只按 <see cref="IsCustomBackground" /> 的话，换成视频壁纸后按钮会整排消失。
    /// </summary>
    public bool HasCustomBackground => IsCustomBackground || IsVideoBackground;

    partial void OnCustomBackgroundPathChanged(string? value)
    {
        // 背景图与视频壁纸互斥：后写的赢。这里只在"真的选了图"时清视频，
        // 清空（value is null）不触发，否则 ApplySettingsToUi 读回设置时会互相清空。
        if (value is not null && VideoBackgroundPath is not null) VideoBackgroundPath = null;

        OnPropertyChanged(nameof(IsCustomBackground));
        OnPropertyChanged(nameof(HasCustomBackground));
        RaiseFramingVisibility();
        PersistSettings();

        // 自选壁纸的像素由共享层持有（解码 + 调色都在那儿做），这里只是通知它"图换了"。
        RequestBackgroundDecode();
    }

    /// <summary>
    /// 视频壁纸的文件路径。<b>VM 只持有路径，不持有任何播放器</b> ——
    /// <c>MediaPlayer</c> 是 WPF 类型，而本工程被 Avalonia 侧共用。
    /// 视图订阅这个属性，自己去建播放器和画刷（见 App 的 <c>Media/VideoBackgroundController</c>）。
    /// </summary>
    [ObservableProperty]
    private string? _videoBackgroundPath;

    public bool IsVideoBackground => VideoBackgroundPath is not null;

    partial void OnVideoBackgroundPathChanged(string? value)
    {
        // 视频优先：一旦有视频就把背景图让出来（见 LauncherSettings.VideoBackgroundPath 的注释）。
        if (value is not null && CustomBackgroundPath is not null) CustomBackgroundPath = null;

        OnPropertyChanged(nameof(IsVideoBackground));
        OnPropertyChanged(nameof(HasCustomBackground));
        OnPropertyChanged(nameof(ShowVideoSoundButton));
        RaiseFramingVisibility();
        // 视频没了，喇叭和它底下那条都该消失 —— 按钮的显隐走绑定，浮层得自己收。
        if (value is null) IsVolumePopOpen = false;
        PersistSettings();
    }

    /// <summary>
    /// 是否出声 —— <b>就是"音量不为 0"</b>，不是第二个开关。
    ///
    /// <para>名字留着是因为两端都绑着它（WPF 的喇叭图标、两边那行"出声"复选框），
    /// 绑的还是同一个 bool，只是它现在跟着音量走。设 <c>true</c> 会把音量回成上一次那个非 0 值，
    /// 设 <c>false</c> 就是音量归 0。</para>
    ///
    /// <para>⚠️ 以前这是一个独立的布尔，和音量各管各的：拖到 0 会顺手关掉它，把音量拖回去却没人
    /// 把它打开，于是"音量 40 了还是没声"。一个状态两份记录就一定会分叉 —— 现在只有音量这一份。</para>
    ///
    /// <para>和 <see cref="VideoBackgroundPath"/> 一样，VM 只存值、不碰播放器 ——
    /// 真正把它应用到 <c>MediaPlayer</c> 的是视图侧的 <c>VideoBackgroundController</c>。</para>
    /// </summary>
    public bool VideoBackgroundSound
    {
        get => VideoBackgroundVolume > 0;
        set => VideoBackgroundVolume = value ? _lastAudibleVolume : 0;
    }

    /// <summary>
    /// 视频壁纸的音量，0–100。<b>0 就是静音</b>（默认 0：背景视频是替用户放着的，
    /// 突然出声很唐突，想要声音的用户自己把音量拉起来）。
    /// </summary>
    [ObservableProperty]
    private int _videoBackgroundVolume;

    /// <summary>上一次非 0 的音量；点喇叭"取消静音"回到这一格，而不是硬回 100。</summary>
    private int _lastAudibleVolume = 100;

    partial void OnVideoBackgroundVolumeChanged(int value)
    {
        // 输入框/绑定都可能送来越界值，先夹回去（夹完会再进来一次，那次就走正常分支）。
        var clamped = Math.Clamp(value, 0, 100);
        if (clamped != value)
        {
            VideoBackgroundVolume = clamped;
            return;
        }

        if (clamped > 0) _lastAudibleVolume = clamped;
        // 喇叭图标和那行复选框绑的是 VideoBackgroundSound：它没有自己的 setter 了，
        // 音量一动就得替它喊一声，否则图标会停在上一态。
        OnPropertyChanged(nameof(VideoBackgroundSound));
        PersistSettings();
    }

    /// <summary>
    /// 右上角那个喇叭按钮是否出现。<b>只看"是不是视频背景"</b> —— 背景现在每一页都在，
    /// 音频也是一整条，所以喇叭跟着背景走，不再跟着页走。
    /// </summary>
    public bool ShowVideoSoundButton => IsVideoBackground;

    /// <summary>
    /// 喇叭底下那条竖排音量条是否展开。视图侧靠鼠标进出驱动（悬停喇叭就展开），
    /// 值本身放 VM 只是为了复用 <c>Pop</c> 那套开合动画。
    /// </summary>
    [ObservableProperty]
    private bool _isVolumePopOpen;

    [ObservableProperty]
    private double _bgBlur;

    // -100 (darkest) .. 100 (brightest); half the range maps to 0.5 alpha so the art stays readable
    [ObservableProperty]
    private double _bgBrightness;

    /// <summary>
    /// 背景调节窗口是否开着。它<b>不再是主窗口里的弹层</b>：弹层整个盖在它正在调的那块背景上，
    /// 而且加了取景和调色之后卡片已经顶到窗口底边，所以挪到窗口外面做成一个独立小窗。
    /// </summary>
    [ObservableProperty]
    private bool _isBgTuningOpen;

    /// <summary>
    /// 取景手势层（拖背景平移 + 滚轮缩放）与提示胶囊 / 画中画 / 底部说明是否出现 ——
    /// <b>只在调节窗开着、而且有自选内容可调的时候</b>。
    ///
    /// <para>平常状态背景就该只是一张背景：按住不该能拖，滚轮更不该被"缩放背景"吃掉。
    /// 以前这里还挂一个 <see cref="IsOnHome" />，因为背景只在首页露出；现在背景每页都在，
    /// 取景跟着调节窗走就对了，在哪一页都能取景。</para>
    /// </summary>
    public bool IsFramingVisible => IsBgTuningOpen && HasCustomBackground;

    partial void OnIsBgTuningOpenChanged(bool value) => RaiseFramingVisibility();

    /// <summary>门控是算出来的，任何一路输入变了都要重发（换图、换视频、开关窗）。</summary>
    private void RaiseFramingVisibility() => OnPropertyChanged(nameof(IsFramingVisible));

    /// <summary>调节窗贴在主窗口的哪一侧：false=右。右侧屏幕余量不够时视图会翻到左边。</summary>
    [ObservableProperty]
    private bool _bgTuningLeft;

    /// <summary>
    /// 调节窗的位置（屏幕坐标）。<b>null = 用户没拖过</b>，由视图按"贴主窗口一侧"自己算；
    /// 拖过一次之后就固定下来，下次开机还在老地方。
    /// </summary>
    [ObservableProperty]
    private double? _bgTuningX;

    [ObservableProperty]
    private double? _bgTuningY;

    // 视图拖完窗口只管把位置写回这三个属性，落盘统一在这里做 —— 视图不该知道设置文件的存在。
    // PersistSettings 自带防抖，一次拖动连改三个值也只落盘一次。
    partial void OnBgTuningLeftChanged(bool value) => PersistSettings();
    partial void OnBgTuningXChanged(double? value) => PersistSettings();
    partial void OnBgTuningYChanged(double? value) => PersistSettings();

    // ── 取景与调色 ────────────────────────────────────────────────────────
    // 这六个都是"滑块值"（百分比 / 度），不是像素也不是矩阵系数：换算全在
    // Raster/BackgroundFrame.cs 与 Raster/ColorMatrix.cs 里，两端共用一份。
    //
    // 两条变更通知是**分开的**：取景只改变换（零像素工作），调色要重跑色彩内核。
    // 拖动平移如果也触发调色，就是每帧白烧几百万像素 —— 用户的头號红线是帧率。

    /// <summary>横向平移，单位 = 可平移范围的百分比（−100..100）。</summary>
    [ObservableProperty]
    private double _bgPanX;

    /// <summary>纵向平移，同上。</summary>
    [ObservableProperty]
    private double _bgPanY;

    /// <summary>缩放，100 = 今天的居中裁切铺满。</summary>
    [ObservableProperty]
    private double _bgZoom;

    [ObservableProperty]
    private double _bgContrast;

    [ObservableProperty]
    private double _bgSaturation;

    /// <summary>色相旋转，−180..180 度。</summary>
    [ObservableProperty]
    private double _bgHue;

    /// <summary>取景变了：视图重算变换即可，不用碰像素。</summary>
    public event Action? BackgroundFramingChanged;

    /// <summary>调色变了：需要重跑 <c>ImageGrade</c>。</summary>
    public event Action? BackgroundGradeChanged;

    public double BgDarkOpacity => BgBrightness < 0 ? -BgBrightness / 200 : 0;

    public double BgLightOpacity => BgBrightness > 0 ? BgBrightness / 200 : 0;

    partial void OnBgBrightnessChanged(double value)
    {
        // The number box writes straight through, so snap typed values back into range.
        var clamped = Math.Clamp(value, -100, 100);
        if (!clamped.Equals(value))
        {
            BgBrightness = clamped;
            return;
        }

        OnPropertyChanged(nameof(BgDarkOpacity));
        OnPropertyChanged(nameof(BgLightOpacity));
        PersistSettings();
    }

    partial void OnBgBlurChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, 40);
        if (!clamped.Equals(value))
        {
            BgBlur = clamped;
            return;
        }

        PersistSettings();
    }

    partial void OnBgPanXChanged(double value)
    {
        if (ClampBack(value, -100, 100, out var c)) { BgPanX = c; return; }
        RaiseFraming();
    }

    partial void OnBgPanYChanged(double value)
    {
        if (ClampBack(value, -100, 100, out var c)) { BgPanY = c; return; }
        RaiseFraming();
    }

    partial void OnBgZoomChanged(double value)
    {
        // 下限跟着共享层的 MinZoom 走（100% 仍是 cover 铺满，更小则是"图之外露主题底色"）。
        // NaN 的兜底必须是 100 而不是下限：一个坏值不该把背景缩成一小块。
        if (ClampBack(value, BackgroundFrame.MinZoom, BackgroundFrame.MaxZoom, 100, out var c)) { BgZoom = c; return; }
        RaiseFraming();
    }

    partial void OnBgContrastChanged(double value)
    {
        if (ClampBack(value, -100, 100, out var c)) { BgContrast = c; return; }
        RaiseGrade();
    }

    partial void OnBgSaturationChanged(double value)
    {
        if (ClampBack(value, -100, 100, out var c)) { BgSaturation = c; return; }
        RaiseGrade();
    }

    partial void OnBgHueChanged(double value)
    {
        if (ClampBack(value, -180, 180, out var c)) { BgHue = c; return; }
        RaiseGrade();
    }

    private void RaiseFraming()
    {
        BackgroundFramingChanged?.Invoke();
        PersistSettings();
    }

    private void RaiseGrade()
    {
        Regrade();
        BackgroundGradeChanged?.Invoke();
        PersistSettings();
    }

    /// <summary>
    /// 数字框是直写属性的，所以手输的越界值 / 非有限值要夹回去（沿用 <c>BgBrightness</c> 那套写法）。
    /// 返回 true 表示"值被改过，重新赋值后会有第二次回调，这次就到此为止"。
    /// </summary>
    /// <summary>非有限值的兜底：参数域含 0 就回 0，否则回下限。</summary>
    private static bool ClampBack(double value, double min, double max, out double clamped) =>
        ClampBack(value, min, max, min <= 0 && max >= 0 ? 0 : min, out clamped);

    private static bool ClampBack(double value, double min, double max, double nonFinite, out double clamped)
    {
        clamped = double.IsFinite(value) ? Math.Clamp(value, min, max) : nonFinite;
        return !clamped.Equals(value);
    }

    // ==========================================================
    // 自选壁纸：解码一次 + 调色一次，三个绘制点共用
    // ==========================================================

    /// <summary>视图注入的解码器；没注入（离屏探针、单测）就不出自选壁纸，安静回落到生成场景。</summary>
    public IBitmapDecoder? Decoder { get; }

    /// <summary>
    /// 壁纸解码的长边上限。窗口只有 1180×720，缩到 2560 在 200% 缩放下仍然够锐；
    /// 按 4K 原尺寸解要多养 18MB 像素，每次调色还白烧一倍时间。
    /// </summary>
    public const int BackgroundMaxLongEdge = 2560;

    /// <summary>解码出来的原图（直通 alpha 已在 <c>PixelBuffer.FromBgra</c> 里预乘过）。</summary>
    private PixelBuffer? _bgSource;

    /// <summary>调色输出。可反复就地重写，见 <see cref="Regrade"/> 的单飞说明。</summary>
    private PixelBuffer? _bgOut;

    private PixelBuffer? _bgGraded;
    private CancellationTokenSource? _bgDecode;
    private bool _gradeRunning;
    private bool _gradePending;

    /// <summary>
    /// 自选壁纸最终上屏的像素。主层、侧栏磨砂背板、设置里那张 52×34 缩略图都绑这一个 ⇒
    /// 一张图只解码一次、只调色一次。<b>没调色时它就是原图同一个实例</b>（直通，不复制），
    /// 所以默认参数下像素一字不差。
    /// </summary>
    public PixelBuffer? BgCustomArt => _bgGraded;

    /// <summary>
    /// 最近一次自选壁纸解码的结论。壁纸没出来是"静默回落"的（不能因为一张坏图让启动器崩），
    /// 所以必须留一个能问的地方 —— 调试桥的 <c>state</c> 会把它打出来。
    /// </summary>
    public string BackgroundDecodeState { get; private set; } = "idle";

    /// <summary>换图 / 清图：重新解码。latest-wins，旧请求直接作废。</summary>
    private void RequestBackgroundDecode()
    {
        _bgDecode?.Cancel();
        _gradeRunning = false;
        _gradePending = false;

        var path = CustomBackgroundPath;
        if (path is null)
        {
            BackgroundDecodeState = "cleared";
            _bgSource = null;
            PublishGraded(null);
            return;
        }

        if (Decoder is null)
        {
            BackgroundDecodeState = "no decoder injected";
            _bgSource = null;
            PublishGraded(null);
            return;
        }

        var decoder = Decoder;
        var cts = new CancellationTokenSource();
        _bgDecode = cts;

        // 解码放后台线程：磁盘读 + 一张 4K 照片的解码是几十毫秒量级，
        // 不能挡在"刚选完文件"那一下。
        _ = Task.Run(() =>
        {
            PixelBuffer? decoded = null;
            try
            {
                // 缩放放在这里、不交给解码器：两端必须缩同一档、用同一个算法，
                // 否则同一张图在 WPF 与 Avalonia 上就不是同一份像素（见 IBitmapDecoder）。
                decoded = PixelBuffer.Downsample(decoder.Decode(path), BackgroundMaxLongEdge);
                BackgroundDecodeState = $"ok {decoded.Width}x{decoded.Height}";
            }
            catch (Exception ex)
            {
                decoded = null;
                BackgroundDecodeState = $"{ex.GetType().Name}: {ex.Message}";
            }

            if (cts.IsCancellationRequested) return;
            AppDispatcher.Current.Post(() =>
            {
                if (cts.IsCancellationRequested) return;
                _bgSource = decoded;
                _bgOut = null;   // 尺寸可能变了，输出缓冲重建
                Regrade();
            });
        }, cts.Token);
    }

    private void Regrade()
    {
        var source = _bgSource;
        if (source is null)
        {
            PublishGraded(null);
            return;
        }

        var matrix = ColorMatrix.Build(BgContrast, BgSaturation, BgHue);
        if (ColorMatrix.IsIdentity(matrix))
        {
            // 没调色 ⇒ 直接发布原图那一个实例，一次拷贝都不做。默认参数下像素一字不差。
            PublishGraded(source);
            return;
        }

        if (_gradeRunning)
        {
            _gradePending = true;   // 单飞 + 补跑：拖动时永远收敛到最新一组值，不会排队积压
            return;
        }

        _gradeRunning = true;
        var output = EnsureOutput(source);

        // 反复就地重写同一个输出缓冲是安全的，因为：
        // ① 视图把 PixelBuffer 转成位图时**必定复制**（WPF 的 BitmapSource.Create、
        //    Avalonia 的 Lock + MemoryCopy），已经上屏的那张不受后续改写影响；
        // ② 只有回到 UI 线程才发 PropertyChanged。
        // ⚠️ 哪天给转换器加了缓存 / 共享位图，这条前提就断了，得改成每次新分配。
        //
        // 一次调色只有几毫秒，不做中途取消：算完发现有了更进一步的请求就不发布、直接补跑。
        _ = Task.Run(() =>
        {
            ImageGrade.Grade(source.Pixels, output.Pixels, matrix);
            AppDispatcher.Current.Post(() =>
            {
                _gradeRunning = false;
                if (_gradePending)
                {
                    _gradePending = false;
                    Regrade();
                    return;
                }

                // 期间换了图的话 _bgSource 已经不是我刚算的那一张，新请求会自己发布。
                if (ReferenceEquals(_bgSource, source)) PublishGraded(_bgOut);
            });
        });
    }

    private PixelBuffer EnsureOutput(PixelBuffer source)
    {
        var output = _bgOut;
        if (output is not null && output.Width == source.Width && output.Height == source.Height)
        {
            return output;
        }

        output = new PixelBuffer(source.Width, source.Height);
        _bgOut = output;
        return output;
    }

    private void PublishGraded(PixelBuffer? art)
    {
        if (ReferenceEquals(_bgGraded, art)) return;
        _bgGraded = art;
        OnPropertyChanged(nameof(BgCustomArt));
    }

    [RelayCommand]
    private void SetBackground(string path)
    {
        if (!IsReadableImage(path))
        {
            ShowBanner("无法读取这张图片，请换一张试试。");
            return;
        }

        CustomBackgroundPath = path;

        // The art only shows on home, so jump there and open the tuning popup.
        CurrentPage = "home";
        IsBgTuningOpen = true;
    }

    /// <summary>
    /// The picker filter only checks the extension. PNGs (the only format the shared layer can
    /// decode) are validated up front; other formats are accepted on the file being readable,
    /// and the view falls back to the generated art if it cannot decode them.
    /// </summary>
    private static bool IsReadableImage(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return false;
            if (!PngCodec.LooksLikePng(bytes)) return true;
            PngCodec.Decode(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 视频壁纸的候选扩展名。这里只做粗筛，真正的判据是"能不能播"——
    /// 播不了由视图侧的 <c>MediaFailed</c> 兜底报错。
    /// </summary>
    private static readonly string[] VideoExtensions =
        [".mp4", ".webm", ".avi", ".mkv", ".mov", ".m4v", ".wmv", ".mpg", ".mpeg"];

    [RelayCommand]
    private void SetVideoBackground(string path)
    {
        if (!IsPlayableVideo(path))
        {
            ShowBanner("这不是一个可播放的视频文件。");
            return;
        }

        VideoBackgroundPath = path;

        // 和选图片一样：背景只在首页可见，跳过去并把调节浮层打开。
        CurrentPage = "home";
        IsBgTuningOpen = true;
    }

    private static bool IsPlayableVideo(string path)
    {
        try
        {
            return File.Exists(path)
                   && VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把 Wallpaper Engine 当前选中的壁纸搬过来。
    ///
    /// <para><b>只搬视频壁纸。</b>场景 / 网页 / 应用三类要靠 WE 自己的引擎渲染（场景是实时 3D 管线、
    /// 网页是内嵌浏览器），我们渲染不了；也刻意<b>不做</b>「拿 preview.jpg 当静态背景」的兜底 ——
    /// 那等于把用户的动态壁纸悄悄换成一张截图，比什么都不做更糟。这几类一律只报类型、不动背景。</para>
    /// </summary>
    [RelayCommand]
    private void SyncWallpaperEngine()
    {
        var current = WallpaperEngineLocator.GetCurrent(refresh: true);
        if (current is null)
        {
            ShowBanner("没找到 Wallpaper Engine 的壁纸设置，确认它已经装好并选过壁纸。");
            return;
        }

        var title = string.IsNullOrWhiteSpace(current.Title) ? "当前壁纸" : current.Title!;

        if (current.Kind != WallpaperEngineKind.Video)
        {
            ShowBanner($"Wallpaper Engine 的「{title}」是{DescribeKind(current.Kind)}，目前只支持视频壁纸。");
            return;
        }

        if (current.VideoPath is null)
        {
            ShowBanner($"Wallpaper Engine 的「{title}」是视频，但文件已经不在原处了。");
            return;
        }

        SetVideoBackground(current.VideoPath);
        ShowBanner($"已同步 Wallpaper Engine 的视频壁纸：{title}");
    }

    /// <summary>壁纸类型的中文说法，直接接在「是……」后面用，所以自带「壁纸」二字。</summary>
    private static string DescribeKind(WallpaperEngineKind kind) => kind switch
    {
        WallpaperEngineKind.Scene => "场景壁纸",
        WallpaperEngineKind.Web => "网页壁纸",
        WallpaperEngineKind.Application => "应用壁纸",
        _ => "未知类型壁纸",
    };

    [RelayCommand]
    private void OpenBgTuning()
    {
        // 调节的是首页那层背景，所以开窗前先把人带回首页（弹层时代同一条规矩）。
        CurrentPage = "home";
        IsBgTuningOpen = true;
    }

    [RelayCommand]
    private void CloseBgTuning() => IsBgTuningOpen = false;

    [RelayCommand]
    private void ResetBackgroundTuning()
    {
        // 只把"调过的东西"归零，**不动用户选的那张背景** —— 调节窗里的这颗按钮是"我调坏了，
        // 回到没调过的样子"，不是"换回默认壁纸"。后者在设置页的「背景」那一行，是另一回事。
        BgBlur = 0;
        BgBrightness = 0;
        BgPanX = 0;
        BgPanY = 0;
        BgZoom = 100;
        BgContrast = 0;
        BgSaturation = 0;
        BgHue = 0;
    }

    [RelayCommand]
    private void ResetBackground()
    {
        BgArt = PixelArt.CreateBackground(IsDark);
        VideoBackgroundPath = null;
        // 复位到"静音"就是音量 0；顺手把"上一次非 0"记回 100，免得点喇叭又回到某个陈年刻度。
        _lastAudibleVolume = 100;
        VideoBackgroundVolume = 0;
        CustomBackgroundPath = null;
        BgBlur = 0;
        BgBrightness = 0;
        BgPanX = 0;
        BgPanY = 0;
        BgZoom = 100;
        BgContrast = 0;
        BgSaturation = 0;
        BgHue = 0;
        // 不关窗：恢复默认之后用户要看见"回到原样了"，窗口正好就是那面镜子。
    }

    // ==========================================================
    // 首页 3D 模型（皮肤小人）
    // ==========================================================

    /// <summary>
    /// 主页那块 3D 皮肤模型是否出现，<b>默认开</b>。
    ///
    /// <para>关掉时把拖过的位置一起清掉：一个看不见的东西没有"停在哪儿"可言，重新打开就该站在
    /// 设计锚点上。读设置那一次例外 —— 那一刻 <see cref="HomeSkinModelX" /> 还没轮到赋值，
    /// 清了就把用户拖出来的位置抹在启动路上。</para>
    /// </summary>
    [ObservableProperty]
    private bool _homeSkinModelVisible = true;

    partial void OnHomeSkinModelVisibleChanged(bool value)
    {
        if (!value && !_applyingSettings)
        {
            HomeSkinModelX = null;
            HomeSkinModelY = null;
        }

        PersistSettings();
        OnPropertyChanged(nameof(ShowHomeSkinModel));
    }

    /// <summary>
    /// 模型在首页可用区里的位置，<b>单位是宽高的比例</b>（0..1）。视图只管把拖完的比例写回来，
    /// 落盘统一在这里（和 <see cref="BgTuningX" /> 同一套路）。
    /// </summary>
    [ObservableProperty]
    private double? _homeSkinModelX;

    [ObservableProperty]
    private double? _homeSkinModelY;

    partial void OnHomeSkinModelXChanged(double? value)
    {
        PersistSettings();
        OnPropertyChanged(nameof(HasHomeSkinModelPosition));
    }

    partial void OnHomeSkinModelYChanged(double? value)
    {
        PersistSettings();
        OnPropertyChanged(nameof(HasHomeSkinModelPosition));
    }

    /// <summary>「复位位置」这颗按钮只在拖过之后才有得按。</summary>
    public bool HasHomeSkinModelPosition => HomeSkinModelX is not null || HomeSkinModelY is not null;

    /// <summary>首页那层 Canvas 的可见性：没账号就没人可画，开关关掉就整块不出现。</summary>
    public bool ShowHomeSkinModel => HasAccount && HomeSkinModelVisible;

    [RelayCommand]
    private void ResetHomeSkinModelPosition()
    {
        if (HomeSkinModelX is null && HomeSkinModelY is null) return;

        HomeSkinModelX = null;
        HomeSkinModelY = null;
    }

    // ==========================================================
    // Accounts
    // ==========================================================

    [ObservableProperty]
    private PixelBuffer? _avatarBitmap;

    public ObservableCollection<AccountModel> Accounts { get; } = new();

    public bool HasAccounts => Accounts.Count > 0;

    [ObservableProperty]
    private AccountModel? _currentAccount;

    /// <summary>Drives the home 3D preview: with no account there is nobody to render.</summary>
    public bool HasAccount => CurrentAccount is not null;

    [ObservableProperty]
    private bool _isAccPopOpen;

    /// <summary>
    /// 悬停侧栏底部头像弹出的账户信息卡。与 <see cref="IsAccPopOpen"/>（点出来的账户列表）
    /// 互斥：列表开着时不显示这张卡，反过来点头像开列表时这张卡也让位。
    /// 开合由 view 侧的悬停轮询驱动，这里只存状态。
    /// </summary>
    [ObservableProperty]
    private bool _isAccountInfoOpen;

    /// <summary>信息卡上的账户名。没有账户时说清楚是"未登录"，别留一片空白。</summary>
    public string AccountInfoName => CurrentAccount?.Name ?? "未登录";

    /// <summary>信息卡上的登录方式；一个账户都没有时，这行改成引导去添加。</summary>
    public string AccountInfoType => CurrentAccount?.Type ?? "点击头像添加账户";

    partial void OnCurrentAccountChanged(AccountModel? value)
    {
        foreach (var a in Accounts) a.IsCurrent = ReferenceEquals(a, value);
        AvatarBitmap = value?.AvatarBitmap ?? NoAccountAvatar;
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(ShowHomeSkinModel));
        OnPropertyChanged(nameof(EffectivePetName));
        OnPropertyChanged(nameof(AccountInfoName));
        OnPropertyChanged(nameof(AccountInfoType));
    }

    [RelayCommand]
    private void ToggleAccPop()
    {
        IsAccPopOpen = !IsAccPopOpen;
        // 悬停信息卡和点出来的列表不同时在场：头像正悬着的时候点头，这张卡先让位。
        IsAccountInfoOpen = false;
        IsInstPopOpen = false;
    }

    [RelayCommand]
    private void SelectAccount(AccountModel account)
    {
        CurrentAccount = account;
        _launcher.Accounts.SetCurrent(account.Source);
        IsAccPopOpen = false;
    }

    [RelayCommand]
    private async Task AddOfflineAccountAsync()
    {
        IsAccPopOpen = false;

        var name = await ShowDialogAsync("添加离线账户", "离线账户只在这台电脑上有效，名字可以随时改。",
            "玩家名", "", "添加");
        if (name is null) return;

        name = name.Trim();
        if (!OfflineAccountFactory.IsValidPlayerName(name))
        {
            ShowBanner("玩家名需要是 3-16 位的字母、数字或下划线。");
            return;
        }

        _launcher.Accounts.AddOffline(name);
        RefreshAccounts();
    }

    [RelayCommand]
    private void AddMicrosoftAccount()
    {
        IsAccPopOpen = false;
        // Reserved: the OAuth flow needs an Azure application (client) id before it can be wired up.
        ShowBanner("微软账户登录尚未开放：需要在 Azure 注册应用并填入 Client ID 后才能启用。");
    }

    [RelayCommand]
    private async Task RemoveAccountAsync(AccountModel account)
    {
        IsAccPopOpen = false;

        var ok = await ShowDialogAsync("删除账户", $"确定要删除「{account.Name}」吗？", null, null, "删除");
        if (ok is null) return;

        _launcher.Accounts.Remove(account.Source);
        RefreshAccounts();
    }

    private void RefreshAccounts()
    {
        var currentId = _launcher.Accounts.Current?.Id;

        Accounts.Clear();
        foreach (var account in _launcher.Accounts.Accounts)
        {
            var isSlim = SkinRepository.IsSlimForPlayerName(account.Name);
            var initialAvatar = isSlim ? NoAccountAvatar : DefaultAvatar;
            var model = new AccountModel(account) { AvatarBitmap = initialAvatar };
            Accounts.Add(model);
            _ = LoadAvatarAsync(model);
        }

        CurrentAccount = Accounts.FirstOrDefault(a => a.Id == currentId) ?? Accounts.FirstOrDefault();
        AvatarBitmap = CurrentAccount?.AvatarBitmap ?? NoAccountAvatar;
        OnPropertyChanged(nameof(HasAccounts));
    }

    private static PixelBuffer DefaultAvatar { get; } = AvatarComposer.Create(DefaultSkins.Pixels(slim: false));

    /// <summary>Shown in the sidebar while there is no account at all, or as the initial Alex placeholder.</summary>
    private static PixelBuffer NoAccountAvatar { get; } = AvatarComposer.Create(DefaultSkins.Pixels(slim: true));

    private async Task LoadAvatarAsync(AccountModel model)
    {
        var skinData = await SkinRepository.GetOrFetchAsync(model.Name);

        var pixels = skinData is not null
            ? SkinTexture.Decode(skinData.Bytes)?.ToRenderPixels()
            : null;

        pixels ??= DefaultSkins.Pixels(skinData?.IsSlim ?? SkinRepository.IsSlimForPlayerName(model.Name));

        var avatar = AvatarComposer.Create(pixels);
        await AppDispatcher.Current.InvokeAsync(() =>
        {
            model.AvatarBitmap = avatar;
            if (ReferenceEquals(model, CurrentAccount))
            {
                AvatarBitmap = avatar;
            }
        });
    }

    // ==========================================================
    // Instances
    // ==========================================================

    /// <summary>Every instance, shown in the dock popover.</summary>
    public ObservableCollection<InstanceModel> Instances { get; } = new();

    /// <summary>The instances page grid, narrowed by the search box.</summary>
    public ObservableCollection<InstanceModel> VisibleInstances { get; } = new();

    [ObservableProperty]
    private InstanceModel? _currentInstance;

    [ObservableProperty]
    private bool _isInstPopOpen;

    [ObservableProperty]
    private string _instanceSearch = "";

    /// <summary>加载器筛选，「全部」= 不筛。</summary>
    [ObservableProperty]
    private string _instanceLoader = LoaderOptionModel.AllName;

    /// <summary>排序：最近游玩（默认，没启动过的沉底）或名称。</summary>
    [ObservableProperty]
    private string _instanceSort = "最近游玩";

    /// <summary>
    /// 视图按页宽算出来的列数 —— UniformGrid 吃它才能既自适应拉伸又保证同一行的卡片等高。
    /// 这是个布局量，不是设置，不落盘。
    /// </summary>
    [ObservableProperty]
    private int _instanceColumns = 3;

    public bool HasInstances => Instances.Count > 0;

    /// <summary>筛选下拉的选项：「全部」+ 实例里真出现过的加载器（去重、按名字排）。</summary>
    public ObservableCollection<LoaderOptionModel> LoaderChoices { get; } = new();

    /// <summary>有没有条件在筛（决定空状态说哪句话）。</summary>
    public bool HasInstanceFilter => InstanceSearch.Trim().Length > 0 || InstanceLoader != LoaderOptionModel.AllName;

    /// <summary>有实例，但被搜索/筛选筛到一条不剩 —— 空状态要换一套话术。</summary>
    public bool HasNoInstanceMatches => HasInstances && VisibleInstances.Count == 0;

    /// <summary>筛空了那句提示，把当前生效的条件原样念回去，省得用户以为实例被删了。</summary>
    public string InstanceEmptyHint
    {
        get
        {
            var bits = new List<string>();
            if (InstanceSearch.Trim().Length > 0) bits.Add($"「{InstanceSearch.Trim()}」");
            if (InstanceLoader != LoaderOptionModel.AllName) bits.Add(InstanceLoader);
            return bits.Count > 0 ? $"换个关键词，或把筛选清掉（当前：{string.Join(" · ", bits)}）" : "换个关键词试试";
        }
    }

    /// <summary>加载器筛选的下拉。选项来自 <see cref="LoaderChoices" />，所以列表比"所有存在的加载器"短得多。</summary>
    [ObservableProperty]
    private bool _isLoaderPopOpen;

    /// <summary>筛选下拉显示的那句：「全部加载器」而不是光秃秃一个「全部」。</summary>
    public string InstanceLoaderLabel => InstanceLoader == LoaderOptionModel.AllName ? "全部加载器" : InstanceLoader;

    [RelayCommand]
    private void ToggleLoaderPop()
    {
        IsLoaderPopOpen = !IsLoaderPopOpen;
        IsAccPopOpen = false;
        IsInstPopOpen = false;
        IsDlPopOpen = false;
    }

    /// <summary>Drives the transparent overlay that closes a popover when the user clicks elsewhere.</summary>
    public bool HasOpenPopover => IsAccPopOpen || IsInstPopOpen || IsDlPopOpen || IsLoaderPopOpen || IsFolderPopOpen;

    partial void OnIsAccPopOpenChanged(bool value) => OnPropertyChanged(nameof(HasOpenPopover));

    partial void OnIsInstPopOpenChanged(bool value) => OnPropertyChanged(nameof(HasOpenPopover));

    partial void OnIsDlPopOpenChanged(bool value) => OnPropertyChanged(nameof(HasOpenPopover));

    partial void OnIsLoaderPopOpenChanged(bool value) => OnPropertyChanged(nameof(HasOpenPopover));

    partial void OnCurrentInstanceChanged(InstanceModel? value)
    {
        foreach (var i in Instances) i.IsCurrent = ReferenceEquals(i, value);
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(IsEditingCurrentInstance));
    }

    partial void OnInstanceSearchChanged(string value) => ApplyInstanceFilter();

    partial void OnInstanceLoaderChanged(string value)
    {
        ApplyInstanceFilter();
        OnPropertyChanged(nameof(InstanceLoaderLabel));
        MarkLoaderSelected();
    }

    private void MarkLoaderSelected()
    {
        foreach (var option in LoaderChoices) option.IsSelected = option.Name == InstanceLoader;
    }

    partial void OnInstanceSortChanged(string value) => ApplyInstanceFilter();

    /// <summary>
    /// 卡片本体点击 = 设为当前，但不跳页。
    /// 停靠浮层那条 <see cref="SelectInstance" /> 会顺手回首页（那里选完就是要启动），
    /// 实例页选完还要接着看列表，所以两条路分开。
    /// </summary>
    [RelayCommand]
    private void MakeCurrentInstance(InstanceModel instance)
    {
        CurrentInstance = instance;
        _launcher.SelectInstance(instance.Source);
    }

    /// <summary>卡片上的「启动」：先设成当前并跳回首页，再走启动流程。</summary>
    [RelayCommand]
    private async Task LaunchInstanceAsync(InstanceModel instance)
    {
        MakeCurrentInstance(instance);
        CurrentPage = "home";
        await LaunchGameAsync();
    }

    /// <summary>
    /// 存档文件夹。隔离实例就在实例目录里；没隔离的在公共根目录下。
    /// 没生成过存档时目录还不存在，那就开它上一层 —— 报错不如让人看见东西。
    /// </summary>
    [RelayCommand]
    private void OpenInstanceSaves(InstanceModel instance)
    {
        var root = _launcher.Instances.GameDirectoryFor(instance.Source);
        OpenFolder(Directory.Exists(Path.Combine(root, "saves")) ? Path.Combine(root, "saves") : root);
    }

    [RelayCommand]
    private void SelectInstanceLoader(string loader)
    {
        IsLoaderPopOpen = false;
        if (!string.IsNullOrEmpty(loader)) InstanceLoader = loader;
    }

    [RelayCommand]
    private void SelectInstanceSort(string sort)
    {
        if (!string.IsNullOrEmpty(sort)) InstanceSort = sort;
    }

    [RelayCommand]
    private void ToggleInstPop()
    {
        IsInstPopOpen = !IsInstPopOpen;
        IsAccPopOpen = false;
    }

    [RelayCommand]
    private void SelectInstance(InstanceModel instance)
    {
        CurrentInstance = instance;
        _launcher.SelectInstance(instance.Source);
        IsInstPopOpen = false;
        CurrentPage = "home";
    }

    [RelayCommand]
    private void CloseAllPopovers()
    {
        IsAccPopOpen = false;
        IsAccountInfoOpen = false;
        IsInstPopOpen = false;
        IsDlPopOpen = false;
        IsLoaderPopOpen = false;
        IsFolderPopOpen = false;
        IsInstConfigOpen = false;
        IsVolumePopOpen = false;
    }

    // ==========================================================
    // 游戏目录（实例页顶上那颗胶囊）
    // ==========================================================

    /// <summary>启动器认下的所有 .minecraft 式目录；实例页只看其中一个。</summary>
    public ObservableCollection<GameFolderModel> Folders { get; } = new();

    [ObservableProperty]
    private bool _isFolderPopOpen;

    partial void OnIsFolderPopOpenChanged(bool value) => OnPropertyChanged(nameof(HasOpenPopover));

    public string ActiveFolderName => _launcher.ActiveFolder.Name;

    public string ActiveFolderPath => _launcher.ActiveFolder.Path;

    [RelayCommand]
    private void ToggleFolderPop()
    {
        IsFolderPopOpen = !IsFolderPopOpen;
        IsAccPopOpen = false;
        IsInstPopOpen = false;
        IsDlPopOpen = false;
        IsLoaderPopOpen = false;
    }

    /// <summary>切目录 = 换一份实例列表；当前实例跟着这个目录自己记的那一个走。</summary>
    [RelayCommand]
    private void SelectFolder(string path)
    {
        if (string.IsNullOrEmpty(path)) return;

        _launcher.SelectFolder(path);
        IsFolderPopOpen = false;
        RefreshInstances();
    }

    [RelayCommand]
    private async Task RenameFolderAsync(string path)
    {
        if (_launcher.Settings.FindFolder(path) is not { } folder) return;

        var name = await ShowDialogAsync("重命名目录", folder.Name, "显示用的名字", folder.Name, "保存");
        if (name is null || string.IsNullOrWhiteSpace(name)) return;

        _launcher.TryRenameFolder(path, name);
        RefreshFolders();
    }

    [RelayCommand]
    private void SetDefaultFolder(string path)
    {
        if (!_launcher.TrySetDefaultFolder(path)) return;

        IsFolderPopOpen = false;
        RefreshFolders();
        ShowBanner($"「{_launcher.Settings.FindFolder(path)?.Name}」现在是默认目录，新建与下载都会落在这里。");
    }

    [RelayCommand]
    private async Task RemoveFolderAsync(string path)
    {
        if (_launcher.Settings.FindFolder(path) is not { } folder) return;

        var ok = await ShowDialogAsync("移除目录",
            $"只是从启动器的列表里去掉「{folder.Name}」，硬盘上的文件一个都不动。确定吗？", null, null, "移除");
        if (ok is null) return;

        if (!_launcher.TryRemoveFolder(path, out var error))
        {
            ShowBanner(error);
            return;
        }

        // 移除的有可能正是眼前这一个，Settings.Save 已经把活动目录退回默认的，这里跟上。
        _launcher.SelectFolder(_launcher.ActiveFolder.Path);
        IsFolderPopOpen = false;
        RefreshInstances();
    }

    // ----------------------------------------------------------
    // 添加已有目录
    // ----------------------------------------------------------

    [ObservableProperty]
    private bool _isAddFolderOpen;

    [ObservableProperty]
    private string _newFolderPath = "";

    [ObservableProperty]
    private string _newFolderName = "";

    public string NewFolderSuggestedName
    {
        get
        {
            var path = NclPaths.NormaliseGameRoot(NewFolderPath);
            return path.Length > 0 ? Core.Settings.LauncherSettings.SuggestFolderName(path) : "";
        }
    }

    /// <summary>
    ///     那行校验提示：<b>真去数一遍 versions/ 里的实例</b>，不是摆个样子。
    ///     路径写错、盘没插、或者那是个空文件夹，都在这句话里说清楚，别等添加完了看空列表。
    /// </summary>
    public string NewFolderScanHint
    {
        get
        {
            var path = NclPaths.NormaliseGameRoot(NewFolderPath);
            if (path.Length == 0) return "先填一个文件夹路径。";
            if (!Directory.Exists(path)) return "这个路径不存在，或者不是一个文件夹。";
            if (_launcher.Settings.FindFolder(path) is { } taken) return $"已经添加过了：「{taken.Name}」。";

            var count = _launcher.CountInstancesIn(path);
            return count > 0
                ? $"校验通过：versions 里有 {count} 个实例。"
                : "没有 versions 目录或里面是空的 —— 添加进来也可以，之后在里面装的实例会显示在这。";
        }
    }

    partial void OnNewFolderPathChanged(string value)
    {
        OnPropertyChanged(nameof(NewFolderScanHint));
        OnPropertyChanged(nameof(NewFolderSuggestedName));
        OnPropertyChanged(nameof(NewFolderLooksValid));
    }

    /// <summary>校验那句配哪个图标：路径在、又不重复，才算真通过 —— 警告句子挂个绿勾是骗人。</summary>
    public bool NewFolderLooksValid
    {
        get
        {
            var path = NclPaths.NormaliseGameRoot(NewFolderPath);
            return path.Length > 0 && Directory.Exists(path) && _launcher.Settings.FindFolder(path) is null;
        }
    }

    [RelayCommand]
    private void BeginAddFolder(string? suggestedPath = null)
    {
        NewFolderPath = suggestedPath ?? "";
        NewFolderName = "";
        IsFolderPopOpen = false;
        IsAddFolderOpen = true;
    }

    [RelayCommand]
    private void CloseAddFolder() => IsAddFolderOpen = false;

    [RelayCommand]
    private void ConfirmAddFolder()
    {
        var name = NewFolderName.Trim().Length > 0 ? NewFolderName.Trim() : NewFolderSuggestedName;

        if (!_launcher.TryAddFolder(NewFolderPath, name, out var added, out var error))
        {
            ShowBanner(error);
            return;
        }

        IsAddFolderOpen = false;

        // 添加完就切过去看成果，和实例页那颗胶囊的语义一致：胶囊说的就是"我现在在哪"。
        if (added is not null) _launcher.SelectFolder(added.Path);
        RefreshInstances();
    }

    /// <summary>用视图选回来的路径开对话框（两边的目录选择器 API 不同，选文件是视图的事）。</summary>
    public void OfferNewFolder(string path)
    {
        NewFolderPath = path;
        if (NewFolderName.Length == 0) NewFolderName = NewFolderSuggestedName;
        IsAddFolderOpen = true;
    }

    // ==========================================================
    // 导入整合包
    // ==========================================================

    [ObservableProperty]
    private bool _isPackImportOpen;

    [ObservableProperty]
    private string _packSourcePath = "";

    [ObservableProperty]
    private string _packInstanceName = "";

    [ObservableProperty]
    private string _packTargetFolder = "";

    /// <summary>导入是长活（要下整份原版 + 一堆 mod），跑起来就不许再点一次。</summary>
    [ObservableProperty]
    private bool _isPackImportBusy;

    [ObservableProperty]
    private string _packStageText = "";

    [ObservableProperty]
    private double _packProgressValue;

    /// <summary>把整合包拖进窗口时那层遮罩。视图管拖放事件，这里只管它该不该露出来。</summary>
    [ObservableProperty]
    private bool _isDropOverlayVisible;

    public string PackProgressText => IsPackImportBusy
        ? $"{PackStageText} {PackProgressValue:0}%"
        : "";

    partial void OnPackStageTextChanged(string value) => OnPropertyChanged(nameof(PackProgressText));

    partial void OnPackProgressValueChanged(double value) => OnPropertyChanged(nameof(PackProgressText));

    partial void OnIsPackImportBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartPackImport));
        OnPropertyChanged(nameof(PackProgressText));
    }

    [RelayCommand]
    private void BeginPackImport()
    {
        PackSourcePath = "";
        PackInstanceName = "";
        PackTargetFolder = ActiveFolderPath;
        PackStageText = "";
        PackProgressValue = 0;
        IsPackImportOpen = true;
    }

    public string PackSourceName => Path.GetFileName(PackSourcePath);

    /// <summary>拖拽区那一行大字：没选文件时是引导，选了就把文件名念回去，免得用户不确定到底挑了哪个。</summary>
    public string PackDropTitle => PackSourceName.Length > 0 ? PackSourceName : "把整合包拖进来，或点这里浏览";

    public bool CanStartPackImport => PackSourcePath.Length > 0 && !IsPackImportBusy;

    partial void OnPackSourcePathChanged(string value)
    {
        OnPropertyChanged(nameof(PackSourceName));
        OnPropertyChanged(nameof(PackDropTitle));
    }

    /// <summary>拖进窗口或点了「浏览」之后由视图调这里：文件名先填进实例名，用户想改就改。</summary>
    public void OfferPackFile(string path)
    {
        if (!File.Exists(path))
        {
            ShowBanner("这个文件已经不在了。");
            return;
        }

        // 只吃 zip 容器：rar / 7z 要额外的原生解压器，启动器里没有，拖进来也解不开。
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".zip" or ".mrpack"))
        {
            ShowBanner($"「{extension}」解不了 —— 整合包请用 .mrpack 或 .zip。");
            return;
        }

        PackSourcePath = path;
        PackInstanceName = Path.GetFileNameWithoutExtension(path);
        PackTargetFolder = ActiveFolderPath;
        IsPackImportOpen = true;
    }

    [RelayCommand]
    private void ClosePackImport() => IsPackImportOpen = false;

    [RelayCommand]
    private void SelectPackTargetFolder(string path)
    {
        if (!string.IsNullOrEmpty(path)) PackTargetFolder = path;
    }

    partial void OnPackTargetFolderChanged(string value) => MarkPackTargetSelected();

    /// <summary>对话框那排目录 chip 的选中态：和加载器下拉同一套做法，状态挂在每一项自己身上。</summary>
    private void MarkPackTargetSelected()
    {
        foreach (var folder in Folders)
        {
            folder.IsPackTarget = string.Equals(folder.Path, PackTargetFolder, StringComparison.OrdinalIgnoreCase);
        }
    }

    [RelayCommand]
    private async Task StartPackImportAsync()
    {
        if (!CanStartPackImport) return;
        if (!File.Exists(PackSourcePath))
        {
            ShowBanner("这个文件已经不在了。");
            return;
        }

        var source = PackSourcePath;
        var target = PackTargetFolder.Length > 0 ? PackTargetFolder : ActiveFolderPath;
        var name = PackInstanceName.Trim();

        IsPackImportBusy = true;
        PackStageText = "准备导入";
        PackProgressValue = 0;
        var task = BeginTask($"导入 {Path.GetFileName(source)}");

        // 同一份进度喂两个地方：任务列表那条行，和对话框里让人安心等着的那行字。
        var progress = new CallbackProgress<InstallProgress>(p => AppDispatcher.Current.Post(() =>
        {
            Apply(task, p);
            PackStageText = p.Stage;
            PackProgressValue = Math.Clamp(p.Fraction * 100, 0, 100);
        }));

        try
        {
            var result = await _launcher.ImportModpackAsync(
                source, target, name.Length == 0 ? null : name, progress, task.Cts!.Token);

            FinishTask(task, "已完成");
            IsPackImportBusy = false;

            if (result.NewGameFolder is { } extracted)
            {
                // 一整份 .minecraft 那种包不当实例装：铺成新目录并切过去，版本/libraries/assets 都在包里。
                if (_launcher.TryAddFolder(extracted, result.Manifest.Name, out var added, out _))
                {
                    _launcher.SelectFolder(added!.Path);
                }
            }
            else if (string.Equals(target, ActiveFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                _launcher.ReloadInstances();
            }

            RefreshInstances();
            ClosePackImport();
            ShowBanner(result.NewGameFolder is { } folder
                ? $"已经解压成一个新的游戏目录：{Path.GetFileName(folder)}"
                : $"「{result.Manifest.Name}」导入好了，可以直接启动。");
        }
        catch (Exception ex)
        {
            // 不给导入留「继续」：重试会再开一个实例目录（同名会带上 (2)），已下好的文件倒是都跳过。
            EndTask(task, ex);
            IsPackImportBusy = false;
            if (task.Status is not ("已暂停" or "已取消")) ShowBanner($"导入失败：{ex.Message}");
        }
    }

    // ==========================================================
    // 实例页的排法（卡片 / 列表）
    // ==========================================================

    /// <summary>"grid" 卡片墙，"list" 紧凑列表。看法而已，跟着设置落盘。</summary>
    [ObservableProperty]
    private string _instanceViewMode = "grid";

    public bool IsGridView => InstanceViewMode != "list";

    public bool IsListView => InstanceViewMode == "list";

    partial void OnInstanceViewModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsGridView));
        OnPropertyChanged(nameof(IsListView));
        PersistSettings();
    }

    [RelayCommand]
    private void SelectInstanceView(string mode)
    {
        if (!string.IsNullOrEmpty(mode)) InstanceViewMode = mode;
    }

    private void RefreshFolders()
    {
        var active = _launcher.ActiveFolder.Path;

        Folders.Clear();
        foreach (GameFolder folder in _launcher.Folders)
        {
            Folders.Add(new GameFolderModel(folder)
            {
                IsCurrent = string.Equals(folder.Path, active, StringComparison.OrdinalIgnoreCase),
            });
        }

        OnPropertyChanged(nameof(ActiveFolderName));
        OnPropertyChanged(nameof(ActiveFolderPath));
        OnPropertyChanged(nameof(GameRoot));
        MarkPackTargetSelected();
    }

    // ==========================================================
    // Instance Management Page
    // ==========================================================

    [ObservableProperty]
    private bool _isInstConfigOpen;

    [ObservableProperty]
    private InstanceModel? _editingInstance;

    [ObservableProperty]
    private string _configInstName = "";

    [ObservableProperty]
    private bool _configInstIsolated = true;

    [ObservableProperty]
    private bool _configInstCustomJava;

    [ObservableProperty]
    private string? _configInstJavaPath;

    [ObservableProperty]
    private string _configInstJavaLabel = "跟随全局设置";

    [ObservableProperty]
    private bool _isInstJavaChooserOpen;

    [ObservableProperty]
    private bool _configInstCustomMemory;

    [ObservableProperty]
    private int _configInstMemoryGb = 4;

    /// <summary>实例那一行手写的 JVM 参数，原样存、按空白切开喂给启动命令。</summary>
    [ObservableProperty]
    private string _configInstExtraJvmArgs = "";

    /// <summary>预分配：<c>-Xms</c> 直接顶到 <c>-Xmx</c>。默认关 —— 那是拿物理内存换 GC 碎片。</summary>
    [ObservableProperty]
    private bool _configInstPreallocate;

    public ObservableCollection<JavaOptionModel> InstJavaOptions { get; } = new();

    public string GlobalMemoryGbSummary => $"{MaxMemoryGb} GB";
    public string GlobalJavaSummary => string.IsNullOrWhiteSpace(JavaLabel) ? "自动" : JavaLabel;

    /// <summary>管理页当前那一页：概览 / 模组 / 资源包 / 世界 / Java / 目录。</summary>
    [ObservableProperty]
    private string _configTab = "概览";

    [RelayCommand]
    private void SelectConfigTab(string tab)
    {
        if (!string.IsNullOrEmpty(tab)) ConfigTab = tab;
    }

    /// <summary>正在管理的就是当前实例时，操作栏那颗「设为当前」不该出现。</summary>
    public bool IsEditingCurrentInstance => EditingInstance is not null && ReferenceEquals(EditingInstance, CurrentInstance);

    /// <summary>目录页那三行路径。没开隔离时"实例文件夹"就是公共游戏根目录，照实显示。</summary>
    public string EditingGameDirectory =>
        EditingInstance is { } i ? _launcher.Instances.GameDirectoryFor(i.Source) : "";

    public string EditingSavesDirectory =>
        EditingInstance is { } i ? Path.Combine(_launcher.Instances.GameDirectoryFor(i.Source), "saves") : "";

    public string EditingModsDirectory =>
        EditingInstance is { } i ? Path.Combine(_launcher.Instances.GameDirectoryFor(i.Source), "mods") : "";

    public string EditingResourcePacksDirectory =>
        EditingInstance is { } i ? Path.Combine(_launcher.Instances.GameDirectoryFor(i.Source), "resourcepacks") : "";

    /// <summary>头部那行小字：版本 · 加载器 · 所在目录。</summary>
    public string EditingVersionSummary =>
        EditingInstance is { } i ? $"{i.MetaText} · 所在目录：{ActiveFolderName}" : "";

    [ObservableProperty]
    private string _editingModCountText = "0 模组";

    [ObservableProperty]
    private string _editingResourcePackCountText = "0 资源包";

    [ObservableProperty]
    private string _editingWorldCountText = "0 世界";

    [ObservableProperty]
    private string _modSearch = "";

    partial void OnModSearchChanged(string value) => ApplyModFilter();

    public ObservableCollection<ModItemModel> EditingMods { get; } = new();
    public ObservableCollection<ModItemModel> FilteredEditingMods { get; } = new();
    public ObservableCollection<ResourcePackItemModel> EditingResourcePacks { get; } = new();
    public ObservableCollection<WorldItemModel> EditingWorlds { get; } = new();

    public bool HasEditingMods => EditingMods.Count > 0;
    public bool HasFilteredEditingMods => FilteredEditingMods.Count > 0;
    public bool HasEditingResourcePacks => EditingResourcePacks.Count > 0;
    public bool HasEditingWorlds => EditingWorlds.Count > 0;

    public event Action? RequestAddModFile;
    public event Action? RequestAddResourcePackFile;

    [RelayCommand]
    private void AddModFile() => RequestAddModFile?.Invoke();

    [RelayCommand]
    private void AddResourcePackFile() => RequestAddResourcePackFile?.Invoke();

    // ==========================================================
    // 实例图标
    // ==========================================================

    /// <summary>图标那行在空闲时显示的说明（出错时会被换成具体的错误话）。</summary>
    private const string IconHintIdle = "默认跟随加载器，也可以选一块预设方块";

    /// <summary>这个实例有没有自定义图标 —— 决定「恢复默认」那颗按钮显不显示。</summary>
    public bool HasCustomIcon =>
        EditingInstance is { } model &&
        (!string.IsNullOrEmpty(model.Source.IconPreset) ||
         (!string.IsNullOrEmpty(model.Source.IconPath) && File.Exists(model.Source.IconPath)));

    /// <summary>图标那行的小字：平时是说明，选图失败时换成错误原因。</summary>
    [ObservableProperty]
    private string _instanceIconHint = IconHintIdle;

    /// <summary>「选一块方块」那排预设展不展开。</summary>
    [ObservableProperty]
    private bool _isIconPickerOpen;

    /// <summary>
    /// 可选的预设方块 = <see cref="BlockIconKeys"/> 整张表（含铁砧 —— 它只是不进「按 id 散列」
    /// 那个池子，用户自己挑它是正当的）。
    ///
    /// <para><b>这里取 128 而不是原图</b>：这排格子只有 32 DIP（200% 缩放要 64 物理像素），
    /// 18 张 1024 全上就是 18 × 4 MB 的位图，为了几十像素的缩略图不值得。128 供 32~128 像素
    /// 的格子都够用，一整套加起来才 1 MB 出头。真正上屏的实例图标走
    /// <see cref="BlockIcon"/>，那边是 1024 原图。</para>
    ///
    /// <para>第一次用到时才建：18 张解码 + 缩放要两百多毫秒，别在建 VM 的时候干。</para>
    /// </summary>
    public ObservableCollection<BlockIconModel> IconPresets { get; } = new();

    /// <summary>预设格子里那张缩略图的边长。见 <see cref="IconPresets"/> 的说明。</summary>
    private const int IconPresetThumbSize = 128;

    private void EnsureIconPresets()
    {
        if (IconPresets.Count > 0) return;

        foreach (var key in BlockIconKeys)
        {
            if (BlockAssets.Load(key, IconPresetThumbSize) is not { } thumb) continue;
            IconPresets.Add(new BlockIconModel(key, BlockDisplayName(key), thumb));
        }
    }

    /// <summary>展开/收起预设。展开时顺手把「当前选的是哪块」标出来。</summary>
    [RelayCommand]
    private void ToggleIconPicker()
    {
        EnsureIconPresets();
        IsIconPickerOpen = !IsIconPickerOpen;
        if (IsIconPickerOpen) SyncIconPresetSelection();
    }

    /// <summary>把当前实例用的图标（如果是预设）在那排方块上标出来。</summary>
    private void SyncIconPresetSelection()
    {
        var key = EditingInstance?.Source.IconPreset;
        foreach (var preset in IconPresets) preset.IsSelected = preset.Key == key;
    }

    /// <summary>
    /// 把选中的预设方块设成实例图标。
    ///
    /// <para>写进元数据的是<b>键</b>（<see cref="Instance.IconPreset"/>），不往实例目录落图片：
    /// 图标本体就在程序集里，复制一份到磁盘只会多占空间、多一次解码，换资源版本后还会留下
    /// 孤儿旧图。落盘这条路只留给历史数据 —— 老版本存过的 <c>icon.png</c> 仍然认，
    /// 一旦选了预设就会被清掉。</para>
    /// </summary>
    [RelayCommand]
    private void SetInstanceIconPreset(BlockIconModel? preset)
    {
        if (EditingInstance is not { } model || preset is null) return;

        if (!_launcher.Instances.SetIconPreset(model.Source, preset.Key))
        {
            InstanceIconHint = "写不进实例元数据，检查一下磁盘权限。";
            return;
        }

        _iconCache.Remove(_launcher.Instances.IconPathFor(model.Source));
        model.IconBitmap = IconFor(model.Source);
        if (CurrentInstance is { } current && !ReferenceEquals(current, model)) current.IconBitmap = IconFor(current.Source);

        IsIconPickerOpen = false;
        InstanceIconHint = IconHintIdle;
        OnPropertyChanged(nameof(HasCustomIcon));
        SyncIconPresetSelection();
    }

    /// <summary>清掉预设 / 旧的 icon.png，图标自动回落到加载器标志 / 方块。</summary>
    [RelayCommand]
    private void ResetInstanceIcon()
    {
        if (EditingInstance is not { } model) return;

        if (!_launcher.Instances.ClearIcon(model.Source))
        {
            InstanceIconHint = "删不掉旧的图标文件，检查一下磁盘权限。";
            return;
        }

        _iconCache.Remove(_launcher.Instances.IconPathFor(model.Source));
        model.IconBitmap = IconFor(model.Source);
        if (CurrentInstance is { } current && !ReferenceEquals(current, model)) current.IconBitmap = IconFor(current.Source);

        IsIconPickerOpen = false;
        InstanceIconHint = IconHintIdle;
        OnPropertyChanged(nameof(HasCustomIcon));
        SyncIconPresetSelection();
    }

    // ==========================================================
    // 所在地区
    // ==========================================================

    /// <summary>设置页里这一项的名字。挡切页要用到，别在各处重新拼字符串。</summary>
    private const string RegionTab = "地区";

    /// <summary>
    /// 设置里「地区」那一页显不显示 —— 跟 <see cref="RegionFeature"/> 走，
    /// <b>Release 构建里恒为 false</b>。
    /// </summary>
    public bool IsRegionVisible => RegionFeature.Enabled;

    /// <summary>
    /// 调试通道强制出来的地区，<b>只在内存里</b>。真值永远是 <c>LauncherSettings.Region</c>；
    /// 这一份只为「手上没有对应出口 IP 也要看到别的地区长什么样」而存在。
    /// </summary>
    private DetectedRegion? _regionOverride;

    /// <summary>上一次探测整个失败了（不是"探到但认不出"）。界面要如实说，别拿旧结果冒充新的。</summary>
    private bool _regionFailed;

    private DetectedRegion? EffectiveRegion => _regionOverride ?? _launcher.Settings.Region;

    [ObservableProperty]
    private bool _isRegionChecking;

    partial void OnIsRegionCheckingChanged(bool value)
    {
        OnPropertyChanged(nameof(RegionLabel));
        OnPropertyChanged(nameof(RegionHint));
    }

    /// <summary>地区那行的大字：「中国大陆 · 天津」；没探到就是「未知」。</summary>
    public string RegionLabel
    {
        get
        {
            if (EffectiveRegion is not { } region) return IsRegionChecking ? "正在检测…" : "未知";

            var name = string.IsNullOrWhiteSpace(region.Name) ? region.Code : region.Name;
            if (string.IsNullOrWhiteSpace(name)) name = "未知";

            return string.IsNullOrWhiteSpace(region.City) ? name : $"{name} · {region.City}";
        }
    }

    /// <summary>地区那行的小字：什么时候、由谁探的，以及"它到底依据什么判断"。</summary>
    public string RegionHint
    {
        get
        {
            if (IsRegionChecking) return "正在探测出口 IP 的注册地…";

            if (EffectiveRegion is not { } region)
            {
                return _regionFailed
                    ? "这次没探到（接口都不通），先按「未知」处理。"
                    : "还没探测过。依据是出口 IP 的注册地 —— 不读设备信息，也不做定位。";
            }

            var via = region.Source switch
            {
                "ipip" => "ipip",
                "cloudflare" => "Cloudflare",
                "ipwho.is" => "ipwho.is",
                "debug" => "调试通道（不写盘）",
                _ => region.Source,
            };

            var when = region.CheckedAt.ToString("yyyy-MM-dd HH:mm");
            var hint = $"{when} 由 {via} 探测，依据是出口 IP 的注册地，换网络会跟着变。";

            return _regionFailed ? $"这次没探到，下面是上次的结果。{hint}" : hint;
        }
    }

    /// <summary>
    /// 出口 IP 在中国大陆。**这是后续"按地区给不给某些内容"的唯一判断依据** ——
    /// 港澳台按国内口径单独成区（HK / MO / TW），不算大陆。
    /// </summary>
    public bool IsMainlandChina => EffectiveRegion?.Code == "CN";

    /// <summary>重新探一次。探失败不清空上一次的结果，只在提示里说明。</summary>
    [RelayCommand]
    private async Task RefreshRegionAsync()
    {
#if DEBUG
        if (IsRegionChecking) return;

        IsRegionChecking = true;
        try
        {
            var detected = await RegionProbe.DetectAsync();

            if (detected is null)
            {
                _regionFailed = true;
                return;
            }

            _regionFailed = false;
            _regionOverride = null;
            _launcher.Settings.Region = new DetectedRegion
            {
                Code = detected.Code,
                Name = detected.Name,
                City = detected.City,
                CheckedAt = DateTime.Now,
                Source = detected.Source,
            };
            _launcher.Settings.Save();
        }
        finally
        {
            IsRegionChecking = false;
            OnPropertyChanged(nameof(RegionLabel));
            OnPropertyChanged(nameof(RegionHint));
            OnPropertyChanged(nameof(IsMainlandChina));
        }
#else
        // Release 里这块功能整个关着（见 RegionFeature）。命令本身留着，免得 XAML 里的
        // 绑定报"找不到命令"；但一个字都不往网络上发。
        await Task.CompletedTask;
#endif
    }

    /// <summary>
    /// 启动时探一次：没存过、或者存的那次已经超过一周才动。地区只影响"按地区提供哪些内容"，
    /// 探不到也不影响启动，所以不 await、不挡路。
    ///
    /// <para><b>Release 里这个方法体是空的</b>：这块功能整个关着（<see cref="RegionFeature"/>），
    /// 发版不该因为一个还没开放的设置项偷偷请求第三方 IP 接口。用 <c>#if</c> 而不是
    /// <c>if (!RegionFeature.Enabled) return;</c>，是因为 const 折叠之后那句 return 在 Debug 里
    /// 永远到不了，编译器会报 CS0162「无法访问的代码」。</para>
    /// </summary>
    private void StartRegionProbe()
    {
#if DEBUG
        var saved = _launcher.Settings.Region;
        if (saved is null || DateTime.Now - saved.CheckedAt > TimeSpan.FromDays(7)) _ = RefreshRegionAsync();
#endif
    }

    /// <summary>
    /// 调试通道用：把地区强制成某个值（传空串恢复成真实探测结果）。<b>不写 settings.json</b> ——
    /// 它只是让手上这台机器也能看到「日本 / 美国 / 未知」这些分支，不改用户的真实记录。
    /// </summary>
    public string ForceRegionForDebug(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            _regionOverride = null;
        }
        else
        {
            var upper = code.Trim().ToUpperInvariant();
            _regionOverride = new DetectedRegion
            {
                Code = upper,
                Name = RegionProbe.NameOf(upper),
                City = null,
                CheckedAt = DateTime.Now,
                Source = "debug",
            };
        }

        _regionFailed = false;
        OnPropertyChanged(nameof(RegionLabel));
        OnPropertyChanged(nameof(RegionHint));
        OnPropertyChanged(nameof(IsMainlandChina));
        return RegionLabel;
    }

    partial void OnEditingInstanceChanged(InstanceModel? value)
    {
        OnPropertyChanged(nameof(IsEditingCurrentInstance));
        OnPropertyChanged(nameof(EditingGameDirectory));
        OnPropertyChanged(nameof(EditingSavesDirectory));
        OnPropertyChanged(nameof(EditingModsDirectory));
        OnPropertyChanged(nameof(EditingResourcePacksDirectory));
        OnPropertyChanged(nameof(EditingVersionSummary));
        OnPropertyChanged(nameof(HasCustomIcon));
        InstanceIconHint = IconHintIdle;
        IsIconPickerOpen = false;
        SyncIconPresetSelection();
    }

    /// <summary>返回实例列表页并保存可能改过的基本设置。</summary>
    [RelayCommand]
    private void BackToInstances()
    {
        SaveEditingInstanceInternal();
        CurrentPage = "instances";
    }

    /// <summary>设为当前实例。</summary>
    [RelayCommand]
    private void MakeEditingInstanceCurrent()
    {
        if (EditingInstance is not { } instance) return;

        MakeCurrentInstance(instance);
        OnPropertyChanged(nameof(IsEditingCurrentInstance));
        ShowBanner($"已将「{instance.Name}」设为当前实例。");
    }

    /// <summary>从管理页启动当前正在编辑的实例，启动后返回首页。</summary>
    [RelayCommand]
    private async Task LaunchEditingInstanceAsync()
    {
        if (EditingInstance is not { } instance) return;

        SaveEditingInstanceInternal();
        await LaunchInstanceAsync(instance);
    }

    /// <summary>删除当前正在编辑的实例，删完回实例列表。</summary>
    [RelayCommand]
    private async Task DeleteEditingInstanceAsync()
    {
        if (EditingInstance is not { } instance) return;

        CurrentPage = "instances";
        await DeleteInstanceAsync(instance);
    }

    [RelayCommand]
    private void OpenInstanceConfig(InstanceModel instance)
    {
        EditingInstance = instance;
        ConfigInstName = instance.Name;
        ConfigInstIsolated = instance.Isolated;
        ConfigInstJavaPath = instance.JavaPath;
        ConfigInstCustomJava = !string.IsNullOrWhiteSpace(instance.JavaPath);

        ConfigInstCustomMemory = instance.MaxMemoryMb.HasValue;
        ConfigInstMemoryGb = instance.MaxMemoryMb.HasValue
            ? Math.Clamp(instance.MaxMemoryMb.Value / 1024, 1, 32)
            : MaxMemoryGb;

        ConfigInstExtraJvmArgs = instance.ExtraJvmArguments ?? "";
        ConfigInstPreallocate = instance.PreallocateMemory;

        RefreshInstJavaOptions();
        IsInstJavaChooserOpen = false;
        IsIconPickerOpen = false;
        InstanceIconHint = IconHintIdle;
        ConfigTab = "概览";
        ModSearch = "";
        RefreshInstanceManageData();

        IsInstConfigOpen = false;
        CurrentPage = "instance_manage";
    }

    [RelayCommand]
    private void CloseInstanceConfig()
    {
        IsInstConfigOpen = false;
        CurrentPage = "instances";
    }

    [RelayCommand]
    private void SaveEditingInstance()
    {
        if (SaveEditingInstanceInternal())
        {
            ShowBanner($"实例「{ConfigInstName.Trim()}」配置已保存。");
        }
    }

    // 保留旧命名以防外部或旧视图绑定调用
    [RelayCommand]
    private void SaveInstanceConfig() => SaveEditingInstance();

    private bool SaveEditingInstanceInternal()
    {
        if (EditingInstance is null) return false;

        var name = ConfigInstName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowBanner("实例名称不能为空。");
            return false;
        }

        var javaPath = ConfigInstCustomJava ? ConfigInstJavaPath : null;
        var maxMemoryMb = ConfigInstCustomMemory ? (int?)(ConfigInstMemoryGb * 1024) : null;

        _launcher.Instances.Update(EditingInstance.Id, name, ConfigInstIsolated, javaPath, maxMemoryMb,
            ConfigInstExtraJvmArgs, ConfigInstPreallocate);
        EditingInstance.Refresh();

        ApplyInstanceFilter();
        if (CurrentInstance?.Id == EditingInstance.Id)
        {
            CurrentInstance.Refresh();
        }

        return true;
    }

    [RelayCommand]
    private void RefreshInstanceManageData()
    {
        RefreshEditingMods();
        RefreshEditingResourcePacks();
        RefreshEditingWorlds();
        EditingInstance?.Refresh();
    }

    [RelayCommand]
    private void OpenEditingResourcePacks()
    {
        if (EditingInstance is null) return;
        var dir = EditingResourcePacksDirectory;
        try { Directory.CreateDirectory(dir); } catch { }
        OpenFolder(dir);
    }

    [RelayCommand]
    private void OpenEditingSaves()
    {
        if (EditingInstance is null) return;
        var dir = EditingSavesDirectory;
        try { Directory.CreateDirectory(dir); } catch { }
        OpenFolder(dir);
    }

    [RelayCommand]
    private void OpenWorldFolder(WorldItemModel item) => OpenFolder(item.FolderPath);

    [RelayCommand]
    private void ToggleMod(ModItemModel item)
    {
        try
        {
            var oldPath = item.FilePath;
            if (!File.Exists(oldPath)) return;

            string newPath;
            if (item.IsEnabled)
            {
                newPath = oldPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? oldPath
                    : oldPath + ".disabled";
                item.IsEnabled = false;
            }
            else
            {
                newPath = oldPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? oldPath[..^9]
                    : oldPath;
                item.IsEnabled = true;
            }

            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                if (File.Exists(newPath)) File.Delete(newPath);
                File.Move(oldPath, newPath);
                item.FilePath = newPath;
                item.FileName = Path.GetFileName(newPath);
            }

            var enabledCount = EditingMods.Count(m => m.IsEnabled);
            EditingModCountText = EditingMods.Count == enabledCount
                ? $"{EditingMods.Count} 模组"
                : $"已启用 {enabledCount} / 共 {EditingMods.Count} 模组";
            OnPropertyChanged(nameof(EditingModCountText));
            EditingInstance?.Refresh();
        }
        catch (Exception ex)
        {
            ShowBanner($"更改模组状态失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteModAsync(ModItemModel item)
    {
        var ok = await ShowDialogAsync("删除模组", $"确定要删除模组「{item.DisplayName}」吗？", null, null, "删除");
        if (ok is null) return;

        try
        {
            if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
            EditingMods.Remove(item);
            FilteredEditingMods.Remove(item);
            var enabledCount = EditingMods.Count(m => m.IsEnabled);
            EditingModCountText = EditingMods.Count == enabledCount
                ? $"{EditingMods.Count} 模组"
                : $"已启用 {enabledCount} / 共 {EditingMods.Count} 模组";
            OnPropertyChanged(nameof(EditingModCountText));
            OnPropertyChanged(nameof(HasEditingMods));
            OnPropertyChanged(nameof(HasFilteredEditingMods));
            EditingInstance?.Refresh();
            ShowBanner($"已删除模组「{item.DisplayName}」。");
        }
        catch (Exception ex)
        {
            ShowBanner($"删除模组失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteResourcePackAsync(ResourcePackItemModel item)
    {
        var ok = await ShowDialogAsync("删除资源包", $"确定要删除资源包「{item.Name}」吗？", null, null, "删除");
        if (ok is null) return;

        try
        {
            if (item.IsDirectory)
            {
                if (Directory.Exists(item.FilePath)) Directory.Delete(item.FilePath, true);
            }
            else
            {
                if (File.Exists(item.FilePath)) File.Delete(item.FilePath);
            }
            EditingResourcePacks.Remove(item);
            EditingResourcePackCountText = $"{EditingResourcePacks.Count} 资源包";
            OnPropertyChanged(nameof(EditingResourcePackCountText));
            OnPropertyChanged(nameof(HasEditingResourcePacks));
            ShowBanner($"已删除资源包「{item.Name}」。");
        }
        catch (Exception ex)
        {
            ShowBanner($"删除资源包失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteWorldAsync(WorldItemModel item)
    {
        var ok = await ShowDialogAsync("删除世界存档", $"确定要删除世界「{item.Name}」吗？存档删除后无法恢复！", null, null, "删除");
        if (ok is null) return;

        try
        {
            if (Directory.Exists(item.FolderPath)) Directory.Delete(item.FolderPath, true);
            EditingWorlds.Remove(item);
            EditingWorldCountText = $"{EditingWorlds.Count} 世界";
            OnPropertyChanged(nameof(EditingWorldCountText));
            OnPropertyChanged(nameof(HasEditingWorlds));
            ShowBanner($"已删除世界「{item.Name}」。");
        }
        catch (Exception ex)
        {
            ShowBanner($"删除世界失败：{ex.Message}");
        }
    }

    private void RefreshEditingMods()
    {
        EditingMods.Clear();
        if (EditingInstance is not { } instance)
        {
            ApplyModFilter();
            return;
        }

        var dir = EditingModsDirectory;
        if (Directory.Exists(dir))
        {
            try
            {
                var files = Directory.GetFiles(dir);
                foreach (var file in files.OrderBy(f => Path.GetFileName(f)))
                {
                    var ext = Path.GetExtension(file);
                    var name = Path.GetFileName(file);
                    bool isJar = ext.Equals(".jar", StringComparison.OrdinalIgnoreCase);
                    bool isDisabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ||
                                      ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase);
                    if (!isJar && !isDisabled) continue;

                    var fi = new FileInfo(file);
                    var cleanName = isDisabled && name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                        ? name[..^9]
                        : name;

                    EditingMods.Add(new ModItemModel
                    {
                        FilePath = file,
                        FileName = name,
                        DisplayName = cleanName,
                        IsEnabled = !isDisabled,
                        FileSizeText = FormatFileSize(fi.Length),
                        ModifiedTimeText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    });
                }
            }
            catch { }
        }

        var enabledCount = EditingMods.Count(m => m.IsEnabled);
        EditingModCountText = EditingMods.Count == enabledCount
            ? $"{EditingMods.Count} 模组"
            : $"已启用 {enabledCount} / 共 {EditingMods.Count} 模组";

        OnPropertyChanged(nameof(EditingModCountText));
        OnPropertyChanged(nameof(HasEditingMods));
        ApplyModFilter();
    }

    private void ApplyModFilter()
    {
        FilteredEditingMods.Clear();
        var q = ModSearch.Trim();
        var matches = EditingMods.Where(m =>
            string.IsNullOrEmpty(q) ||
            m.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            m.FileName.Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var m in matches)
        {
            FilteredEditingMods.Add(m);
        }
        OnPropertyChanged(nameof(HasFilteredEditingMods));
    }

    private void RefreshEditingResourcePacks()
    {
        EditingResourcePacks.Clear();
        if (EditingInstance is null)
        {
            OnPropertyChanged(nameof(HasEditingResourcePacks));
            return;
        }

        var dir = EditingResourcePacksDirectory;
        if (Directory.Exists(dir))
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir).OrderBy(d => Path.GetFileName(d)))
                {
                    var di = new DirectoryInfo(sub);
                    EditingResourcePacks.Add(new ResourcePackItemModel
                    {
                        FilePath = sub,
                        Name = di.Name,
                        IsDirectory = true,
                        SizeText = "文件夹",
                        ModifiedTimeText = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    });
                }

                foreach (var file in Directory.GetFiles(dir, "*.zip").OrderBy(f => Path.GetFileName(f)))
                {
                    var fi = new FileInfo(file);
                    EditingResourcePacks.Add(new ResourcePackItemModel
                    {
                        FilePath = file,
                        Name = fi.Name,
                        IsDirectory = false,
                        SizeText = FormatFileSize(fi.Length),
                        ModifiedTimeText = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")
                    });
                }
            }
            catch { }
        }

        EditingResourcePackCountText = $"{EditingResourcePacks.Count} 资源包";
        OnPropertyChanged(nameof(EditingResourcePackCountText));
        OnPropertyChanged(nameof(HasEditingResourcePacks));
    }

    private void RefreshEditingWorlds()
    {
        EditingWorlds.Clear();
        if (EditingInstance is null)
        {
            OnPropertyChanged(nameof(HasEditingWorlds));
            return;
        }

        var dir = EditingSavesDirectory;
        if (Directory.Exists(dir))
        {
            try
            {
                foreach (var sub in Directory.GetDirectories(dir).OrderByDescending(d => Directory.GetLastWriteTime(d)))
                {
                    var di = new DirectoryInfo(sub);
                    EditingWorlds.Add(new WorldItemModel
                    {
                        FolderPath = sub,
                        Name = di.Name,
                        LastPlayedText = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm"),
                        SizeText = "世界存档"
                    });
                }
            }
            catch { }
        }

        EditingWorldCountText = $"{EditingWorlds.Count} 世界";
        OnPropertyChanged(nameof(EditingWorldCountText));
        OnPropertyChanged(nameof(HasEditingWorlds));
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024.0):F1} MB";
    }


    [RelayCommand]
    private void ToggleInstJavaChooser() => IsInstJavaChooserOpen = !IsInstJavaChooserOpen;

    [RelayCommand]
    private void SelectInstJava(JavaOptionModel option)
    {
        foreach (var o in InstJavaOptions) o.IsSelected = ReferenceEquals(o, option);
        ConfigInstJavaPath = option.Path;
        ConfigInstJavaLabel = option.Label;
        IsInstJavaChooserOpen = false;
    }

    [RelayCommand]
    public void SetInstJavaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var runtime = JavaRuntimeLocator.Probe(path, Path.GetFileName(Path.GetDirectoryName(path)!));
        if (runtime is null)
        {
            ShowBanner("指定的路径不是有效的 Java 运行时。");
            return;
        }

        ConfigInstJavaPath = runtime.ExecutablePath;
        ConfigInstCustomJava = true;
        RefreshInstJavaOptions();
        IsInstJavaChooserOpen = false;
    }

    private void RefreshInstJavaOptions()
    {
        InstJavaOptions.Clear();

        InstJavaOptions.Add(new JavaOptionModel
        {
            Label = "自动（按版本要求挑选）",
            Path = null,
            IsSelected = string.IsNullOrEmpty(ConfigInstJavaPath)
        });

        foreach (var runtime in JavaRuntimeLocator.FindAll())
        {
            InstJavaOptions.Add(new JavaOptionModel
            {
                Label = $"Java {runtime.MajorVersion} · {runtime.Label}",
                Path = runtime.ExecutablePath,
                IsSelected = string.Equals(ConfigInstJavaPath, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            });
        }

        if (string.IsNullOrEmpty(ConfigInstJavaPath))
        {
            ConfigInstJavaLabel = "自动（按版本要求挑选）";
        }
        else
        {
            var matched = InstJavaOptions.FirstOrDefault(o => o.IsSelected);
            ConfigInstJavaLabel = matched?.Label ?? Path.GetFileName(ConfigInstJavaPath);
        }
    }

    /// <summary>
    /// The version browser already lists everything that can be installed, so "new" is a jump there
    /// rather than a second copy of the same list.
    /// </summary>
    [RelayCommand]
    private void NewInstance()
    {
        CurrentResCategory = "game";
        CurrentPage = "download";
        ShowBanner("选择一个版本开始安装，装好后会自动成为当前实例。");
    }

    [RelayCommand]
    private async Task RenameInstanceAsync(InstanceModel instance)
    {
        var name = await ShowDialogAsync("重命名实例", instance.Name, "新名字", instance.Name, "保存");
        if (name is null || string.IsNullOrWhiteSpace(name)) return;

        _launcher.Instances.Rename(instance.Id, name);
        RefreshInstances();
    }

    [RelayCommand]
    private async Task DeleteInstanceAsync(InstanceModel instance)
    {
        var ok = await ShowDialogAsync("删除实例",
            $"「{instance.Name}」的版本文件夹会一并删除，存档也在里面。确定吗？", null, null, "删除");
        if (ok is null) return;

        if (CurrentInstance is { } current && current.Id == instance.Id)
        {
            _launcher.SelectInstance(null);
        }

        _launcher.Instances.Delete(instance.Id);
        RefreshInstances();
    }

    [RelayCommand]
    private void OpenInstanceFolder(InstanceModel instance) =>
        OpenFolder(_launcher.Instances.GameDirectoryFor(instance.Source));

    /// <summary>配置弹窗里的「打开模组目录」：目录还没生成过就先建出来，开一个不存在的路径只会报错。</summary>
    [RelayCommand]
    private void OpenEditingMods()
    {
        if (EditingInstance is not { } instance) return;

        var directory = Path.Combine(_launcher.Instances.GameDirectoryFor(instance.Source), "mods");
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            ShowBanner($"建不出 mods 目录：{ex.Message}");
            return;
        }

        OpenFolder(directory);
    }

    private void RefreshInstances()
    {
        var currentId = _launcher.CurrentInstance?.Id;

        Instances.Clear();
        foreach (var instance in _launcher.Instances.Instances)
        {
            Instances.Add(new InstanceModel(instance) { IconBitmap = IconFor(instance) });
        }

        CurrentInstance = Instances.FirstOrDefault(i => i.Id == currentId) ?? Instances.FirstOrDefault();

        // 下拉只列真出现过的加载器**种类**：tag 上写的是「Fabric 0.16.10」这种带版本的一整串，
        // 按整串列就是装了五个 Fabric 出五个选项，撑爆工具条。
        LoaderChoices.Clear();
        LoaderChoices.Add(new LoaderOptionModel(LoaderOptionModel.AllName));
        foreach (var loader in Instances.Select(i => i.LoaderKind)
                     .Distinct(StringComparer.Ordinal)
                     .Where(x => x != LoaderOptionModel.AllName)
                     .OrderBy(x => x, StringComparer.CurrentCulture))
        {
            LoaderChoices.Add(new LoaderOptionModel(loader));
        }
        MarkLoaderSelected();
        OnPropertyChanged(nameof(HasInstances));
        ApplyInstanceFilter();
        RefreshFolders();
    }

    private void ApplyInstanceFilter()
    {
        var query = InstanceSearch.Trim();

        var hits = Instances.Where(m =>
            (InstanceLoader == LoaderOptionModel.AllName || m.LoaderKind == InstanceLoader) &&
            (query.Length == 0 ||
             m.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
             m.MetaText.Contains(query, StringComparison.OrdinalIgnoreCase)));

        // 默认按最近游玩排：没启动过的当成"最久远"沉到底，而不是插在最前面。
        var sorted = InstanceSort == "名称"
            ? hits.OrderBy(m => m.Name, StringComparer.CurrentCulture)
            : hits.OrderByDescending(m => m.Source.LastPlayed ?? DateTime.MinValue);

        VisibleInstances.Clear();
        foreach (var model in sorted) VisibleInstances.Add(model);

        OnPropertyChanged(nameof(HasInstanceFilter));
        OnPropertyChanged(nameof(HasNoInstanceMatches));
        OnPropertyChanged(nameof(InstanceEmptyHint));
    }

    private PixelBuffer IconFor(Instance instance)
    {
        // 用户自己挑的预设最优先 —— 它压过加载器标志，也压过盘上可能残留的旧 icon.png。
        if (!string.IsNullOrEmpty(instance.IconPreset))
        {
            return BlockIcon(instance.IconPreset);
        }

        if (!string.IsNullOrEmpty(instance.IconPath) && File.Exists(instance.IconPath))
        {
            if (_iconCache.TryGetValue(instance.IconPath, out var cached)) return cached;

            try
            {
                // Instance icons are icon.png, so the shared PNG decoder covers them.
                var loaded = PngCodec.Decode(File.ReadAllBytes(instance.IconPath)).ToPixelBuffer();
                _iconCache[instance.IconPath] = loaded;
                return loaded;
            }
            catch
            {
                // A corrupt (or non-PNG) icon falls through to the generated block below.
            }
        }

        // 装了加载器就挂那个加载器的标志 —— 和 HMCL 等启动器 一个道理：一眼能认出这个实例是
        // Forge 还是 Fabric，比给每个实例随机挑个方块有用得多。
        if (LoaderIconName(instance.Loader) is { } logo && BuiltinIcons.Load(logo) is { } bitmap)
        {
            return bitmap;
        }

        // Forge 不用内置标志，用游戏里的铁砧方块 —— 详见 ForgeBlockType 的注释。
        if (LoaderBlockType(instance.Loader) is { } loaderBlock)
        {
            return BlockIcon(loaderBlock);
        }

        // 原版（或标志读不到）：按 id 散列挑一个方块图标。
        // GetHashCode is randomised per process, so hash by hand to keep the icon stable.
        var hash = instance.Id.Aggregate(0, (acc, c) => acc * 31 + c);
        return BlockIcon(BlockIcons[Math.Abs(hash) % BlockIcons.Length]);
    }

    /// <summary>
    /// 取某个方块的图标：<b>直接用内嵌的 1024x1024 原图，不预缩</b>。
    ///
    /// <para>源的来历决定了「预缩」这一步只有坏处：ccVaults 那批 1024 图其实是 64x64 的渲染
    /// 最近邻放大 16 倍（实测 RGB/alpha 都是严格的 16 像素块），有效分辨率上限就是 64x64。
    /// 中间夹任何一次重采样都只是再插值一遍 —— 缩到 128 再缩到 80、或者直接 1024 一次缩到 80，
    /// 最终落在屏幕上的信息量是一样的，但多一次就是多一层软化。</para>
    ///
    /// <para>所以现在把 1024 原图直接交给 WPF，由它<b>一次</b>缩到设备像素。代价是解码慢一点
    /// （18 张全解约 0.5 秒，但只在真用到的那几块上发生，且有
    /// <see cref="EmbeddedImages"/> 缓存兜着），换来的是「用户要原图就真的是原图」。</para>
    ///
    /// <para>代价是解码慢一点、内存大一点：单张 1024 解码约 25 ms，缓存里每块占 4 MB
    /// （<see cref="EmbeddedImages"/> 一份 + 视图层位图一份）。实例数 × 不同方块的组合一般
    /// 在个位数，够用；**真要省就把这里改成 <c>BlockAssets.Load(type, 256)</c>** ——
    /// 256 对 40 DIP 的槽位仍是 3 倍余量，肉眼无差。</para>
    ///
    /// <para>取不到（资源没打进去 / 解码失败）就退回程序化画的立方体，不让图标开天窗。</para>
    /// </summary>
    private PixelBuffer BlockIcon(string type)
    {
        if (BlockAssets.Load(type) is { } bitmap) return bitmap;

        return PixelArt.CreateBlockCube(type, BlockIconSize);
    }

    /// <summary>兜底程序化立方体的边长。真实图标走 1024 原图，这条只在资源缺失时出现。</summary>
    private const int BlockIconSize = 128;

    /// <summary>
    /// <see cref="Instance.Loader"/> 的第一段（<c>Fabric 0.16.10</c> → <c>Fabric</c>）
    /// 映射到<b>内置标志</b>的文件名 —— 装了谁的加载器就挂谁的标志，和 HMCL 等启动器 一个道理：
    /// 一眼能认出这个实例是 Forge 还是 Fabric，比给每个实例随机挑个方块有用得多。
    ///
    /// <para>Forge 不在这张表里：它改走 <see cref="ForgeBlockType"/>（游戏里的铁砧方块）。
    /// OptiFine 也没有 —— 它闭源，官方没有任何能当方形标志用的图。
    /// 这两家之外读不到标志时，回落到按 id 散列的方块。</para>
    /// </summary>
    private static string? LoaderIconName(string loader) =>
        loader.Split(' ')[0].ToLowerInvariant() switch
        {
            "fabric" => "fabric",
            "quilt" => "quilt",
            "neoforge" => NeoForgeIcon,
            _ => null,
        };

    /// <summary>
    /// 有些加载器不用内置标志，而是指定一块<b>游戏里的方块</b>来当图标。
    /// 目前只有 Forge —— 它的官方标志是打在自己 jar 里的深蓝底白铁砧，
    /// 但那套深蓝跟界面不搭；玩家认知里 Forge 就是「铁砧」，直接用 <c>anvil</c> 方块渲染，
    /// 和其他实例的方块图标是同一套观感，也仍然是「谁的加载器就认得出是谁」。
    /// </summary>
    private static string? LoaderBlockType(string loader) =>
        loader.Split(' ')[0].ToLowerInvariant() switch
        {
            "forge" => ForgeBlockType,
            _ => null,
        };

    /// <summary>
    /// Forge 实例的图标方块。<b>故意不进 <see cref="BlockIcons"/> 那个散列池</b>：
    /// 铁砧整体偏深，被随机散列到会得到一块近乎黑的图标；它只该出现在「确定是 Forge」的时候。
    ///
    /// <para>铁砧<b>不是完整方块</b>（基座 + 收腰 + 砧颈 + 砧面四段，腿间镂空），所以
    /// <c>block_anvil.png</c> 是单独一张按它自己造型画的等距图，不是拿立方体模板套出来的。
    /// 早先按 jar 里 <c>template_anvil.json</c> 的四段坐标现拼过，投影偏移差一点就整体畸形，
    /// 于是换成内嵌预渲染图。</para>
    /// </summary>
    private const string ForgeBlockType = "anvil";

    /// <summary>
    /// NeoForge 用哪张图 —— 两版都随程序走，改这一个字符串即可切换：
    ///
    /// <list type="bullet">
    /// <item><c>neoforge</c>（默认）：NeoForge 官方那只会点头的狐狸
    /// （<c>neoforged/.github</c> 的 <c>neoforged_icon_16x16.png</c>，Unlicense）。
    /// 跟 HMCL 等启动器 用的是同一张图，玩家一眼对得上号。</item>
    /// <item><c>neoforge_negi</c>：从本项目 <c>app.png</c> 裁出来的橙狐狸。
    /// NeoForge 自己也是狐狸，想统一成自家风格（跟停靠栏、桌宠一套）时换成这个。</item>
    /// </list>
    /// </summary>
    private const string NeoForgeIcon = "neoforge";

    private void OpenFolder(string path)
    {
        try
        {
            NclPaths.EnsureDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBanner($"无法打开文件夹：{ex.Message}");
        }
    }

    // ==========================================================
    // Launch
    // ==========================================================

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private string _launchBtnText = "启动游戏";

    [ObservableProperty]
    private double _launchProgress;

    [ObservableProperty]
    private string _launchStepText = "";

    [ObservableProperty]
    private string _launchPctText = "0%";

    public bool CanLaunch => !IsLaunching && !IsGameRunning && CurrentInstance is not null;

    partial void OnIsLaunchingChanged(bool value) => OnPropertyChanged(nameof(CanLaunch));

    partial void OnIsGameRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        LaunchBtnText = value ? "游戏运行中" : "启动游戏";
    }

    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        if (IsLaunching || IsGameRunning) return;

        if (CurrentInstance is not { } instance)
        {
            ShowBanner("还没有实例。先到「资源 → 游戏」安装一个版本。");
            return;
        }

        if (CurrentAccount is not { } account)
        {
            ShowBanner("请先添加一个账户。");
            IsAccPopOpen = true;
            IsInstPopOpen = false;
            return;
        }

        IsLaunching = true;
        CurrentPage = "home";
        LaunchProgress = 0;
        LaunchPctText = "0%";
        LaunchStepText = "准备中";
        LaunchBtnText = "启动中";

        var task = BeginTask($"启动 {instance.Name}");
        var hidden = false;

        try
        {
            var session = await _launcher.LaunchAsync(instance.Source, account.Source, TrackLaunch(task));

            FinishTask(task, "已启动");
            IsLaunching = false;
            IsGameRunning = true;

            if (HideOnLaunch)
            {
                hidden = true;
                HideWindowRequested?.Invoke();
            }

            var exitCode = await session.WaitForExitAsync();
            if (exitCode != 0 && session.Diagnose() is { } diagnosis)
            {
                ShowBanner($"游戏异常退出（{exitCode}）：{diagnosis}");
            }
        }
        catch (Exception ex)
        {
            FailTask(task, ex);
            ShowBanner(ex is LaunchException ? ex.Message : $"启动失败：{ex.Message}");
        }
        finally
        {
            IsLaunching = false;
            IsGameRunning = _launcher.IsGameRunning;
            LaunchBtnText = IsGameRunning ? "游戏运行中" : "启动游戏";
            if (hidden) ShowWindowRequested?.Invoke();
            RefreshInstances();
        }
    }

    // ==========================================================
    // Downloads
    // ==========================================================

    public ObservableCollection<DownloadTaskModel> Downloads { get; } = new();

    public bool HasDownloads => Downloads.Count > 0;

    public void RefreshHasDownloads() => OnPropertyChanged(nameof(HasDownloads));

    /// <summary>The task list hangs off the download page as a popover instead of owning a page.</summary>
    [ObservableProperty]
    private bool _isDlPopOpen;

    [RelayCommand]
    private void ToggleDlPop()
    {
        IsDlPopOpen = !IsDlPopOpen;
        IsAccPopOpen = false;
        IsInstPopOpen = false;
    }

    [RelayCommand]
    private void ClearDownloads()
    {
        foreach (var task in Downloads.Where(t => t.IsFinished).ToList()) Downloads.Remove(task);
        OnPropertyChanged(nameof(HasDownloads));
    }

    private DownloadTaskModel BeginTask(string name)
    {
        var task = new DownloadTaskModel { Name = name, Cts = new CancellationTokenSource() };
        Downloads.Insert(0, task);
        OnPropertyChanged(nameof(HasDownloads));
        return task;
    }

    private static void FinishTask(DownloadTaskModel task, string status)
    {
        task.Status = status;
        task.Progress = 100;
        task.PercentText = "100%";
        task.Detail = "";
        task.IsFinished = true;
    }

    private static void FailTask(DownloadTaskModel task, Exception ex)
    {
        task.Status = "失败";
        task.Detail = ex.Message;
        task.IsFinished = true;
    }

    /// <summary>An aborted run is not a failure: keep the row resumable instead of red.</summary>
    private static void EndTask(DownloadTaskModel task, Exception ex)
    {
        if (task.Cts?.IsCancellationRequested == true)
        {
            task.Status = task.PauseRequested ? "已暂停" : "已取消";
            task.Detail = task.PauseRequested ? "点「继续」接着下，已完成的文件会直接跳过。" : "";
            task.IsFinished = true;
            return;
        }

        FailTask(task, ex);
    }

    [RelayCommand]
    private void PauseDownloadTask(DownloadTaskModel task)
    {
        task.PauseRequested = true;
        task.Cts?.Cancel();
    }

    [RelayCommand]
    private void CancelDownloadTask(DownloadTaskModel task)
    {
        task.PauseRequested = false;
        task.Cts?.Cancel();
    }

    [RelayCommand]
    private async Task ResumeDownloadTaskAsync(DownloadTaskModel task)
    {
        if (task.Retry is not { } retry) return;

        task.Cts?.Dispose();
        task.Cts = new CancellationTokenSource();
        task.PauseRequested = false;
        task.IsFinished = false;
        task.Status = "等待中";
        task.Detail = "";

        try
        {
            await retry(task.Cts.Token);
        }
        catch (Exception ex)
        {
            EndTask(task, ex);
        }
    }

    /// <summary>Progress reports arrive on downloader threads, so they are posted to the UI thread.</summary>
    private static IProgress<InstallProgress> Track(DownloadTaskModel task) =>
        new CallbackProgress<InstallProgress>(p => AppDispatcher.Current.Post(() => Apply(task, p)));

    /// <summary>The launch button's progress card mirrors the same reports the download page shows.</summary>
    private IProgress<InstallProgress> TrackLaunch(DownloadTaskModel task) =>
        new CallbackProgress<InstallProgress>(p => AppDispatcher.Current.Post(() =>
        {
            Apply(task, p);
            LaunchStepText = p.Stage;
            LaunchProgress = Math.Clamp(p.Fraction * 100, 0, 100);
            LaunchPctText = $"{(int)LaunchProgress}%";
        }));

    private static void Apply(DownloadTaskModel task, InstallProgress progress)
    {
        task.Status = progress.Stage;
        task.Progress = Math.Clamp(progress.Fraction * 100, 0, 100);
        task.PercentText = $"{(int)task.Progress}%";
        task.Detail = progress.Detail ?? "";
    }

    // ==========================================================
    // Resources
    // ==========================================================

    [ObservableProperty]
    private string _currentResCategory = "game";

    public ObservableCollection<ResourceModel> FilteredResources { get; } = new();

    [ObservableProperty]
    private string _resourceSearch = "";

    [ObservableProperty]
    private bool _isLoadingResources;

    [ObservableProperty]
    private string _resourceEmptyText = "暂无内容";

    public bool HasResources => FilteredResources.Count > 0;

    partial void OnCurrentResCategoryChanged(string value) => UpdateFilteredResources();

    partial void OnResourceSearchChanged(string value) => UpdateFilteredResources();

    [RelayCommand]
    private void SelectResCategory(string category) => CurrentResCategory = category;

    private void UpdateFilteredResources()
    {
        _resourceLoad?.Cancel();
        var token = (_resourceLoad = new CancellationTokenSource()).Token;

        if (CurrentResCategory == "game")
        {
            IsLoadingResources = false;
            ResourceEmptyText = _gameResources.Count == 0 ? "正在加载版本列表…" : "没有匹配的版本";
            FillFiltered(_gameResources);
            return;
        }

        _ = LoadModrinthAsync(CurrentResCategory, ResourceSearch.Trim(), token);
    }

    private void FillFiltered(IEnumerable<ResourceModel> source)
    {
        var query = ResourceSearch.Trim();

        FilteredResources.Clear();
        foreach (var item in source)
        {
            if (CurrentResCategory == "game" && query.Length > 0 &&
                !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredResources.Add(item);
        }

        OnPropertyChanged(nameof(HasResources));
    }

    private async Task LoadGameVersionsAsync()
    {
        try
        {
            var source = await _launcher.ResolveSourceAsync().ConfigureAwait(true);
            var manifest = await _launcher.Manifests.GetAsync(source).ConfigureAwait(true);

            var installed = new HashSet<string>(
                _launcher.Instances.Instances.Select(i => i.VersionId), StringComparer.OrdinalIgnoreCase);

            var icon = BlockAssets.Load("grass") ?? PixelArt.CreateBlockCube("grass");

            _gameResources.Clear();
            foreach (var version in manifest.Versions.Where(v => v.IsRelease).Take(200))
            {
                var known = installed.Contains(version.Id);
                _gameResources.Add(new ResourceModel
                {
                    Name = version.Id,
                    Desc = version.Description,
                    Category = "game",
                    VersionId = version.Id,
                    IconBitmap = icon,
                    ActionText = known ? "已安装" : "安装",
                    IsActionEnabled = !known,
                });
            }
        }
        catch (Exception ex)
        {
            ResourceEmptyText = $"版本列表加载失败：{ex.Message}";
        }

        if (CurrentResCategory == "game") UpdateFilteredResources();
    }

    private async Task LoadModrinthAsync(string category, string query, CancellationToken ct)
    {
        IsLoadingResources = true;
        ResourceEmptyText = "正在搜索…";
        FilteredResources.Clear();

        try
        {
            // Let typing settle before spending a request on every keystroke.
            await Task.Delay(350, ct).ConfigureAwait(true);

            if (CurrentInstance is not { } instance)
            {
                ResourceEmptyText = "先选择一个实例，才能按它的版本和加载器筛选。";
                return;
            }

            var projectType = category switch
            {
                "mod" => ModrinthClient.ProjectTypeMod,
                "rp" => ModrinthClient.ProjectTypeResourcePack,
                "shader" => ModrinthClient.ProjectTypeShader,
                _ => null,
            };

            if (projectType is null)
            {
                ResourceEmptyText = "整合包安装即将支持。";
                return;
            }

            var loader = LoaderFacet(instance.Source.Loader);
            var hits = await _modrinth.SearchAsync(projectType, query, 30, 0,
                instance.Source.VersionId, loader, ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested || CurrentResCategory != category) return;

            var categoryIcon = category switch
            {
                "mod" => "dirt",
                "shader" => "log",
                _ => "stone",
            };

            // 资源和实例列表共用一套方块图标，别在这儿另起炉灶。
            var icon = BlockAssets.Load(categoryIcon) ?? PixelArt.CreateBlockCube(categoryIcon);

            FilteredResources.Clear();
            foreach (var hit in hits)
            {
                FilteredResources.Add(new ResourceModel
                {
                    Name = hit.Title,
                    Desc = hit.Description,
                    Stat = FormatCount(hit.Downloads),
                    Category = category,
                    ProjectId = hit.Id,
                    IconBitmap = icon,
                });
            }

            ResourceEmptyText = query.Length == 0 ? "没有找到内容" : $"没有和「{query}」匹配的内容";
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        catch (Exception ex)
        {
            ResourceEmptyText = $"加载失败：{ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingResources = false;
                OnPropertyChanged(nameof(HasResources));
            }
        }
    }

    /// <summary>Only mods care which loader the instance uses; packs and shaders do not.</summary>
    private static string? LoaderFacet(string loader) => loader switch
    {
        "Fabric" => "fabric",
        "Quilt" => "quilt",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        _ => null,
    };

    private static string FormatCount(int downloads) => downloads switch
    {
        >= 100_000_000 => $"{downloads / 100_000_000.0:0.#}亿",
        >= 10_000 => $"{downloads / 10_000.0:0.#}万",
        _ => downloads.ToString(),
    };

    [RelayCommand]
    private async Task InstallResourceAsync(ResourceModel resource)
    {
        if (resource.Category == "game")
        {
            await InstallVersionAsync(resource);
        }
        else
        {
            await InstallModrinthAsync(resource);
        }
    }

    private async Task InstallVersionAsync(ResourceModel resource)
    {
        var versionId = resource.VersionId!;

        var name = await ShowDialogAsync("新建实例", $"将安装 Minecraft {versionId}。", "实例名称", versionId, "安装");
        if (name is null) return;

        var task = BeginTask($"安装 {versionId}");
        task.Retry = ct => RunVersionInstallAsync(resource, name, task, ct);
        await RunVersionInstallAsync(resource, name, task, task.Cts!.Token);
    }

    private async Task RunVersionInstallAsync(ResourceModel resource, string name, DownloadTaskModel task, CancellationToken ct)
    {
        var versionId = resource.VersionId!;

        resource.IsActionEnabled = false;
        resource.ActionText = "安装中";

        try
        {
            var instance = await _launcher.CreateInstanceAsync(versionId, name, Track(task), ct);

            FinishTask(task, "已完成");
            resource.ActionText = "已安装";
            _launcher.SelectInstance(instance);
            RefreshInstances();
            ShowBanner($"「{instance.DisplayName}」已经可以启动了。");
        }
        catch (Exception ex)
        {
            EndTask(task, ex);
            resource.ActionText = "重试";
            resource.IsActionEnabled = true;
            if (task.Status is not ("已暂停" or "已取消")) ShowBanner($"安装 {versionId} 失败：{ex.Message}");
        }
    }

    private async Task InstallModrinthAsync(ResourceModel resource)
    {
        if (CurrentInstance is null)
        {
            ShowBanner("先在首页选择一个实例。");
            return;
        }

        var task = BeginTask($"下载 {resource.Name}");
        task.Retry = ct => RunModrinthInstallAsync(resource, task, ct);
        await RunModrinthInstallAsync(resource, task, task.Cts!.Token);
    }

    private async Task RunModrinthInstallAsync(ResourceModel resource, DownloadTaskModel task, CancellationToken ct)
    {
        if (resource.ProjectId is not { } projectId) return;

        if (CurrentInstance is not { } instance)
        {
            ShowBanner("先在首页选择一个实例。");
            return;
        }

        resource.IsActionEnabled = false;
        resource.ActionText = "下载中";

        try
        {
            var source = await _launcher.ResolveSourceAsync(ct);
            var loader = resource.Category == "mod" ? LoaderFacet(instance.Source.Loader) : null;
            var version = await _modrinth.FindVersionAsync(projectId, instance.Source.VersionId, loader, ct);

            if (version is null)
            {
                throw new InvalidOperationException($"没有适配 {instance.Source.VersionId} 的文件");
            }

            var gameDirectory = _launcher.Instances.GameDirectoryFor(instance.Source);
            await _modrinthInstaller.InstallAsync(version, gameDirectory, resource.Category, source,
                new CallbackProgress<DownloadProgress>(p => AppDispatcher.Current.Post(() =>
                {
                    task.Status = "下载中";
                    task.Progress = Math.Clamp(p.Percent * 100, 0, 100);
                    task.PercentText = $"{(int)task.Progress}%";
                    task.Detail = p.CurrentItem ?? "";
                })), ct);

            FinishTask(task, "已完成");
            resource.ActionText = "已安装";
            ShowBanner($"「{resource.Name}」已放进 {instance.Name} 的{CategoryName(resource.Category)}。");

            if (resource.Category == "mod" && instance.Source.Loader == "原版")
            {
                ShowBanner("这个实例没有安装 Mod 加载器，Mod 不会生效。");
            }
        }
        catch (Exception ex)
        {
            EndTask(task, ex);
            resource.ActionText = "重试";
            resource.IsActionEnabled = true;
            if (task.Status is not ("已暂停" or "已取消")) ShowBanner($"下载失败：{ex.Message}");
        }
    }

    private static string CategoryName(string category) => category switch
    {
        "mod" => "mods 文件夹",
        "rp" => "资源包文件夹",
        "shader" => "光影文件夹",
        _ => "游戏目录",
    };

    // ==========================================================
    // Settings
    // ==========================================================

    [ObservableProperty]
    private string _currentSettingsTab = "游戏";

    [ObservableProperty]
    private int _maxMemoryGb = 6;

    [ObservableProperty]
    private bool _versionIsolation = true;

    [ObservableProperty]
    private bool _hideOnLaunch;

    /// <summary>"ask" | "tray" | "exit" — see <c>LauncherSettings.CloseWindowBehavior</c>.</summary>
    [ObservableProperty]
    private string _closeWindowBehavior = "ask";

    [ObservableProperty]
    private string _downloadSource = "自动";

    [ObservableProperty]
    private int _downloadThreads = 16;

    [ObservableProperty]
    private bool _isDark = true;

    /// <summary>设置页那行"游戏目录"：报的是实例页正在看的这一个，跟着那颗胶囊走。</summary>
    public string GameRoot => _launcher.ActiveFolder.Path;

    public string SourceHint => DownloadSource switch
    {
        "官方" => "直连 Mojang，国内速度较慢。",
        "BMCLAPI" => "国内镜像，通常最快。",
        _ => "启动时各测一次，用更快的那个。",
    };

    [RelayCommand]
    private void SelectSettingsTab(string tab) => CurrentSettingsTab = tab;

    partial void OnCurrentSettingsTabChanged(string value)
    {
        // Release 里没有「地区」这一页（见 RegionFeature）。旧状态或调试通道把标签设成它时
        // 回退到「游戏」，否则右侧会是一张永远空着的卡片。设回去只会再进一次本方法，
        // 那次 value 是「游戏」，条件不成立就停了。
        if (value == RegionTab && !RegionFeature.Enabled)
        {
            CurrentSettingsTab = "游戏";
            return;
        }

        if (value == "Java") RefreshJavaOptions();
    }

    partial void OnMaxMemoryGbChanged(int value)
    {
        OnPropertyChanged(nameof(GlobalMemoryGbSummary));
        PersistSettings();
    }

    partial void OnVersionIsolationChanged(bool value) => PersistSettings();

    partial void OnHideOnLaunchChanged(bool value) => PersistSettings();

    partial void OnCloseWindowBehaviorChanged(string value) => PersistSettings();

    /// <summary>
    /// Raised when the window has to actually leave: the tray menu's exit item and the close
    /// prompt's "直接退出". Hiding is <see cref="HideWindowRequested"/>; this one is the real shutdown.
    /// </summary>
    public event Action? ExitApplicationRequested;

    [ObservableProperty]
    private bool _isClosePromptOpen;

    [ObservableProperty]
    private bool _closePromptRemember;

    /// <summary>
    /// Every way of leaving the window — the ✕ button, Alt+F4, the taskbar's close item — funnels
    /// through here so one policy covers all three.
    /// </summary>
    public void RequestClose()
    {
        switch (CloseWindowBehavior)
        {
            case "tray":
                HideWindowRequested?.Invoke();
                break;
            case "exit":
                ExitApplicationRequested?.Invoke();
                break;
            default:
                // 桌宠没在跑就不必问：这个询问窗存在的唯一理由是"直接退出会连同桌宠一起结束"
                // （弹窗文案原话），没有桌宠时那句话是空话 —— 直接退出即可。
                // 桌宠在跑才让用户在「收进托盘 / 直接退出」之间选。
                if (!IsPetActive)
                {
                    ExitApplicationRequested?.Invoke();
                    break;
                }
                ClosePromptRemember = false;
                IsClosePromptOpen = true;
                break;
        }
    }

    /// <summary>
    /// One command for both buttons of the prompt; the parameter says which one was pressed.
    /// Ticking 不再询问 promotes this choice into the setting, so the next close skips the dialog.
    /// </summary>
    [RelayCommand]
    private void AnswerClose(string action)
    {
        if (ClosePromptRemember) CloseWindowBehavior = action;

        IsClosePromptOpen = false;
        ClosePromptRemember = false;

        if (action == "tray") HideWindowRequested?.Invoke();
        else ExitApplicationRequested?.Invoke();
    }

    [RelayCommand]
    private void CancelClose()
    {
        IsClosePromptOpen = false;
        ClosePromptRemember = false;
    }

    [RelayCommand]
    private void SetCloseWindowBehavior(string code) => CloseWindowBehavior = code;

    partial void OnDownloadThreadsChanged(int value)
    {
        if (_applyingSettings) return;

        // Both downloaders cap connections by this count; rebuild so the next task picks it up.
        _launcher.Settings.DownloadThreads = value;
        _modrinthInstaller = new ModrinthInstaller(concurrency: value);
        PersistSettings();
    }

    partial void OnDownloadSourceChanged(string value)
    {
        OnPropertyChanged(nameof(SourceHint));
        if (_applyingSettings) return;

        _launcher.Settings.DownloadSource = ParseSource(value);
        _launcher.InvalidateSource();
        PersistSettings();
    }

    [RelayCommand]
    private void SetDownloadSource(string source) => DownloadSource = source;

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        ApplyTheme();
        if (!IsCustomBackground)
        {
            BgArt = PixelArt.CreateBackground(IsDark);
        }

        PersistSettings();
    }

    private void ApplyTheme() => ThemeChanged?.Invoke(IsDark);

    [RelayCommand]
    private void OpenGameRoot() => OpenFolder(GameRoot);

    // ---- Java ----

    public ObservableCollection<JavaOptionModel> JavaOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalJavaSummary))]
    private string _javaLabel = "自动";

    [ObservableProperty]
    private bool _isJavaChooserOpen;

    [RelayCommand]
    private void ToggleJavaChooser() => IsJavaChooserOpen = !IsJavaChooserOpen;

    private void RefreshJavaOptions()
    {
        var selected = _launcher.Settings.JavaPath;

        JavaOptions.Clear();
        JavaOptions.Add(new JavaOptionModel { Label = "自动（按版本挑选）", Path = null, IsSelected = selected is null });

        foreach (var runtime in JavaRuntimeLocator.FindAll())
        {
            JavaOptions.Add(new JavaOptionModel
            {
                Label = $"Java {runtime.MajorVersion} · {runtime.Label}",
                Path = runtime.ExecutablePath,
                IsSelected = string.Equals(selected, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase),
            });
        }

        JavaLabel = JavaOptions.FirstOrDefault(o => o.IsSelected)?.Label ?? "自动";
    }

    [RelayCommand]
    private void SelectJava(JavaOptionModel option)
    {
        foreach (var o in JavaOptions) o.IsSelected = ReferenceEquals(o, option);

        _launcher.Settings.JavaPath = option.Path;
        JavaLabel = option.Label;
        IsJavaChooserOpen = false;
        PersistSettings();
    }

    [RelayCommand]
    private void SetJavaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var runtime = JavaRuntimeLocator.Probe(path, Path.GetFileName(Path.GetDirectoryName(path)!));
        if (runtime is null)
        {
            ShowBanner("这个文件不是可用的 Java 运行时，请选择 javaw.exe。");
            return;
        }

        _launcher.Settings.JavaPath = runtime.ExecutablePath;
        RefreshJavaOptions();
        IsJavaChooserOpen = false;
        PersistSettings();
        ShowBanner($"已选择 Java {runtime.MajorVersion}。");
    }

    // ---- persistence ----

    private void ApplySettingsToUi(Core.Settings.LauncherSettings settings)
    {
        // ⚠️ 这里每一个 VM 属性赋值都会触发它自己的 PersistSettings。**必须全部落在
        // _applyingSettings 守卫内**，否则 PersistSettings 会拿"还没读回来的默认值"把
        // settings 里的真值覆盖掉。
        //
        // 曾经的排布是把 CustomBackgroundPath / BgBlur / BgBrightness 放在 finally 之后：
        // 一有自定义背景图，CustomBackgroundPath 的 setter 就先跑一次 PersistSettings，
        // 那一刻 BgBlur/BgBrightness 还是默认 0 ⇒ 把 settings 的亮度/模糊清零，
        // 紧接着的两行"读回"读到的自然也是 0。症状 = 调好的亮度/模糊每次启动被重置。
        _applyingSettings = true;
        try
        {
            MaxMemoryGb = Math.Clamp(settings.MaxMemoryMb / 1024, 1, 32);
            VersionIsolation = settings.VersionIsolation;
            HideOnLaunch = settings.HideOnLaunch;
            CloseWindowBehavior = settings.CloseWindowBehavior;
            InstanceViewMode = settings.InstanceViewMode;
            DownloadThreads = settings.DownloadThreads;
            DownloadSource = settings.DownloadSource switch
            {
                SourceKind.Official => "官方",
                SourceKind.Bmclapi => "BMCLAPI",
                _ => "自动",
            };
            IsDark = settings.IsDark;

            if (!string.IsNullOrEmpty(settings.CustomBackgroundPath) &&
                File.Exists(settings.CustomBackgroundPath))
            {
                CustomBackgroundPath = settings.CustomBackgroundPath;
            }

            // 视频排在图片后面读：两个 setter 的互斥是"后写的赢"，这样设置里万一两个都有，
            // 结果稳定地按视频算（见 LauncherSettings.VideoBackgroundPath）。
            if (!string.IsNullOrEmpty(settings.VideoBackgroundPath) &&
                File.Exists(settings.VideoBackgroundPath))
            {
                VideoBackgroundPath = settings.VideoBackgroundPath;
            }

            BgBlur = settings.BackgroundBlur;
            BgBrightness = settings.BackgroundBrightness;
            BgPanX = settings.BackgroundPanX;
            BgPanY = settings.BackgroundPanY;
            BgZoom = settings.BackgroundZoom;
            BgContrast = settings.BackgroundContrast;
            BgSaturation = settings.BackgroundSaturation;
            BgHue = settings.BackgroundHue;
            BgTuningLeft = settings.BackgroundTuningLeft;
            BgTuningX = settings.BackgroundTuningX;
            BgTuningY = settings.BackgroundTuningY;
            HomeSkinModelVisible = settings.HomeSkinModelVisible;
            HomeSkinModelX = settings.HomeSkinModelX;
            HomeSkinModelY = settings.HomeSkinModelY;
            // 老配置里"静音"是一个独立布尔（默认 false）。第一次读进来要照它的意思把音量压成 0，
            // 否则更新完第一次放背景视频就是满音量糊脸。存回去时两个字段自洽，之后就恒等了。
            var storedVolume = Math.Clamp(settings.VideoBackgroundVolume, 0, 100);
            if (storedVolume > 0) _lastAudibleVolume = storedVolume;
            VideoBackgroundVolume = settings.VideoBackgroundSound ? storedVolume : 0;
        }
        finally
        {
            _applyingSettings = false;
        }

        ApplyTheme();
        OnPropertyChanged(nameof(SourceHint));
        JavaLabel = settings.JavaPath is null ? "自动" : Path.GetFileName(settings.JavaPath);
    }

    private static SourceKind ParseSource(string label) => label switch
    {
        "官方" => SourceKind.Official,
        "BMCLAPI" => SourceKind.Bmclapi,
        _ => SourceKind.Auto,
    };

    private void PersistSettings()
    {
        if (_applyingSettings) return;

        var settings = _launcher.Settings;
        settings.MaxMemoryMb = MaxMemoryGb * 1024;
        settings.VersionIsolation = VersionIsolation;
        settings.HideOnLaunch = HideOnLaunch;
        settings.CloseWindowBehavior = CloseWindowBehavior;
        settings.InstanceViewMode = InstanceViewMode;
        settings.DownloadThreads = DownloadThreads;
        settings.IsDark = IsDark;
        settings.CustomBackgroundPath = CustomBackgroundPath;
        settings.VideoBackgroundPath = VideoBackgroundPath;
        settings.BackgroundBlur = BgBlur;
        settings.BackgroundBrightness = BgBrightness;
        settings.BackgroundPanX = BgPanX;
        settings.BackgroundPanY = BgPanY;
        settings.BackgroundZoom = BgZoom;
        settings.BackgroundContrast = BgContrast;
        settings.BackgroundSaturation = BgSaturation;
        settings.BackgroundHue = BgHue;
        settings.BackgroundTuningLeft = BgTuningLeft;
        settings.BackgroundTuningX = BgTuningX;
        settings.BackgroundTuningY = BgTuningY;
        settings.HomeSkinModelVisible = HomeSkinModelVisible;
        settings.HomeSkinModelX = HomeSkinModelX;
        settings.HomeSkinModelY = HomeSkinModelY;
        // 这个布尔现在只是"音量 > 0"的别名：留着写是为了让旧版本读到的还是对的意思，
        // 也是新配置第一次读入时那个"照它压一次音量"的迁移依据。
        settings.VideoBackgroundSound = VideoBackgroundSound;
        settings.VideoBackgroundVolume = VideoBackgroundVolume;

        // Sliders fire on every tick; write once they settle.
        _saveDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _saveDebounce = debounce;
        _ = SaveAfterDelayAsync(debounce.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _launcher.SaveSettings();
    }

    // ==========================================================
    // Dialog & banner
    // ==========================================================

    [ObservableProperty]
    private bool _isDialogOpen;

    [ObservableProperty]
    private string _dialogTitle = "";

    [ObservableProperty]
    private string _dialogMessage = "";

    [ObservableProperty]
    private string _dialogInput = "";

    [ObservableProperty]
    private string _dialogPlaceholder = "";

    [ObservableProperty]
    private string _dialogConfirmText = "确定";

    [ObservableProperty]
    private bool _dialogHasInput;

    public bool HasDialogMessage => DialogMessage.Length > 0;

    partial void OnDialogMessageChanged(string value) => OnPropertyChanged(nameof(HasDialogMessage));

    /// <summary>
    /// Shows the in-window prompt. Returns the entered text, or "" for a confirm-only dialog, or
    /// null when the user cancelled.
    /// </summary>
    private Task<string?> ShowDialogAsync(string title, string message, string? placeholder,
        string? initial, string confirmText)
    {
        _dialogTcs?.TrySetResult(null);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogTcs = tcs;

        DialogTitle = title;
        DialogMessage = message;
        DialogHasInput = placeholder is not null;
        DialogPlaceholder = placeholder ?? "";
        DialogInput = initial ?? "";
        DialogConfirmText = confirmText;
        IsDialogOpen = true;

        return tcs.Task;
    }

    [RelayCommand]
    private void ConfirmDialog()
    {
        var tcs = _dialogTcs;
        _dialogTcs = null;
        IsDialogOpen = false;
        tcs?.TrySetResult(DialogHasInput ? DialogInput : "");
    }

    [RelayCommand]
    private void CancelDialog()
    {
        var tcs = _dialogTcs;
        _dialogTcs = null;
        IsDialogOpen = false;
        tcs?.TrySetResult(null);
    }

    [ObservableProperty]
    private string _bannerText = "";

    public bool IsBannerOpen => BannerText.Length > 0;

    partial void OnBannerTextChanged(string value) => OnPropertyChanged(nameof(IsBannerOpen));

    public void ShowBanner(string text)
    {
        BannerText = text;

        _bannerTimer?.Cancel();
        var timer = new CancellationTokenSource();
        _bannerTimer = timer;
        _ = DismissBannerAsync(timer.Token);
    }

    /// <summary>
    /// 视频后端出问题时由视图回调进来。<b>VM 里没有播放器</b>（<c>MediaPlayer</c> 是 WPF 类型，
    /// 本工程被 Avalonia 共用），所以"能不能播"只有视图知道。
    /// 这里顺手把视频清掉，让背景回落到生成图 —— 否则会留一块一直不动的空背景。
    /// </summary>
    public void ReportVideoBackgroundFailure(string message)
    {
        VideoBackgroundPath = null;
        ShowBanner(message);
    }

    private async Task DismissBannerAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(5000, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        BannerText = "";
    }

    [RelayCommand]
    private void DismissBanner()
    {
        _bannerTimer?.Cancel();
        BannerText = "";
    }
}
