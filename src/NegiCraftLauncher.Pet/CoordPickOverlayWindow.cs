using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace NegiCraftLauncher.Pet;

/// <summary>
/// A lightweight transparent overlay that captures clicks anywhere on screen
/// to set the pet's navigation target points.
/// Supports holding Ctrl to continuously click multiple waypoints without exiting.
/// </summary>
public class CoordPickOverlayWindow : Window
{
    private readonly Action<PixelPoint, bool /* isContinuous */> _onWaypoint;
    private readonly Canvas _canvas;
    private readonly TextBlock _hintTextBlock;
    private readonly List<Point> _visualPoints = new();
    private int _waypointCounter;
    private bool _hasHandled;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
    private const int VK_CONTROL = 0x11;

    public CoordPickOverlayWindow(Action<PixelPoint> onSinglePicked)
        : this((pt, _) => onSinglePicked(pt))
    {
    }

    public CoordPickOverlayWindow(Action<PixelPoint, bool> onWaypoint)
    {
        _onWaypoint = onWaypoint;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // Subtly non-null to intercept all pointer events
        ShowInTaskbar = false;
        Topmost = true;
        CanResize = false;
        Cursor = new Cursor(StandardCursorType.Cross);

        // 盖住**整个虚拟屏**，不只是主屏 —— 副屏上点不到坐标正是"只盖主屏"造成的。
        // WPF 侧（Pet.Wpf/CoordPickOverlayWindow.cs）用的是 SystemParameters.VirtualScreen*，
        // 两端行为要对齐。
        var screens = Screens;
        var all = screens.All;
        if (all.Count > 0)
        {
            // Screen.Bounds 是**物理像素**（PixelRect），而 Window.Width/Height 是 DIP。
            // 除以主屏缩放：遮罩窗口自己就落在主屏（虚拟屏原点是主屏左上角），
            // Avalonia 的 Width 也是按窗口所在显示器的缩放换算的。
            var anchor = screens.Primary ?? all[0];
            var scale = anchor.Scaling > 0 ? anchor.Scaling : 1.0;

            int left = all[0].Bounds.X;
            int top = all[0].Bounds.Y;
            int right = left + all[0].Bounds.Width;
            int bottom = top + all[0].Bounds.Height;
            foreach (var s in all)
            {
                left = Math.Min(left, s.Bounds.X);
                top = Math.Min(top, s.Bounds.Y);
                right = Math.Max(right, s.Bounds.X + s.Bounds.Width);
                bottom = Math.Max(bottom, s.Bounds.Y + s.Bounds.Height);
            }

            Position = new PixelPoint(left, top);
            Width = (right - left) / scale;
            Height = (bottom - top) / scale;
        }
        else
        {
            Width = 1920;
            Height = 1080;
        }

        var root = new Panel { Background = Brushes.Transparent };
        _canvas = new Canvas { Background = Brushes.Transparent, IsHitTestVisible = false };
        root.Children.Add(_canvas);

        _hintTextBlock = new TextBlock
        {
            Text = "点击设定目标 | 按住 Ctrl 连续设定多个途经点 (ESC 退出)",
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(Color.Parse("#fafafa")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var hintCard = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 48, 0, 0),
            Background = new SolidColorBrush(Color.Parse("#E6111114")),
            BorderBrush = new SolidColorBrush(Color.Parse("#33FFFFFF")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(22, 10),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 24, Spread = -2, Color = Color.FromArgb(180, 0, 0, 0) }),
            IsHitTestVisible = false,
            Child = _hintTextBlock
        };
        root.Children.Add(hintCard);
        Content = root;

        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_hasHandled) return;

        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsRightButtonPressed)
        {
            // 右键单击：直接结束选点并关闭
            _hasHandled = true;
            Close();
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        bool isCtrlHeld = e.KeyModifiers.HasFlag(KeyModifiers.Control) || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        var posInWindow = e.GetPosition(this);
        var screenPoint = this.PointToScreen(posInWindow);

        _waypointCounter++;
        AddVisualWaypoint(posInWindow, _waypointCounter);
        _visualPoints.Add(posInWindow);

        _onWaypoint?.Invoke(screenPoint, isCtrlHeld);

        if (!isCtrlHeld)
        {
            // 单次点击（或松开 Ctrl 的最后一点）：关闭选点遮罩
            _hasHandled = true;
            Close();
        }
        else
        {
            // 连续模式（按住 Ctrl）：保持遮罩，更新顶部提示
            _hintTextBlock.Text = $"已添加 {_waypointCounter} 个目标航点 | 继续按住 Ctrl 点击添加，松开 Ctrl 点击或按 ESC 结束";
        }
    }

    private void AddVisualWaypoint(Point pt, int number)
    {
        // 如果有上一个航点，绘制连线
        if (_visualPoints.Count > 0)
        {
            var prev = _visualPoints[^1];
            var line = new Line
            {
                StartPoint = prev,
                EndPoint = pt,
                Stroke = new SolidColorBrush(Color.Parse("#9938BDF8")),
                StrokeThickness = 2,
                StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 5, 3 },
                IsHitTestVisible = false
            };
            _canvas.Children.Add(line);
        }

        // 航点圆形徽章
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.Parse("#E618181B")),
            BorderBrush = new SolidColorBrush(Color.Parse("#38BDF8")),
            BorderThickness = new Thickness(2),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 8, Spread = 0, Color = Color.FromArgb(140, 56, 189, 248) }),
            IsHitTestVisible = false,
            Child = new TextBlock
            {
                Text = number.ToString(),
                FontSize = 11,
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        Canvas.SetLeft(badge, pt.X - 12);
        Canvas.SetTop(badge, pt.Y - 12);
        _canvas.Children.Add(badge);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.Enter or Key.Space)
        {
            if (!_hasHandled)
            {
                _hasHandled = true;
                Close();
            }
        }
    }
}
