using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.App.Services;
using NegiCraftLauncher.Theme;

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
    private string _settingsReturnPage = "home";

    /// <summary>空闲预热的防抖计时器：场景稳定下来之后再把一次性成本做掉，见 <see cref="ScheduleSettingsWarmup"/>。</summary>
    private readonly DispatcherTimer _settingsWarmupTimer = new(DispatcherPriority.Background)
    {
        Interval = TimeSpan.FromMilliseconds(1500),
    };

    private Panel SettingsNavPanel => (Panel)DialogSettingsNavHost.Content;
    private Panel SettingsCardsPanel => (Panel)DialogSettingsCardsHost.Content;

    private void InitializeSettingsViews()
    {
        var navigation = (DataTemplate)FindResource("SettingsNavTemplate");
        var cards = (DataTemplate)FindResource("SettingsCardsTemplate");
        PageSettingsNavHost.Content = navigation.LoadContent();
        DialogSettingsNavHost.Content = navigation.LoadContent();
        PageSettingsCardsHost.Content = cards.LoadContent();
        DialogSettingsCardsHost.Content = cards.LoadContent();

        _settingsWarmupTimer.Tick += (_, _) =>
        {
            _settingsWarmupTimer.Stop();
            WarmupSettingsDialog();
        };
    }

    private bool OnSettingsNavigationRequested(string page)
    {
        if (page == "settings" && _vm is { } vm)
        {
            if (vm.CurrentPage != "settings") _settingsReturnPage = vm.CurrentPage;
            if (vm.UseSettingsDialog)
            {
                OpenSettingsDialog();
                return true;
            }
        }
        IsSettingsOpen = false;
        return false;
    }

    public void OpenSettingsDialog()
    {
        _vm?.CloseAllPopoversCommand.Execute(null);
        IsSettingsOpen = true;
    }

    private void OnSettingsModeClick(object sender, RoutedEventArgs e)
    {
        if (_vm is not { } vm) return;
        vm.UseSettingsDialog = !vm.UseSettingsDialog;
        if (vm.UseSettingsDialog)
        {
            // Reveal the previous page without replaying its entrance behind the snapshot.
            PageTransition.SetSuppressEnterAnimation(LauncherScene, true);
            vm.GoCommand.Execute(_settingsReturnPage);
            LauncherScene.UpdateLayout();
            OpenSettingsDialog();
        }
        else
        {
            vm.GoCommand.Execute("settings");
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
            SettingsOverlay.Visibility = Visibility.Visible;
            SettingsOverlay.IsHitTestVisible = true;
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
                new DoubleAnimation(1, 0, Motion.Slow)
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                });
            await Task.Delay(Motion.Slow + TimeSpan.FromMilliseconds(20));
        }
        if (_settingsVersion != version || IsSettingsOpen) return;

        SettingsOverlay.Visibility = Visibility.Collapsed;
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

    /// <summary>快照作废：清缓存并安排一次空闲预热，这样下次打开设置不必现抓。</summary>
    private void InvalidateSettingsBackdrop()
    {
        _settingsBackdropCache = null;
        ScheduleSettingsWarmup();
    }

    /// <summary>
    /// 安排一次"空闲预热"。
    ///
    /// <para><b>为什么需要它</b>：抓一次背景快照要 220ms（<see cref="SettingsBackdropRenderer"/> 用
    /// <c>VisualBrush</c> 把整棵启动器场景重新栅格化一遍，输出虽只有 25% 大小，成本在源上），
    /// 而这 220ms 就是设置弹窗首次打开那一下卡顿的全部来源 —— 实测首开 240ms 里它占 220，
    /// 后续几次只要 2~3ms（缓存命中）。既然它只在"快照失效之后的第一次打开"发生，
    /// 就把这次抓取挪到没人看的时候做。</para>
    ///
    /// <para><b>为什么在等的时候不重置</b>：重置会让连续活动期间永远等不到窗口 ——
    /// 而"操作一阵子然后打开设置"正是最常见的路径。所以只要计时器已经在跑就让它跑完。</para>
    /// </summary>
    private void ScheduleSettingsWarmup()
    {
        if (IsSettingsOpen || _settingsWarmupTimer.IsEnabled) return;
        _settingsWarmupTimer.Start();
    }

    /// <summary>
    /// 把首开的一次性成本提前做掉：先抓背景快照，再让对话框在 <c>Visibility.Hidden</c> 下
    /// 排练一次布局。<c>Hidden</c> 只是不渲染，照样 Measure/Arrange —— 模板应用和布局都会
    /// 真的跑一遍，但屏上什么都看不到（也不用担心像 Visible 那样闪一下）。
    /// </summary>
    private void WarmupSettingsDialog()
    {
        // 刻意不看 UseSettingsDialog：那个开关决定的是"设置用弹窗还是整页"，默认关，
        // 而这里预热的是弹窗那份视图。按开关去预热等于默认情况下什么都不做。
        if (!IsLoaded || IsSettingsOpen) return;

        // 有动画在飞就先不动手：抓快照会让 UI 线程连续忙 220ms，落在过渡中间就是一次卡顿。
        // 让计时器下一轮再试（MotionEngine 有在飞过渡数，正好在这里派上用场）。
        if (Theme.MotionEngine.ActiveCount > 0)
        {
            _settingsWarmupTimer.Start();
            return;
        }

        _settingsBackdropCache ??= CaptureSettingsBackdrop();

        if (SettingsOverlay.Visibility == Visibility.Visible) return;
        SettingsOverlay.Visibility = Visibility.Hidden;
        SettingsOverlay.UpdateLayout();
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

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
