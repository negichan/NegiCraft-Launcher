using CommunityToolkit.Mvvm.ComponentModel;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Settings;
using NegiCraftLauncher.Raster;

namespace NegiCraftLauncher.ViewModels;

/// <summary>A card on the instances page and in the dock selector, backed by a real instance.</summary>
public partial class InstanceModel : ObservableObject
{
    public InstanceModel(Instance source) => Source = source;

    public Instance Source { get; }

    public string Id => Source.Id;

    public string Name => Source.DisplayName;

    public string MetaText => Source.MetaText;

    public string VersionId => Source.VersionId;

    /// <summary>加载器名（原版 / Fabric / Forge / NeoForge / Quilt / OptiFine …）。卡片的 tag 与筛选都吃它。</summary>
    public string Loader => Source.Loader;

    /// <summary>
    ///     tag 上写的是「Fabric 0.16.10」这样带版本的一整串，筛选下拉只按种类分组，
    ///     不然装了五个 Fabric 就是五个选项，撑爆工具条。
    /// </summary>
    public string LoaderKind => Loader.Split(' ')[0];

    /// <summary>列表模式那颗「N 模组」tag；没装模组就不摆，空 tag 是噪声。</summary>
    public bool HasMods => Source.ModCount > 0;

    public string ModCountText => $"{Source.ModCount} 模组";

    public bool Isolated => Source.Isolated;

    /// <summary>导出时保存框默认的文件名；名字里 Windows 不认的字符会被剔掉。</summary>
    public string ExportFileName
    {
        get
        {
            var name = InstanceManager.SanitizeId(Source.DisplayName);
            return (name.Length == 0 ? "整合包" : name) + ".mrpack";
        }
    }

    public string? JavaPath => Source.JavaPath;

    public int? MaxMemoryMb => Source.MaxMemoryMb;

    public string? ExtraJvmArguments => Source.ExtraJvmArguments;

    public bool PreallocateMemory => Source.PreallocateMemory;

    /// <summary>
    /// 卡片底行那句「最近游玩 2 小时前」。启动时 <c>GameLauncher</c> 会 <c>Touch()</c> 写下
    /// <see cref="Instance.LastPlayed" />，所以这边只读不写。
    ///
    /// <para>从没启动过时只说「未启动过」，不加"最近游玩"前缀 ——「最近游玩 未启动过」读着像坏了。</para>
    /// </summary>
    public string LastPlayedText => Source.LastPlayed is { } played ? "最近游玩 " + RelativeTime(played) : "未启动过";

    /// <summary>刚刚 / N 分钟前 / N 小时前 / 昨天 / N 天前 / N 周前。跨天用"昨天"而不是"1 天前"，是人的说法。</summary>
    private static string RelativeTime(DateTime when)
    {
        var span = DateTime.Now - when;
        if (span.TotalMinutes < 1) return "刚刚";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        if (span.TotalHours < 48) return "昨天";
        if (span.TotalDays < 168) return $"{(int)span.TotalDays} 天前";
        return $"{(int)(span.TotalDays / 7)} 周前";
    }

    /// <summary>平台中立像素；由视图层转成自己的 Bitmap。</summary>
    [ObservableProperty]
    private PixelBuffer? _iconBitmap;

    [ObservableProperty]
    private bool _isCurrent;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(VersionId));
        OnPropertyChanged(nameof(Loader));
        OnPropertyChanged(nameof(LoaderKind));
        OnPropertyChanged(nameof(HasMods));
        OnPropertyChanged(nameof(ModCountText));
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(Isolated));
        OnPropertyChanged(nameof(ExportFileName));
        OnPropertyChanged(nameof(JavaPath));
        OnPropertyChanged(nameof(MaxMemoryMb));
        OnPropertyChanged(nameof(ExtraJvmArguments));
        OnPropertyChanged(nameof(PreallocateMemory));
    }
}

/// <summary>
///     实例页顶上那颗胶囊与它展开的目录列表里的一行。<b>路径就是身份</b>（命令都按路径下发），
///     名字只是显示用的别名，所以重命名不会动到盘上的任何东西。
/// </summary>
public partial class GameFolderModel : ObservableObject
{
    public GameFolderModel(GameFolder source) => Source = source;

    public GameFolder Source { get; }

    public string Path => Source.Path;

    public string Name => Source.Name;

    public bool IsDefault => Source.IsDefault;

    /// <summary>官方启动器目录（开机自动挂进来的）：由启动器托管，列表里不给「移除」。</summary>
    public bool IsOfficial => Source.IsOfficial;

    /// <summary>目录不在了（挪了盘、拔了移动硬盘）：列表里直说，不然空列表看着像实例被删了。</summary>
    public bool Exists => Core.NclPaths.NormaliseGameRoot(Path).Length > 0 && System.IO.Directory.Exists(Path);

    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>导入对话框里"装到哪个目录"选中的那一个。XAML 比不了"这一行 == 当前选择"，只能把状态挂在行上。</summary>
    [ObservableProperty]
    private bool _isPackTarget;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(IsDefault));
        OnPropertyChanged(nameof(Exists));
    }
}

