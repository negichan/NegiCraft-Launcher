using System.Windows;
using System.Windows.Controls;
using NegiCraftLauncher.App.Media;

namespace NegiCraftLauncher.App.Controls;

public sealed class BackgroundThumbnail : Image
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.Register(
        nameof(Path), typeof(string), typeof(BackgroundThumbnail), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty IsVideoProperty = DependencyProperty.Register(
        nameof(IsVideo), typeof(bool), typeof(BackgroundThumbnail), new PropertyMetadata(false, Changed));
    private static readonly DependencyPropertyKey IsLoadingPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(IsLoading), typeof(bool), typeof(BackgroundThumbnail), new PropertyMetadata(false));
    public static readonly DependencyProperty IsLoadingProperty = IsLoadingPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey HasThumbnailPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasThumbnail), typeof(bool), typeof(BackgroundThumbnail), new PropertyMetadata(false));
    public static readonly DependencyProperty HasThumbnailProperty = HasThumbnailPropertyKey.DependencyProperty;
    private int _request;

    public string? Path { get => (string?)GetValue(PathProperty); set => SetValue(PathProperty, value); }
    public bool IsVideo { get => (bool)GetValue(IsVideoProperty); set => SetValue(IsVideoProperty, value); }
    public bool IsLoading => (bool)GetValue(IsLoadingProperty);
    public bool HasThumbnail => (bool)GetValue(HasThumbnailProperty);

    public BackgroundThumbnail()
    {
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Release();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Refresh();
            else Release();
        };
    }

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args) =>
        ((BackgroundThumbnail)target).Refresh();

    private void Release()
    {
        _request++;
        Source = null;
        SetValue(IsLoadingPropertyKey, false);
        SetValue(HasThumbnailPropertyKey, false);
    }

    private async void Refresh()
    {
        Release();
        if (!IsLoaded || !IsVisible || string.IsNullOrEmpty(Path)) return;
        var request = _request;
        SetValue(IsLoadingPropertyKey, true);
        var image = await BackgroundThumbnailCache.GetAsync(Path, IsVideo);
        if (request != _request || !IsLoaded || !IsVisible) return;
        Source = image;
        SetValue(HasThumbnailPropertyKey, image is not null);
        SetValue(IsLoadingPropertyKey, false);
    }
}
