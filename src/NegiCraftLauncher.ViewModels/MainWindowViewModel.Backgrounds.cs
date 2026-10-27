using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NegiCraftLauncher.Core.Settings;

namespace NegiCraftLauncher.ViewModels;

public partial class MainWindowViewModel
{
    public ObservableCollection<BackgroundAssetModel> BackgroundLibrary { get; } = new();
    public event Action<string>? BackgroundVideoRemoved;

    [ObservableProperty]
    private BackgroundAssetModel? _currentBackground;

    [ObservableProperty]
    private string _backgroundVideoQuality = "efficient";

    [ObservableProperty]
    private bool _isPreparingBackgroundVideo;

    [ObservableProperty]
    private double _backgroundVideoProgress;

    [ObservableProperty]
    private string _backgroundVideoStatus = "";

    [ObservableProperty]
    private bool _hasBackgroundVideoError;

    partial void OnBackgroundVideoQualityChanged(string value)
    {
        if (value is not ("efficient" or "clear" or "original"))
        {
            BackgroundVideoQuality = "efficient";
            return;
        }
        PersistSettings();
    }

    [RelayCommand]
    private void SetBackgroundVideoQuality(string quality) => BackgroundVideoQuality = quality;

    private void RememberBackground(string? path, bool video)
    {
        if (path is not null)
        {
            path = System.IO.Path.GetFullPath(path);
            var existing = BackgroundLibrary.FirstOrDefault(item =>
                item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new BackgroundAssetModel { Path = path, IsVideo = video };
                BackgroundLibrary.Add(existing);
            }
            existing.RefreshAvailability();
        }
        foreach (var item in BackgroundLibrary)
            item.IsSelected = item.Path.Equals(VideoBackgroundPath ?? CustomBackgroundPath,
                StringComparison.OrdinalIgnoreCase);
        CurrentBackground = BackgroundLibrary.FirstOrDefault(item => item.IsSelected);
    }

    [RelayCommand]
    private void UseSavedBackground(BackgroundAssetModel? item)
    {
        if (item is null) return;
        item.RefreshAvailability();
        if (!item.IsAvailable)
        {
            ShowBanner("背景文件已移动或删除，请重新添加。");
            return;
        }
        if (item.IsSelected) return;
        IsBgTuningOpen = false;
        if (item.IsVideo) VideoBackgroundPath = item.Path;
        else CustomBackgroundPath = item.Path;
    }

    [RelayCommand]
    private void RemoveSavedBackground(BackgroundAssetModel? item)
    {
        if (item is null || !BackgroundLibrary.Remove(item)) return;
        if (item.IsSelected)
        {
            IsBgTuningOpen = false;
            ResetBackground();
        }
        if (item.IsVideo) BackgroundVideoRemoved?.Invoke(item.Path);
        PersistSettings();
    }

    private void RestoreBackgroundLibrary(LauncherSettings settings)
    {
        BackgroundLibrary.Clear();
        foreach (var asset in settings.BackgroundLibrary)
        {
            try { RememberBackground(asset.Path, asset.IsVideo); }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        BackgroundVideoQuality = settings.BackgroundVideoQuality;
    }
}
