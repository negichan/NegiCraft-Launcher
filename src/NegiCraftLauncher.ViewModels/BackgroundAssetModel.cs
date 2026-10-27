using CommunityToolkit.Mvvm.ComponentModel;

namespace NegiCraftLauncher.ViewModels;

public sealed partial class BackgroundAssetModel : ViewModelBase
{
    public required string Path { get; init; }
    public bool IsVideo { get; init; }
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string KindLabel => IsVideo ? "视频" : "图片";
    public string IconKind => IsVideo ? "VideoFilm" : "ImageFrame";

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isAvailable;

    public void RefreshAvailability() => IsAvailable = File.Exists(Path);
}
