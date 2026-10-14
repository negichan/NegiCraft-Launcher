using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.App.Services;

namespace NegiCraftLauncher.App;

public partial class MainWindow
{
    public static readonly DependencyProperty IsSettingsOpenProperty = DependencyProperty.Register(
        nameof(IsSettingsOpen), typeof(bool), typeof(MainWindow),
        new PropertyMetadata(false, (d, e) => ((MainWindow)d).SetSettingsOpen((bool)e.NewValue)));

    public bool IsSettingsOpen
    {
        get => (bool)GetValue(IsSettingsOpenProperty);
        set => SetValue(IsSettingsOpenProperty, value);
    }

    private int _settingsVersion;
    private IInputElement? _settingsPreviousFocus;
    private bool _settingsBackdropPrepared;
    private BitmapSource? _settingsBackdropCache;

    private void OnSettingsNavigationRequested(string page)
    {
        if (page == "settings")
        {
            _vm?.CloseAllPopoversCommand.Execute(null);
            IsSettingsOpen = true;
        }
        else
        {
            IsSettingsOpen = false;
        }
    }

    private void SetSettingsOpen(bool open)
    {
        var version = ++_settingsVersion;
        if (open)
        {
            SettingsScrim.BeginAnimation(UIElement.OpacityProperty, null);
            SettingsScrim.Opacity = 1;
            PageTransition.SetSuppressEnterAnimation(LauncherScene, true);
            if (!_settingsBackdropPrepared)
            {
                _settingsPreviousFocus = Keyboard.FocusedElement;
                _settingsBackdropCache ??= CaptureSettingsBackdrop();
                SettingsBackdrop.Source = _settingsBackdropCache;
                _settingsBackdropPrepared = true;
            }
            LauncherScene.Visibility = Visibility.Hidden;
            _bgToneTimer.Stop();
            PageSettings.Visibility = Visibility.Visible;
            PageSettings.IsHitTestVisible = true;
            Pop.SetIsOpen(SettingsDialog, true);
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (IsSettingsOpen) SettingsCloseButton.Focus();
            });
        }
        else
        {
            Pop.SetIsOpen(SettingsDialog, false);
            _ = FinishSettingsClose(version);
        }
    }

    private async Task FinishSettingsClose(int version)
    {
        // Restore the live scene under the opaque snapshot before fading it out.
        // Its pages stayed selected; revealing them must not replay their entry animations.
        LauncherScene.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
        {
            await Dispatcher.Yield(DispatcherPriority.Render);
            if (_settingsVersion != version || IsSettingsOpen) return;
            SettingsScrim.Opacity = 0;
            SettingsScrim.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(Pop.SlideCloseDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            await Task.Delay(Pop.SlideCloseDurationMs + 20);
        }
        if (_settingsVersion != version || IsSettingsOpen) return;

        PageSettings.Visibility = Visibility.Collapsed;
        SettingsScrim.BeginAnimation(UIElement.OpacityProperty, null);
        SettingsScrim.Opacity = 1;
        SettingsBackdrop.Source = null;
        _settingsBackdropPrepared = false;
        if (_vm?.IsVideoBackground == true) _settingsBackdropCache = null;
        PageTransition.SetSuppressEnterAnimation(LauncherScene, false);
        SyncToneSampling();
        if (_settingsPreviousFocus is UIElement { IsVisible: true, IsEnabled: true } previous)
            Keyboard.Focus(previous);
        _settingsPreviousFocus = null;
    }

    private BitmapSource? CaptureSettingsBackdrop() => SettingsBackdropRenderer.Capture(LauncherScene);

    private void OnSettingsCloseClick(object sender, RoutedEventArgs e) => IsSettingsOpen = false;

    private void OnSettingsFrameSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var frame = (FrameworkElement)sender;
        frame.Clip = new RectangleGeometry(new Rect(e.NewSize), 8, 8);
    }

    private void OnSettingsBackdropClick(object sender, MouseButtonEventArgs e)
    {
        IsSettingsOpen = false;
        e.Handled = true;
    }

    private void OnSettingsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is { IsClosePromptOpen: true }) return;
        IsSettingsOpen = false;
        e.Handled = true;
    }
}
