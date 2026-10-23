#if DEBUG
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using NegiCraftLauncher.Theme;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App;

/// <summary>
/// 设置页里那块 <c>Debug</c> 分区（左侧导航项 + 右侧卡片）。**只有 Debug 构建才存在。**
///
/// <para><b>为什么是"代码建 UI"而不是写在 MainWindow.xaml 里</b>：XAML 没有 <c>#if</c>，
/// 写进 XAML 就一定会被编成 BAML 进 Release 包。这个文件整体包在 <c>#if DEBUG</c> 里，
/// Release 下编译为空，连 <see cref="MainWindow.OnDebugSectionAttach"/> 的声明和调用点
/// 都被编译器一并消掉 —— "发版不编译"由编译器保证，而不是运行期藏起来。</para>
///
/// <para>挂载点是 XAML 里两个空面板（<c>SettingsNavPanel</c> / <c>SettingsCardsPanel</c>），
/// 它们本身不带任何 Debug 信息，Release 下就是两个普通容器。</para>
///
/// <para><b>状态不落盘</b>：这几个开关只活在内存里，重开就回默认。要持久化得往
/// <c>LauncherSettings</c>（Core 工程，Release 也会编译）里加字段，那就违背"发版不带 Debug"
/// 的初衷了。</para>
///
/// <para>⚠️ 改这里要同步 <c>App.Avalonia/Views/MainWindow.Debug.cs</c>，两边是逐行对照的两份。</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>设置页里这一项的标签。与其它标签一样是个普通字符串，只是不进 XAML。</summary>
    private const string DebugTab = "Debug";

    /// <summary>阴影两层的出厂不透明度。Tailwind shadow-lg 两层都是 10% 黑，
    /// 与 <c>MainWindow.xaml</c> 里两个 caster 的 <c>Opacity="0.10"</c> 对应。</summary>
    private const double BaseShadowOpacity = 0.10;

    /// <summary>两处模糊的出厂半径（px），也是滑块的默认值：
    /// 侧栏玻璃背板 18（对齐 mockup 的 <c>blur(18px)</c>）、
    /// 停靠条与启动进度卡共用的浮卡投影 30（对齐 <c>BoxShadow "0 10 30 -12"</c>）。</summary>
    private const double BaseSidebarBlur = 18;
    private const double BaseCardBlur = 30;

    private MainWindowViewModel? _dbgVm;
    private Button? _dbgNavButton;
    private Border? _dbgCard;
    private CheckBox? _dbgShadowToggle;
    private Slider? _dbgShadowOpacity;
    private TextBlock? _dbgShadowOpacityText;
    private Slider? _dbgSidebarBlur;
    private TextBlock? _dbgSidebarBlurText;
    private Slider? _dbgCardBlur;
    private TextBlock? _dbgCardBlurText;

    /// <summary>投影层那两个可改写的 Effect。XAML 里内联声明的 Freezable 可能被冻结，
    /// 所以建分区时先 <c>Clone()</c> 一份可写的挂回去。</summary>
    private DropShadowEffect? _dbgShadowWideEffect;
    private DropShadowEffect? _dbgShadowTightEffect;

    /// <summary>侧栏背板里那几层各自挂的 BlurEffect（像素场景 / 用户图片 / 视频，互斥显示）。
    /// 按树收而不是逐个点名：以后加一层背景不用回来改这里。</summary>
    private readonly List<BlurEffect> _sidebarBlurs = [];

    private BlurEffect? _dbgDockBlur;
    private BlurEffect? _dbgLaunchBlur;

    partial void OnDebugSectionAttach(MainWindowViewModel? vm)
    {
        EnsureDebugSection();

        if (!ReferenceEquals(_dbgVm, vm))
        {
            if (_dbgVm is not null) _dbgVm.PropertyChanged -= OnDebugVmPropertyChanged;
            _dbgVm = vm;
            if (_dbgVm is not null) _dbgVm.PropertyChanged += OnDebugVmPropertyChanged;
        }

        SyncDebugTab();
    }

    /// <summary>幂等：只建一次（DataContext 换过之后只是重新接线，不重建控件树）。</summary>
    private void EnsureDebugSection()
    {
        if (_dbgNavButton is not null) return;

        // 投影层的 Effect 换成可写副本，否则调不透明度会撞 Freezable 冻结。
        if (ShellShadowWide.Effect is DropShadowEffect wide)
        {
            _dbgShadowWideEffect = wide.Clone();
            ShellShadowWide.Effect = _dbgShadowWideEffect;
        }

        if (ShellShadowTight.Effect is DropShadowEffect tight)
        {
            _dbgShadowTightEffect = tight.Clone();
            ShellShadowTight.Effect = _dbgShadowTightEffect;
        }

        // 侧栏磨砂：背板里每一层各一份 BlurEffect，换成可写副本收上来。
        CollectSidebarBlurs(SidebarBackdrop);

        // 浮卡投影：两张卡共用 FloatShadowStyle，setter 里的 Freezable 会被冻结，
        // 所以各自 Clone 一份可写的挂回去。
        _dbgDockBlur = WritableBlur(DockShadow);
        _dbgLaunchBlur = WritableBlur(LaunchProgressShadow);

        _dbgNavButton = new Button
        {
            Content = DebugTab,
            Style = (Style)FindResource("SetNavBtnStyle"),
        };
        _dbgNavButton.Click += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm) vm.SelectSettingsTabCommand.Execute(DebugTab);
        };
        SettingsNavPanel.Children.Add(_dbgNavButton);

        _dbgCard = BuildDebugCard();
        SettingsCardsPanel.Children.Add(_dbgCard);
    }

    private Border BuildDebugCard()
    {
        var stack = new NegiStackPanel();

        stack.Children.Add(HintRow(
            "Debug 调试项",
            "仅在 Debug 模式可见；Release 打包时这段 UI 不会被编译进去。"));

        // ---- 窗口阴影 ----
        _dbgShadowToggle = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        _dbgShadowToggle.Checked += (_, _) => ApplyShadowState();
        _dbgShadowToggle.Unchecked += (_, _) => ApplyShadowState();
        stack.Children.Add(OptionRow("窗口阴影", "窗口外壳那圈投影（Tailwind shadow-lg，两层）。", _dbgShadowToggle));

        _dbgShadowOpacity = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 210 };
        _dbgShadowOpacityText = ValueText("100%");
        _dbgShadowOpacity.ValueChanged += (_, _) => ApplyShadowState();
        stack.Children.Add(SliderRow("阴影浓度", _dbgShadowOpacity, _dbgShadowOpacityText));

        // ---- 模糊半径（两处，出厂值就是 XAML 里写死的那个）----
        _dbgSidebarBlur = BlurSlider(BaseSidebarBlur);
        _dbgSidebarBlurText = ValueText($"{BaseSidebarBlur:0} px");
        _dbgSidebarBlur.ValueChanged += (_, _) => ApplySidebarBlurState();
        stack.Children.Add(SliderRow("侧栏磨砂模糊", _dbgSidebarBlur, _dbgSidebarBlurText));

        _dbgCardBlur = BlurSlider(BaseCardBlur);
        _dbgCardBlurText = ValueText($"{BaseCardBlur:0} px");
        _dbgCardBlur.ValueChanged += (_, _) => ApplyCardBlurState();
        stack.Children.Add(SliderRow("浮卡投影模糊", _dbgCardBlur, _dbgCardBlurText, divider: false));

        var card = new Border
        {
            Background = Solid(0x11, 0x11, 0x14),
            BorderBrush = Solid(0x26, 0x26, 0x2B),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 0, 18, 0),
            Visibility = Visibility.Collapsed,
            Child = stack,
        };

        ApplyShadowState();
        ApplySidebarBlurState();
        ApplyCardBlurState();
        return card;
    }

    // ---- 生效 ----

    private void ApplySidebarBlurState()
    {
        if (_dbgSidebarBlur is null || _dbgSidebarBlurText is null) return;

        _dbgSidebarBlurText.Text = $"{_dbgSidebarBlur.Value:0} px";
        foreach (var blur in _sidebarBlurs)
        {
            blur.Radius = _dbgSidebarBlur.Value;
        }
    }

    private void ApplyCardBlurState()
    {
        if (_dbgCardBlur is null || _dbgCardBlurText is null) return;

        _dbgCardBlurText.Text = $"{_dbgCardBlur.Value:0} px";
        // 停靠条与启动进度卡共用一个值（两张卡本来就共用 FloatShadowStyle）。
        if (_dbgDockBlur is not null) _dbgDockBlur.Radius = _dbgCardBlur.Value;
        if (_dbgLaunchBlur is not null) _dbgLaunchBlur.Radius = _dbgCardBlur.Value;
    }

    private void ApplyShadowState()
    {
        if (_dbgShadowToggle is null || _dbgShadowOpacity is null) return;

        var visibility = _dbgShadowToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        ShellShadowWide.Visibility = visibility;
        ShellShadowTight.Visibility = visibility;

        if (_dbgShadowOpacityText is not null)
        {
            _dbgShadowOpacityText.Text = $"{_dbgShadowOpacity.Value:0}%";
        }

        var opacity = BaseShadowOpacity * (_dbgShadowOpacity.Value / 100d);
        if (_dbgShadowWideEffect is not null) _dbgShadowWideEffect.Opacity = opacity;
        if (_dbgShadowTightEffect is not null) _dbgShadowTightEffect.Opacity = opacity;
    }

    private void SyncDebugTab()
    {
        var isDebug = (DataContext as MainWindowViewModel)?.CurrentSettingsTab == DebugTab;

        if (_dbgNavButton is not null) Negi.SetOn(_dbgNavButton, isDebug);
        if (_dbgCard is not null) _dbgCard.Visibility = isDebug ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDebugVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentSettingsTab)) SyncDebugTab();
    }

    // ---- 行构件（照抄 XAML 里那种卡的排布，只为不再造一套视觉） ----

    /// <summary>模糊半径滑块：0~60px，一格一像素。出厂值由调用方传，就是 XAML 里写死的那个半径。</summary>
    private static Slider BlurSlider(double value) => new()
    {
        Minimum = 0,
        Maximum = 60,
        Value = value,
        Width = 210,
        IsSnapToTickEnabled = true,
        TickFrequency = 1,
    };

    /// <summary>把目标上那份 BlurEffect 换成可写副本（原值带过来）并返回副本。</summary>
    private static BlurEffect? WritableBlur(FrameworkElement target)
    {
        if (target.Effect is not BlurEffect original) return null;

        var clone = original.Clone();
        target.Effect = clone;
        return clone;
    }

    /// <summary>递归收一棵<b>逻辑</b>树上的 BlurEffect。用逻辑树是因为这里在 DataContext
    /// 接线时就跑，视觉树还没建，<c>VisualTreeHelper</c> 会拿到空。</summary>
    private void CollectSidebarBlurs(DependencyObject node)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
        {
            if (child is UIElement ui && ui.Effect is BlurEffect original)
            {
                var clone = original.Clone();
                ui.Effect = clone;
                _sidebarBlurs.Add(clone);
            }

            CollectSidebarBlurs(child);
        }
    }

    /// <summary>卡片顶部那条说明行：有底色提示 + 下分隔线，高度自适应（文字会折行）。</summary>
    private static Border HintRow(string title, string desc) =>
        new()
        {
            Padding = new Thickness(0, 14, 0, 14),
            BorderBrush = Solid(0x26, 0x26, 0x2B),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Titled(title, desc),
        };

    private static Border OptionRow(string title, string desc, UIElement control, bool divider = true)
    {
        var grid = new NegiGrid { Columns = "* Auto" };
        grid.Children.Add(Titled(title, desc));
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return Row(grid, divider);
    }

    private static Border SliderRow(string title, Slider slider, TextBlock value, bool divider = true)
    {
        var label = new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Medium,
            Foreground = Solid(0xFA, 0xFA, 0xFA),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var right = new NegiStackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Width = 280,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(slider);
        right.Children.Add(value);

        var grid = new NegiGrid { Columns = "* Auto" };
        grid.Children.Add(label);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return Row(grid, divider);
    }

    private static Border Row(UIElement content, bool divider) =>
        new()
        {
            Height = 58,
            BorderBrush = Solid(0x26, 0x26, 0x2B),
            BorderThickness = divider ? new Thickness(0, 0, 0, 1) : new Thickness(0),
            Child = content,
        };

    private static NegiStackPanel Titled(string title, string desc)
    {
        var panel = new NegiStackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.Medium,
            Foreground = Solid(0xFA, 0xFA, 0xFA),
        });
        panel.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 12,
            Foreground = Solid(0x71, 0x71, 0x7A),
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }

    private static TextBlock ValueText(string text) => new()
    {
        Text = text,
        Width = 48,
        TextAlignment = TextAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = Solid(0xFA, 0xFA, 0xFA),
    };

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
#endif