/// <summary>
/// 加载器筛选下拉里的一项。选中态单独挂出来，是因为 XAML 里"这一项 == 当前筛选"
/// 没有便宜的写法（DataTrigger 只能比一个绑定，ConverterParameter 又不能绑）。
/// </summary>
public partial class LoaderOptionModel : ObservableObject
{
    public const string AllName = "全部";

    public LoaderOptionModel(string name) => Name = name;

    public string Name { get; }

    /// <summary>「全部」在下拉里写成「全部加载器」，光一个"全部"看着像没选东西。</summary>
    public string Label => Name == AllName ? "全部加载器" : Name;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
///     「实例图标」里可选的一块预设方块 —— 键 + 中文名 + 已经解码好的位图。
///
///     <para>位图是<b>共享实例</b>（<c>BlockAssets</c> 的缓存），所以 18 个预设不会各解一份；
///     显示层再按引用缓存一次位图，整排方块就只占一份内存。</para>
/// </summary>
public partial class BlockIconModel : ObservableObject
{
    public BlockIconModel(string key, string name, PixelBuffer bitmap)
    {
        Key = key;
        Name = name;
        Bitmap = bitmap;
    }

    /// <summary>对应 <c>Assets/block_&lt;key&gt;.png</c>，也是写进 <c>Instance.IconPreset</c> 的值。</summary>
    public string Key { get; }

    public string Name { get; }

    public PixelBuffer Bitmap { get; }

    /// <summary>这个方块就是当前实例正在用的那块 —— 那排图标上给它描一圈高亮。</summary>
    [ObservableProperty]
    private bool _isSelected;
}

public partial class AccountModel : ObservableObject
{
    public AccountModel(GameAccount source) => Source = source;

    public GameAccount Source { get; }

    public string Id => Source.Id;

    public string Name => Source.Name;

    public string Type => Source.TypeName;

    public bool IsMicrosoft => Source.Type == GameAccountType.Microsoft;

    [ObservableProperty]
    private PixelBuffer? _avatarBitmap;

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>One row on the resources page: either a game version or a Modrinth project.</summary>
public partial class ResourceModel : ObservableObject
{
    public string Name { get; init; } = "";

    public string Desc { get; init; } = "";

    public string Stat { get; init; } = "";

    /// <summary>Tab id: game, mod, pack, rp or shader.</summary>
    public string Category { get; init; } = "";

    public PixelBuffer? IconBitmap { get; init; }

    /// <summary>Set on the game tab.</summary>
    public string? VersionId { get; init; }

    /// <summary>Modrinth project id, set on the mod / resource pack / shader tabs.</summary>
    public string? ProjectId { get; init; }

    [ObservableProperty]
    private string _actionText = "安装";

    [ObservableProperty]
    private bool _isActionEnabled = true;
}

/// <summary>A row on the download page. Install and launch work both report into it.</summary>
public partial class DownloadTaskModel : ObservableObject
{
    public string Name { get; init; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    private string _status = "等待中";

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _percentText = "0%";

    [ObservableProperty]
    private string _detail = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(CanResume))]
    private bool _isFinished;

    /// <summary>Aborts the run in flight; a resume creates a fresh one.</summary>
    public CancellationTokenSource? Cts { get; set; }

    /// <summary>Re-runs the whole operation; finished files are skipped, so it resumes in effect.</summary>
    public Func<CancellationToken, Task>? Retry { get; set; }

    public bool PauseRequested { get; set; }

    public bool IsRunning => !IsFinished;

    public bool CanResume => IsFinished && Status != "已完成";
}

/// <summary>A row in the Java runtime chooser. A null path means "pick automatically".</summary>
public partial class JavaOptionModel : ObservableObject
{
    public string Label { get; init; } = "";

    public string? Path { get; init; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>实例管理页「模组」标签里的一行。</summary>
public partial class ModItemModel : ObservableObject
{
    public ModItemModel()
    {
    }

    public ModItemModel(string displayName, string? modVersion)
    {
        DisplayName = displayName;
        ModVersionText = modVersion ?? "";
    }

    public string FilePath { get; set; } = "";

    [ObservableProperty]
    private string _fileName = "";

    /// <summary>给人看的名字：优先是 jar 清单里解析出的模组名，解析不到就是去掉后缀的文件名。</summary>
    [ObservableProperty]
    private string _displayName = "";

    /// <summary>jar 清单里声明的版本（fabric.mod.json / mods.toml …）。空串 = 没解析到，pill 不显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModVersion))]
    private string _modVersionText = "";

    public bool HasModVersion => ModVersionText.Length > 0;

    [ObservableProperty]
    private string _fileSizeText = "";

    [ObservableProperty]
    private string _modifiedTimeText = "";

    [ObservableProperty]
    private bool _isEnabled = true;
}

/// <summary>实例管理页「资源包」标签里的一行。</summary>
public partial class ResourcePackItemModel : ObservableObject
{
    public string FilePath { get; set; } = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _sizeText = "";

    [ObservableProperty]
    private string _modifiedTimeText = "";

    [ObservableProperty]
    private bool _isDirectory;
}

/// <summary>实例管理页「世界」标签里的一行。</summary>
public partial class WorldItemModel : ObservableObject
{
    public string FolderPath { get; set; } = "";

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _lastPlayedText = "";

    [ObservableProperty]
    private string _sizeText = "";
}
