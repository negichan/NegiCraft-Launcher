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

    public bool Isolated => Source.Isolated;

    public string? JavaPath => Source.JavaPath;

    public int? MaxMemoryMb => Source.MaxMemoryMb;

    /// <summary>平台中立像素；由视图层转成自己的 Bitmap。</summary>
    [ObservableProperty]
    private PixelBuffer? _iconBitmap;

    [ObservableProperty]
    private bool _isCurrent;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(MetaText));
        OnPropertyChanged(nameof(Isolated));
        OnPropertyChanged(nameof(JavaPath));
        OnPropertyChanged(nameof(MaxMemoryMb));
    }
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
