using System;
using System.IO;
using System.Text.Json.Serialization;

namespace NegiCraftLauncher.Core.Settings;

public enum DownloadSource
{
    Official,
    Bmclapi,
    /// <summary>Probe both and keep whichever answered faster for this run.</summary>
    Auto,
}

/// <summary>
/// 一个 .minecraft 式游戏目录。<b>路径就是它的身份</b> —— 重命名只改显示名，实例元数据与
/// "当前实例"都按路径挂着，所以同一路径在列表里只能出现一次。
/// </summary>
public sealed class GameFolder
{
    public string Path { get; set; } = "";

    /// <summary>用户起的别名；留空时按路径末段生成（见 <see cref="LauncherSettings.SuggestFolderName"/>）。</summary>
    public string Name { get; set; } = "";

    /// <summary>新建与下载默认落到的那一个；列表里恰好一个为 true。</summary>
    public bool IsDefault { get; set; }

    /// <summary>
    ///     官方启动器目录（开机探测自动挂进来的那两个位置）。由启动器托管：
    ///     不许从列表里移除 —— 只要官方启动器还装着，这个入口就一直在。
    /// </summary>
    public bool IsOfficial { get; set; }

    /// <summary>这个目录里当前选中的实例；各目录各记各的，切回来不用重选。</summary>
    public string? CurrentInstanceId { get; set; }
}

/// <summary>
/// 一次地区探测的落盘结果。<see cref="Code"/> 是 ISO 3166-1 alpha-2，港澳台按国内口径写
/// <c>HK</c> / <c>MO</c> / <c>TW</c>；空字符串表示"探到了但没认出来是哪个国家"。
/// </summary>
public sealed class DetectedRegion
{
    public string Code { get; set; } = "";

    /// <summary>显示名（已经是本地化过的，例如「中国大陆」「日本」）。</summary>
    public string Name { get; set; } = "";

    /// <summary>省 / 市，可能为空 —— 只有 ipip 那一路给。</summary>
    public string? City { get; set; }

    public DateTime CheckedAt { get; set; }

    /// <summary>哪一家探到的（见 <c>RegionProbe</c>），排查"它怎么给我判成这个地区"时有用。</summary>
    public string Source { get; set; } = "";
}

/// <summary>
/// Everything the settings page edits, persisted as a single JSON document.
/// </summary>
public sealed class LauncherSettings
{
    public string GameRoot { get; set; } = NclPaths.DefaultGameRoot;

    /// <summary>实例页可以来回切的游戏目录；<see cref="GameRoot"/> 是其中默认那一个的旧字段。</summary>
    public List<GameFolder> GameFolders { get; set; } = new();

    /// <summary>实例页正在看的那个目录的路径。</summary>
    public string ActiveGameFolder { get; set; } = "";

    /// <summary>
    ///     官方启动器目录的首次探测做过了没有。做过就不再自动加 ——
    ///     用户把那个目录从列表里删掉之后，不该每次开机又被复活。
    /// </summary>
    public bool OfficialFolderProbed { get; set; }

    /// <summary>Explicit javaw.exe; null means pick one automatically per version.</summary>
    public string? JavaPath { get; set; }

    public int MaxMemoryMb { get; set; } = 4096;

    public DownloadSource DownloadSource { get; set; } = DownloadSource.Auto;

    /// <summary>
    ///     Open download connections: shared across files, and a single large file is split into
    ///     at most this many ranged segments.
    /// </summary>
    public int DownloadThreads { get; set; } = 16;

    /// <summary>实例页的排法："grid" 卡片墙，"list" 紧凑列表。只是看法，不影响任何东西。</summary>
    public string InstanceViewMode { get; set; } = "grid";

    /// <summary>When true each instance gets its own saves/mods/config under versions/&lt;id&gt;/.</summary>
    public bool VersionIsolation { get; set; } = true;

    public bool HideOnLaunch { get; set; }

    /// <summary>
    /// What the close button does: "ask" prompts every time, "tray" hides the window to the
    /// notification area, "exit" shuts the launcher down. Kept as a code rather than the label
    /// the UI shows, so rewording a button never invalidates a saved config.
    /// </summary>
    public string CloseWindowBehavior { get; set; } = "ask";

    public bool IsDark { get; set; } = true;

    /// <summary>
    ///     性能模式：关掉窗口级透明，让 WPF 走 GPU 合成。外框圆角会从逐像素 alpha 变成硬裁剪，
    ///     外投影也保不住原样 —— 这是换取动画顺滑的代价。下次启动生效。
    /// </summary>
    public bool PerformanceMode { get; set; }

