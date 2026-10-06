#if DEBUG
using System.Collections.Generic;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Avalonia.Views;

/// <summary>
/// The <c>Debug</c> section of the settings page (left-hand nav entry + right-hand card).
/// **Only exists in a Debug build.**
///
/// <para><b>Why this is code-built instead of living in MainWindow.axaml</b>: XAML has no
/// <c>#if</c>, so anything written there is compiled into BAML and shipped. This whole file is
/// wrapped in <c>#if DEBUG</c>; in a Release build it compiles to nothing and the compiler erases
/// <see cref="MainWindow.OnDebugSectionAttach"/>'s declaration along with every call site — so
/// "not compiled into a release" is guaranteed by the compiler rather than by hiding UI at
/// runtime.</para>
///
/// <para>The mount points are two empty panels in the XAML
/// (<c>SettingsNavPanel</c> / <c>SettingsCardsPanel</c>); they carry no Debug-specific markup, so in
/// Release they are just ordinary containers.</para>
///
/// <para><b>State is not persisted</b>: these switches live in memory only and reset on restart.
/// Persisting them would mean adding fields to <c>LauncherSettings</c> (Core, compiled in Release
/// too), which defeats the point of keeping Debug out of the shipped build.</para>
///
/// <para>⚠️ Keep this in step with <c>App/MainWindow.Debug.cs</c> — the two are line-for-line
/// counterparts.</para>
/// </summary>
public partial class MainWindow
{
    /// <summary>Label of this entry in the settings nav. An ordinary string, just not in the XAML.</summary>
    private const string DebugTab = "Debug";

    /// <summary>Factory alpha of both shell-shadow layers. Matches <c>#1A000000</c> in
    /// MainWindow.axaml — Tailwind's shadow-lg is black at 10% (0.1 * 255 = 25.5 → 26).</summary>
    private const byte BaseShadowAlpha = 0x1A;

    /// <summary>Geometry of the shadow's two layers, mirroring the BoxShadow written in
    /// MainWindow.axaml (Tailwind shadow-lg: "0 10px 15px -3px, 0 4px 6px -4px").</summary>
    private static readonly (double OffsetY, double Blur, double Spread)[] ShadowLayers =
    [
        (10, 15, -3),
        (4, 6, -4),
    ];

    /// <summary>Factory blur radii (px), which are also the sliders' default values: the sidebar's
    /// frosted backdrop 18 (the mockup's <c>blur(18px)</c>) and the floating-card shadow 30
    /// (<c>BoxShadow "0 10 30 -12"</c>) shared by the dock and the launch-progress card.</summary>
    private const double BaseSidebarBlur = 18;
    private const double BaseCardBlur = 30;

    // The floating cards' shadow geometry, shared by both of them (the WPF side has
    // FloatShadowStyle for exactly this).
    private const double CardShadowOffsetY = 10;
    private const double CardShadowSpread = -12;
    private static readonly Color CardShadowColor = Color.FromArgb(0xB0, 0, 0, 0);

    private MainWindowViewModel? _dbgVm;
    private Button? _dbgNavButton;
    private Border? _dbgCard;
    private ToggleSwitch? _dbgShadowToggle;
    private Slider? _dbgShadowOpacity;
    private TextBlock? _dbgShadowOpacityText;
    private Slider? _dbgSidebarBlur;
    private TextBlock? _dbgSidebarBlurText;
    private Slider? _dbgCardBlur;
    private TextBlock? _dbgCardBlurText;

    /// <summary>The backdrop layers' own BlurEffects (pixel scene / picked photo — mutually
    /// exclusive by visibility). Collected by walking the tree so adding a backdrop layer does
    /// not require touching this file. Avalonia has no Freezables, so these are writable as-is.</summary>
    private readonly List<BlurEffect> _sidebarBlurs = [];

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

