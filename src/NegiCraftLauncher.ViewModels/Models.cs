using CommunityToolkit.Mvvm.ComponentModel;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Instances;
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

    public bool Isolated => Source.Isolated;

    public string? JavaPath => Source.JavaPath;

    public int? MaxMemoryMb => Source.MaxMemoryMb;

    /// <summary>
    /// 卡片底行那句「最近游玩 2 小时前」。启动时 <c>GameLauncher</c> 会 <c>Touch()</c> 写下
    /// <see cref="Instance.LastPlayed" />，所以这边只读不写。
    ///
    /// <para>从没启动过时只说「未启动过」，不加"最近游玩"前缀 ——「最近游玩 未启动过」读着像坏了。</para>
    /// </summary>
    public string LastPlayedText => Source.LastPlayed is { } when ? "最近游玩 " + RelativeTime(when) : "未启动过";

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
        OnPropertyChanged(nameof(LastPlayedText));
        OnPropertyChanged(nameof(Isolated));
        OnPropertyChanged(nameof(JavaPath));
        OnPropertyChanged(nameof(MaxMemoryMb));
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

public partial class AccountModel : ObservableObject{
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