    /// <summary>
    ///     界面动画的开法：<c>"system"</c>（跟随系统，默认）/ <c>"alwaysOn"</c> / <c>"alwaysOff"</c>。
    ///
    ///     <para>存成字符串而不是枚举，是为了让认不出的值能安全退回"跟随系统"
    ///     （见 <c>MotionPreferenceExtensions.Parse</c>）—— 存坏了不该让应用起不来。</para>
    /// </summary>
    public string MotionPreference { get; set; } = "system";

    public string? CustomBackgroundPath { get; set; }
    public double BackgroundBlur { get; set; }
    public double BackgroundBrightness { get; set; }

    /// <summary>
    ///     自选壁纸的取景：平移与缩放。
    ///
    ///     <para><b><see cref="BackgroundPanX" /> / <see cref="BackgroundPanY" /> 的单位是"可平移范围的
    ///     百分比"</b>，不是像素也不是窗口尺寸：图片按 cover 铺满后比窗口大出来的那部分就是总余量，
    ///     ±100 恰好看到对侧边。这样换窗口大小、换缩放都自洽，而且参数域本身就限死了不会露边。</para>
    ///
    ///     <para><see cref="BackgroundZoom" /> 的 <c>100</c> 就是今天的"居中裁切铺满"。</para>
    ///
    ///     <para>图片与视频共用这一组参数（数学在 <c>Raster/BackgroundFrame.cs</c> 一份）。
    ///     内置生成场景不吃它 —— 那张是 <c>Fill</c> 硬拉伸的像素画，加分数缩放会让像素宽窄不均。</para>
    /// </summary>
    public double BackgroundPanX { get; set; }
    public double BackgroundPanY { get; set; }
    public double BackgroundZoom { get; set; } = 100;

    /// <summary>
    ///     自选壁纸的调色：对比度 / 饱和度 −100..100（0 = 不变），色相 −180..180 度。
    ///
    ///     <para>与 <see cref="BackgroundBrightness" /> 不同，这三个是色彩矩阵的参数，只在共享层
    ///     <c>Raster/ImageGrade.cs</c> 作用于自选图片的像素上；<b>视频与内置场景都不吃它</b>
    ///     （视频要等着色器那条路，内置场景保持作者调好的原样）。</para>
    /// </summary>
    public double BackgroundContrast { get; set; }
    public double BackgroundSaturation { get; set; }
    public double BackgroundHue { get; set; }

    /// <summary>
    ///     主页那块 3D 皮肤模型是否出现。<b>默认 <c>true</c></b> —— 它本来就一直站在主页上，
    ///     这个开关给的是"今天不想看见它"的出口，不是要改默认长相。
    ///
    ///     <para>关掉时 <see cref="HomeSkinModelX" /> / <see cref="HomeSkinModelY" /> 一起清成
    ///     <c>null</c>：看不见的时候留着一份偏移没有意义，重新打开要回到设计锚点。</para>
    /// </summary>
    public bool HomeSkinModelVisible { get; set; } = true;

    /// <summary>
    ///     用户把主页的 3D 模型拖到了哪儿。<b>单位是首页可用区宽高的比例</b>（0..1），不是像素 ——
    ///     主窗口可缩放（<c>MinWidth=1080</c>），存绝对坐标的话换个窗口大小或换个系统缩放，
    ///     小人就会压到右下停靠卡上或者跑出画面。
    ///
    ///     <para><c>null</c> ＝ "没拖过"，视图走设计锚点 <c>92,44</c>。主页预览有像素回归基准钉在
    ///     那个锚点上，所以 null 这条路一个像素都不许动。</para>
    /// </summary>
    public double? HomeSkinModelX { get; set; }
    public double? HomeSkinModelY { get; set; }

    /// <summary>
    ///     背景调节窗口的位置。
    ///
    ///     <para><see cref="BackgroundTuningX" /> / <see cref="BackgroundTuningY" /> 是屏幕坐标，
    ///     <b>null 表示"用户没拖过"</b> —— 那时由视图按"贴主窗口一侧、那一侧放不下就翻边"现算。
    ///     被拖过之后就不再自动挪：这类小窗每次开机换地方很烦。</para>
    ///
    ///     <para><see cref="BackgroundTuningLeft" /> 记的是上一次贴了哪一边，只作参考 ——
    ///     真正的依据是每次开窗时那侧的屏幕余量，因为窗口可能被拖到另一个显示器旁边。</para>
    /// </summary>
    public bool BackgroundTuningLeft { get; set; }
    public double? BackgroundTuningX { get; set; }
    public double? BackgroundTuningY { get; set; }