    /// <summary>Idempotent: the control tree is built once; a DataContext swap only rewires events.</summary>
    private void EnsureDebugSection()
    {
        if (_dbgNavButton is not null) return;

        _dbgNavButton = new Button { Content = DebugTab };
        _dbgNavButton.Classes.Add("SetNavBtn");
        _dbgNavButton.Click += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm) vm.SelectSettingsTabCommand.Execute(DebugTab);
        };
        SettingsNavPanel.Children.Add(_dbgNavButton);

        // The sidebar's frosted backdrop carries its own BlurEffect per layer (pixel scene /
        // picked photo — only one is visible at a time). Collected from the tree so a new layer
        // does not need a change here; Avalonia has no Freezables, so these are writable as-is.
        foreach (var node in SidebarBackdrop.GetLogicalDescendants())
        {
            if (node is Visual { Effect: BlurEffect blur }) _sidebarBlurs.Add(blur);
        }

        _dbgCard = BuildDebugCard();
        SettingsCardsPanel.Children.Add(_dbgCard);
    }

    private Border BuildDebugCard()
    {
        var stack = new StackPanel();

        stack.Children.Add(HintRow(
            "Debug 调试项",
            "仅在 Debug 模式可见；Release 打包时这段 UI 不会被编译进去。"));

        // ---- window shadow ----
        _dbgShadowToggle = new ToggleSwitch
        {
            IsChecked = true,
            OnContent = "",
            OffContent = "",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _dbgShadowToggle.IsCheckedChanged += (_, _) => ApplyShadowState();
        stack.Children.Add(OptionRow("窗口阴影", "窗口外壳那圈投影（Tailwind shadow-lg，两层）。", _dbgShadowToggle));

        _dbgShadowOpacity = new Slider { Minimum = 0, Maximum = 100, Value = 100, Width = 210 };
        _dbgShadowOpacityText = ValueText("100%");
        _dbgShadowOpacity.PropertyChanged += OnDebugSliderChanged;
        stack.Children.Add(SliderRow("阴影浓度", _dbgShadowOpacity, _dbgShadowOpacityText));

        // ---- blur radii (defaults are the values written in the XAML) ----
        _dbgSidebarBlur = BlurSlider(BaseSidebarBlur);
        _dbgSidebarBlurText = ValueText($"{BaseSidebarBlur:0} px");
        _dbgSidebarBlur.PropertyChanged += OnDebugSliderChanged;
        stack.Children.Add(SliderRow("侧栏磨砂模糊", _dbgSidebarBlur, _dbgSidebarBlurText));

        _dbgCardBlur = BlurSlider(BaseCardBlur);
        _dbgCardBlurText = ValueText($"{BaseCardBlur:0} px");
        _dbgCardBlur.PropertyChanged += OnDebugSliderChanged;
        stack.Children.Add(SliderRow("浮卡投影模糊", _dbgCardBlur, _dbgCardBlurText, divider: false));

        var card = new Border
        {
            Background = Solid(0x11, 0x11, 0x14),
            BorderBrush = Solid(0x26, 0x26, 0x2B),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18, 0, 18, 0),
            IsVisible = false,
            Child = stack,
        };

        ApplyShadowState();
        ApplySidebarBlurState();
        ApplyCardBlurState();
        return card;
    }

    /// <summary>Slider.Value is a styled property, so there is no dedicated event to hook — the
    /// notification arrives through PropertyChanged, and all three sliders share this handler.</summary>
    private void OnDebugSliderChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Slider.ValueProperty) return;

        if (ReferenceEquals(sender, _dbgSidebarBlur)) ApplySidebarBlurState();
        else if (ReferenceEquals(sender, _dbgCardBlur)) ApplyCardBlurState();
        else ApplyShadowState();
    }

    // ---- apply ----

    private void ApplyShadowState()
    {
        if (_dbgShadowToggle is null || _dbgShadowOpacity is null) return;

        var on = _dbgShadowToggle.IsChecked == true;
        ShellShadowCaster.IsVisible = on;

        if (_dbgShadowOpacityText is not null)
        {
            _dbgShadowOpacityText.Text = $"{_dbgShadowOpacity.Value:0}%";
        }

        var alpha = (byte)System.Math.Round(BaseShadowAlpha * (_dbgShadowOpacity.Value / 100d));
        var color = Color.FromArgb(alpha, 0, 0, 0);
        // BoxShadows 的第二参是数组而不是 params，第二层得自己包一下。
        ShellShadowCaster.BoxShadow = new BoxShadows(
            Shadow(ShadowLayers[0], color),
            new[] { Shadow(ShadowLayers[1], color) });
    }

    private static BoxShadow Shadow((double OffsetY, double Blur, double Spread) layer, Color color) =>
        new() { OffsetY = layer.OffsetY, Blur = layer.Blur, Spread = layer.Spread, Color = color };

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
        // The dock and the launch-progress card move together — same as the WPF side, where both
        // borders are styled by the single FloatShadowStyle.
        DockCard.BoxShadow = CardShadow(_dbgCardBlur.Value);
        LaunchProgressCard.BoxShadow = CardShadow(_dbgCardBlur.Value);
    }

    private static BoxShadows CardShadow(double blur) =>
        new(new BoxShadow
        {
            OffsetY = CardShadowOffsetY,
            Blur = blur,
            Spread = CardShadowSpread,
            Color = CardShadowColor,
        });

    private void SyncDebugTab()
    {
        var isDebug = (DataContext as MainWindowViewModel)?.CurrentSettingsTab == DebugTab;

        _dbgNavButton?.Classes.Set("On", isDebug);
        if (_dbgCard is not null) _dbgCard.IsVisible = isDebug;
    }

    private void OnDebugVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.CurrentSettingsTab)) SyncDebugTab();
    }

    // ---- row builders (mirror the card layout in the XAML so we don't invent a second look) ----

    /// <summary>Leading explanation row: wraps, so its height is automatic rather than the usual 58.</summary>
    private static Border HintRow(string title, string desc) =>
        new()
        {
            Padding = new Thickness(0, 14, 0, 14),
            BorderBrush = Solid(0x26, 0x26, 0x2B),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Titled(title, desc),
        };

    private static Border OptionRow(string title, string desc, Control control, bool divider = true)
    {
        var grid = TwoColumnGrid();
        grid.Children.Add(Titled(title, desc));
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return Row(grid, divider);
    }

    /// <summary>Blur-radius slider: 0..60px, one tick per pixel. The caller passes the factory
    /// value, which is the radius written in the XAML.</summary>
    private static Slider BlurSlider(double value) => new()
    {
        Minimum = 0,
        Maximum = 60,
        Value = value,
        Width = 210,
        IsSnapToTickEnabled = true,
        TickFrequency = 1,
    };

    private static Border SliderRow(string title, Slider slider, TextBlock value, bool divider = true)
    {
        var label = new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Medium,
            Foreground = Solid(0xFA, 0xFA, 0xFA),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Width = 280,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(slider);
        right.Children.Add(value);

        var grid = TwoColumnGrid();
        grid.Children.Add(label);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return Row(grid, divider);
    }

    private static Grid TwoColumnGrid()
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        return grid;
    }

    private static Border Row(Control content, bool divider) =>
        new()
        {
            Height = 58,
            BorderBrush = Solid(0x26, 0x26, 0x2B),
            BorderThickness = divider ? new Thickness(0, 0, 0, 1) : new Thickness(0),
            Child = content,
        };

    private static StackPanel Titled(string title, string desc)
    {
        var panel = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeight.Medium,
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

    private static SolidColorBrush Solid(byte r, byte g, byte b) =>
        new(Color.FromRgb(r, g, b));
}
#endif
