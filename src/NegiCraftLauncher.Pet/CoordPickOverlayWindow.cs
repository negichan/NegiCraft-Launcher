using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace NegiCraftLauncher.Pet;

/// <summary>
/// 全屏透明选点遮罩：点哪就把哪当成桌宠的导航目标。
/// 按住 Ctrl 可以连续点多个途经点而不退出。
///
/// <para>这是 <c>Pet/CoordPickOverlayWindow.cs</c>（192 行）的 WPF 版，逐段对应。
/// 换掉的平台 API：</para>
///
/// <list type="bullet">
/// <item><c>WindowDecorations=None</c> + <c>TransparencyLevelHint=Transparent</c>
/// → <c>WindowStyle=None</c> + <c>AllowsTransparency=True</c>。</item>
/// <item><c>Screens.Primary.Bounds</c>（物理像素，要除 scaling）→
/// <see cref="SystemParameters.VirtualScreenLeft"/> 等（WPF 侧本来就是 DIP），
/// 顺带从"只盖主屏"升级成"盖住整个虚拟屏"，多显示器也能选点。</item>
/// <item><c>PointToScreen</c> 两边都有，但 <b>WPF 返回的是物理像素</b>（<c>Point</c> 的
/// X/Y 是设备像素），跟 Avalonia 的 <c>PixelPoint</c> 语义一致 —— 所以回调里给的
/// 屏幕坐标也是物理像素，由 <c>PetWindow</c> 自己除 DPI 转 DIP。</item>
/// <item><c>BoxShadows</c> → <see cref="DropShadowEffect"/>；<c>StrokeDashArray</c> 用
/// <see cref="DoubleCollection"/>（Avalonia 那边是 <c>AvaloniaList&lt;double&gt;</c>）。</item>
/// <item><c>PointerPressed</c> → <c>MouseLeftButtonDown</c> / <c>MouseRightButtonDown</c>。</item>
/// </list>
/// </summary>
public class CoordPickOverlayWindow : Window
{
    /// <summary>回调参数是**物理像素**屏幕坐标，第二项表示"按住 Ctrl 的连续选点模式"。</summary>
    private readonly Action<Point, bool> _onWaypoint;
    private readonly Canvas _canvas;
    private readonly TextBlock _hintTextBlock;
    private readonly List<Point> _visualPoints = new();
    private int _waypointCounter;
    private bool _hasHandled;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_CONTROL = 0x11;

    private static readonly SolidColorBrush HintForeground = Freeze(new SolidColorBrush(Color.FromRgb(0xfa, 0xfa, 0xfa)));
    private static readonly SolidColorBrush HintBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x11, 0x11, 0x14)));
    private static readonly SolidColorBrush HintBorder = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    private static readonly SolidColorBrush LineStroke = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0x38, 0xBD, 0xF8)));
    private static readonly SolidColorBrush BadgeBackground = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x18, 0x18, 0x1B)));
    private static readonly SolidColorBrush BadgeBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)));

    private static T Freeze<T>(T brush) where T : Freezable
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    public CoordPickOverlayWindow(Action<Point> onSinglePicked)
        : this((pt, _) => onSinglePicked(pt))
    {
    }

    public CoordPickOverlayWindow(Action<Point, bool> onWaypoint)
    {
        _onWaypoint = onWaypoint;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // 几乎全透明，但能吃到所有点击
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Cursor = Cursors.Cross;

        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        if (Width <= 0 || Height <= 0)
        {
            Width = 1920;
            Height = 1080;
        }

        var root = new Grid { Background = Brushes.Transparent };

        _canvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false };
        root.Children.Add(_canvas);

        _hintTextBlock = new TextBlock
        {
            Text = "点击设定目标 | 按住 Ctrl 连续设定多个途经点 (ESC 退出)",
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = HintForeground,
            TextAlignment = TextAlignment.Center,
        };

        var hintCard = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 48, 0, 0),
            Background = HintBackground,
            BorderBrush = HintBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(22, 10, 22, 10),
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 180.0 / 255.0, Color = Colors.Black },
            IsHitTestVisible = false,
            Child = _hintTextBlock,
        };
        root.Children.Add(hintCard);
        Content = root;

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseRightButtonDown += OnMouseRightButtonDown;
        KeyDown += OnKeyDown;
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右键单击：直接结束选点并关闭
        if (_hasHandled) return;
        _hasHandled = true;
        e.Handled = true;
        Close();
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_hasHandled) return;

        var isCtrlHeld = (Keyboard.Modifiers & ModifierKeys.Control) != 0
                         || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        var posInWindow = e.GetPosition(this);
        // WPF 的 PointToScreen 返回**物理像素**（与 Avalonia 的 PixelPoint 同义）。
        var screenPoint = PointToScreen(posInWindow);

        _waypointCounter++;
        AddVisualWaypoint(posInWindow, _waypointCounter);
        _visualPoints.Add(posInWindow);

        _onWaypoint?.Invoke(screenPoint, isCtrlHeld);

        if (!isCtrlHeld)
        {
            // 单次点击（或松开 Ctrl 的最后一点）：关闭选点遮罩
            _hasHandled = true;
            e.Handled = true;
            Close();
        }
        else
        {
            // 连续模式（按住 Ctrl）：保持遮罩，更新顶部提示
            _hintTextBlock.Text = $"已添加 {_waypointCounter} 个目标航点 | 继续按住 Ctrl 点击添加，松开 Ctrl 点击或按 ESC 结束";
        }

        e.Handled = true;
    }

    private void AddVisualWaypoint(Point pt, int number)
    {
        // 如果有上一个航点，绘制连线
        if (_visualPoints.Count > 0)
        {
            var prev = _visualPoints[^1];
            var line = new Line
            {
                X1 = prev.X,
                Y1 = prev.Y,
                X2 = pt.X,
                Y2 = pt.Y,
                Stroke = LineStroke,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                IsHitTestVisible = false,
            };
            _canvas.Children.Add(line);
        }

        // 航点圆形徽章
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = BadgeBackground,
            BorderBrush = BadgeBorder,
            BorderThickness = new Thickness(2),
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity = 140.0 / 255.0,
                Color = Color.FromRgb(0x38, 0xBD, 0xF8),
            },
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brushes.White,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        Canvas.SetLeft(badge, pt.X - 12);
        Canvas.SetTop(badge, pt.Y - 12);
        _canvas.Children.Add(badge);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space)
        {
            if (!_hasHandled)
            {
                _hasHandled = true;
                e.Handled = true;
                Close();
            }
        }
    }
}