    /// <summary>
    ///     视频壁纸。与 <see cref="CustomBackgroundPath" /> 互斥 —— 两个都设了就按视频算。
    ///
    ///     <para>只有 Windows(WPF) 侧会播它：Avalonia 侧没有视频面，读到非空值等于没有背景，
    ///     会回落到生成图。所以这个字段跨平台共享，语义是"用户在这台机器上选了这段视频当背景"，
    ///     而不是"背景一定是视频"。</para>
    /// </summary>
    public string? VideoBackgroundPath { get; set; }

    /// <summary>
    ///     视频壁纸是否出声。<b>默认 <c>false</c>（静音）</b>。
    ///
    ///     <para>⚠️ 现在它只是 <see cref="VideoBackgroundVolume"/> 非 0 的别名 —— 静音就是音量 0，
    ///     没有第二个开关。留着写它有两个理由：旧版本读到这份文件时意思还对得上；
    ///     新版第一次读旧配置时照它压一次音量（"静音着但音量留着 60"是很常见的组合，
    ///     不压就会在更新之后第一次放背景视频时糊脸）。</para>
    ///
    ///     <para>跨平台共享：Avalonia 侧没有视频面，读到什么都不用管。</para>
    /// </summary>
    public bool VideoBackgroundSound { get; set; }

    /// <summary>
    ///     视频壁纸音量，<c>0</c>–<c>100</c>，<b>0 就是静音</b>。<b>默认 <c>0</c></b> ——
    ///     背景视频是替用户放着的，不是他主动点开播的，默认出声太唐突。
    ///
    ///     <para>跨平台共享：Avalonia 侧没有视频面，读到什么都不用管。</para>
    /// </summary>
    public int VideoBackgroundVolume { get; set; }

    /// <summary>
    ///     旧版那一个全局「当前实例」。<b>只在第一次读配置时用来给默认目录补上
    ///     <see cref="GameFolder.CurrentInstanceId"/></b>，之后不再写 —— 现在每个目录各记各的。
    /// </summary>
    public string? CurrentInstanceId { get; set; }

    /// <summary>桌宠独立自定义名称；为 null 或空时继承当前主账号名称。</summary>
    public string? PetCustomName { get; set; }

    /// <summary>
    ///     桌宠是否用 GPU 硬件渲染。
    ///
    ///     <para>只有 Windows(WPF) 侧有这条后端 —— 那边是把模型塞进 <c>Viewport3D</c> 交给显卡；
    ///     Avalonia 侧本来就走 OpenGL，读到了也无处可用，直接忽略即可。所以这个字段跨平台共享、
    ///     语义是"我在这台机器上要不要开硬件后端"。</para>
    ///
    ///     <para>默认 false：软件光栅化是回归基线（确定性、可离屏出图），GPU 是可选加速项。</para>
    /// </summary>
    public bool PetUseGpu { get; set; }

    public string EffectiveGameRoot =>
        string.IsNullOrWhiteSpace(GameRoot) ? NclPaths.DefaultGameRoot : GameRoot;

    /// <summary>实例页正在看的那个目录；永远能在 <see cref="GameFolders"/> 里找到。</summary>
    [JsonIgnore]
    public GameFolder ActiveFolder => FindFolder(ActiveGameFolder) ?? GameFolders[0];

    /// <summary>新建与下载落进去的那个目录。</summary>
    [JsonIgnore]
    public GameFolder DefaultFolder => GameFolders.FirstOrDefault(f => f.IsDefault) ?? GameFolders[0];

    public GameFolder? FindFolder(string? path)
    {
        var key = NclPaths.NormaliseGameRoot(path ?? "");
        return key.Length == 0 ? null : GameFolders.FirstOrDefault(f => SamePath(f.Path, key));
    }

    /// <summary>
    /// 关联一个已有的游戏目录。<b>只登记，不动硬盘上的任何东西</b>；目录不存在或已经登记过都算失败，
    /// 失败原因写进 <paramref name="error"/> 给 UI 直接显示。
    /// </summary>
    public bool TryAddFolder(string path, string? name, out GameFolder? folder, out string error)
    {
        folder = null;
        error = "";

        var key = NclPaths.NormaliseGameRoot(path);
        if (key.Length == 0)
        {
            error = "请输入一个文件夹路径。";
            return false;
        }

        if (!Directory.Exists(key))
        {
            error = $"「{key}」不存在，或者不是一个文件夹。";
            return false;
        }

        if (FindFolder(key) is { } existing)
        {
            folder = existing;
            error = $"「{existing.Name}」已经在列表里了。";
            return false;
        }

        var added = new GameFolder
        {
            Path = key,
            Name = string.IsNullOrWhiteSpace(name) ? SuggestFolderName(key) : name.Trim(),
            // 列表还空着时（手改配置把默认项删了之类），第一个进来的就得当默认。
            IsDefault = GameFolders.Count == 0,
        };

        GameFolders.Add(added);
        folder = added;
        error = "";
        return true;
    }

    public bool TryRenameFolder(string path, string name)
    {
        if (FindFolder(path) is not { } folder || string.IsNullOrWhiteSpace(name)) return false;

        folder.Name = name.Trim();
        return true;
    }

    /// <summary>设为默认 = 新建与下载从此落在这里；默认标记整个列表只有一个。</summary>
    public bool TrySetDefaultFolder(string path)
    {
        if (FindFolder(path) is not { } target) return false;

        foreach (var folder in GameFolders) folder.IsDefault = ReferenceEquals(folder, target);
        return true;
    }

    /// <summary>
    /// 从列表里移除。<b>硬盘上的文件一个都不删</b>，实例元数据也留着 —— 再把同一个路径添加回来，
    /// 别名和实例名都还在。默认那个目录不许移除，否则新建就没了去处。
    /// </summary>
    public bool TryRemoveFolder(string path, out string error)
    {
        error = "";

        if (GameFolders.Count <= 1)
        {
            error = "至少要留一个游戏目录。";
            return false;
        }

        if (FindFolder(path) is not { } folder) return false;

        if (folder.IsDefault)
        {
            error = "默认目录不能移除，先把别的目录设为默认。";
            return false;
        }

        if (folder.IsOfficial)
        {
            error = "官方启动器的目录由启动器托管，不能移除。";
            return false;
        }

        GameFolders.Remove(folder);
        return true;
    }

    public bool TrySetActiveFolder(string path)
    {
        if (FindFolder(path) is not { } folder) return false;

        ActiveGameFolder = folder.Path;
        return true;
    }

    /// <summary>
    /// 没起别名时用的默认名：取游戏目录的<b>上一级</b>目录名 —— 游戏目录几乎都叫
    /// <c>.minecraft</c>，按尾段起名整张列表全重名；上一级（启动器文件夹 / 用户自己起的名）
    /// 才是有区分度的那层。上一级取不到（盘根）再退回尾段。
    /// </summary>
    public static string SuggestFolderName(string path)
    {
        var root = NclPaths.NormaliseGameRoot(path);
        var parent = Path.GetDirectoryName(root);
        var name = parent is { Length: > 0 } ? Path.GetFileName(parent) : Path.GetFileName(root);
        return name.Length == 0 ? root : name;
    }

    private static bool SamePath(string left, string right) =>
        string.Equals(NclPaths.NormaliseGameRoot(left), NclPaths.NormaliseGameRoot(right),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 目录列表自愈：去掉空路径与重复项，没有就按 <see cref="GameRoot"/> 补一个默认，
    /// 保证恰好一个默认、<see cref="ActiveGameFolder"/> 指得到人，并把 <see cref="GameRoot"/>
    /// 跟着默认目录走 —— 旧版本读到这份文件时看的还是那一个树。
    /// </summary>
    private void NormalizeFolders()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in GameFolders)
        {
            folder.Path = NclPaths.NormaliseGameRoot(folder.Path);
            folder.Name = string.IsNullOrWhiteSpace(folder.Name) ? SuggestFolderName(folder.Path) : folder.Name.Trim();
        }

        GameFolders.RemoveAll(f => f.Path.Length == 0 || !seen.Add(f.Path));

        var legacy = NclPaths.NormaliseGameRoot(EffectiveGameRoot);
        if (GameFolders.Count == 0)
        {
            GameFolders.Add(new GameFolder
            {
                Path = legacy,
                // 自建的那份默认目录没有更好的名字可起 —— 列表里就它一个时，它就是「当前文件夹」。
                // （官方启动器目录接管默认时，这个占位会被撤掉，见 Launcher.AdoptOfficialLauncherFolders。）
                Name = "当前文件夹",
                IsDefault = true,
                // 旧配置只有一个全局当前实例，它属于默认目录。
                CurrentInstanceId = CurrentInstanceId,
            });
        }

        if (!GameFolders.Any(f => f.IsDefault))
        {
            GameFolders[0].IsDefault = true;
        }
        else if (GameFolders.Count(f => f.IsDefault) > 1)
        {
            var kept = GameFolders.First(f => f.IsDefault);
            foreach (var folder in GameFolders) folder.IsDefault = ReferenceEquals(folder, kept);
        }

        if (FindFolder(ActiveGameFolder) is null) ActiveGameFolder = DefaultFolder.Path;

        // GameRoot 是旧字段：跟着默认目录走，别的版本读到它时看的还是同一棵树。
        GameRoot = DefaultFolder.Path;
    }

    /// <summary>
    /// 上次探测到的所在地区。用途只有一个：按地区决定某些内容 / 镜像给不给、从哪拿。
    /// <b>判断依据是出口 IP 的注册地</b>，所以换网络结论就变 —— 不读设备信息、不做定位。
    /// 为 <c>null</c> 表示还没探过（或从来没探到），功能侧按"未知"处理。
    /// </summary>
    public DetectedRegion? Region { get; set; }

    [JsonIgnore]
    public string FilePath { get; private set; } = NclPaths.SettingsFile;
    public static LauncherSettings Load(string? path = null)
    {
        path ??= NclPaths.SettingsFile;
        LauncherSettings? loaded = null;
        if (File.Exists(path))
        {
            try
            {
                loaded = NclJson.Deserialize<LauncherSettings>(File.ReadAllText(path));
            }
            // A corrupt settings file must not stop the launcher from starting; fall back to defaults.
            catch (Exception)
            {
                loaded = null;
            }
        }

        var settings = loaded ?? new LauncherSettings();
        settings.FilePath = path;
        settings.Clamp();
        return settings;
    }

    public void Save()
    {
        Clamp();
        NclPaths.EnsureLauncherDirectories();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, NclJson.Serialize(this));
    }

    private void Clamp()
    {
        NormalizeFolders();
        InstanceViewMode = InstanceViewMode == "list" ? "list" : "grid";
        MaxMemoryMb = Math.Clamp(MaxMemoryMb, 512, 65536);
        DownloadThreads = Math.Clamp(DownloadThreads, 1, 64);
        BackgroundBlur = Finite(BackgroundBlur, 0, 40, 0);
        BackgroundBrightness = Finite(BackgroundBrightness, -100, 100, 0);
        BackgroundPanX = Finite(BackgroundPanX, -100, 100, 0);
        BackgroundPanY = Finite(BackgroundPanY, -100, 100, 0);
        // 40..300：100 = cover 铺满，往下是"图比窗口小、外面露主题底色"。
        // 下限的真值在 Raster/BackgroundFrame.MinZoom（Core 不引 Raster，所以这里只能写数字 ——
        // 改那边记得改这里，两处都写了注释指认对方）。
        BackgroundZoom = Finite(BackgroundZoom, 40, 300, 100);
        BackgroundContrast = Finite(BackgroundContrast, -100, 100, 0);
        BackgroundSaturation = Finite(BackgroundSaturation, -100, 100, 0);
        BackgroundHue = Finite(BackgroundHue, -180, 180, 0);
        BackgroundTuningX = FiniteOrNull(BackgroundTuningX);
        BackgroundTuningY = FiniteOrNull(BackgroundTuningY);
        HomeSkinModelX = FractionOrNull(HomeSkinModelX);
        HomeSkinModelY = FractionOrNull(HomeSkinModelY);
    }

    /// <summary>
    /// 调节窗位置：非有限值退回 <c>null</c>（＝"没拖过，让视图自己算"），而不是退回 0 ——
    /// 屏幕坐标 0 是左上角，一个坏值会把窗口甩到屏幕外。
    /// </summary>
    private static double? FiniteOrNull(double? value) =>
        value is { } v && double.IsFinite(v) ? v : null;

    /// <summary>
    /// 主页模型的位置比例：非有限值退回 <c>null</c>（＝"没拖过"，走设计锚点），有限值夹进 0..1 ——
    /// 夹在范围外的手改配置会把小人甩到首页外面，再也拖不回来。
    /// </summary>
    private static double? FractionOrNull(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0.0, 1.0) : null;

    /// <summary>
    /// 背景这几个键会直接喂给变换矩阵和像素内核：<c>Math.Clamp(NaN,…)</c> 原样返回 NaN，
    /// 一旦漏进去，变换会 NaN 成"背景整个消失"、调色会 NaN 成全黑。所以非有限值一律回默认。
    /// </summary>
    private static double Finite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
