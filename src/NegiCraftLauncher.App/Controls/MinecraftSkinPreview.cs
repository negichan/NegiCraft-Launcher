using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;

namespace NegiCraftLauncher.App.Controls;

public class MinecraftSkinPreview : Panel
{
    public static readonly StyledProperty<string> PlayerNameProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, string>(nameof(PlayerName), "MiKu_Mew");

    public string PlayerName
    {
        get => GetValue(PlayerNameProperty);
        set => SetValue(PlayerNameProperty, value);
    }

    public static readonly StyledProperty<bool> SneakingProperty =
        AvaloniaProperty.Register<MinecraftSkinPreview, bool>(nameof(Sneaking));

    public bool Sneaking
    {
        get => GetValue(SneakingProperty);
        set => SetValue(SneakingProperty, value);
    }

    private readonly SkinRenderControl _skinRender;
    private readonly TextBlock _nameText;
    private readonly Border _nametag;
    private readonly Ellipse _shadow;
    private bool _isDragging;
    private Point _lastMousePos;

    public MinecraftSkinPreview()
    {
        Width = 160;
        Height = 250;
        ClipToBounds = false;
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);

        // 1. Soft shadow directly beneath character's feet (feet at Y ≈ 164px)
        _shadow = new Ellipse
        {
            Width = 56,
            Height = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 160, 0, 0),
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(90, 0, 0, 0), 0.0),
                    new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.7)
                }
            }
        };

        // 2. Open-source 3D Minecraft Skin Renderer.
        // The renderer fits the model to ~75% of the viewport height, so to match the
        // mockup's 64x128 model inside the 160x250 stage (~51%) we render into a smaller
        // 110x171 viewport centred on the mockup model position (centre 80,100).
        _skinRender = new SkinRenderControl
        {
            Width = 110,
            Height = 171,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0),
            IsHitTestVisible = false
        };

        // 3. Floating nametag directly above character's head (head top at Y ≈ 36.6px)
        // Jersey 10 draws on a 75/1400 em grid, so 12.5 DIP at RenderScaling 1.5 lands each
        // design pixel on exactly one device pixel. Alias keeps ClearType from smearing it.
        _nameText = new TextBlock
        {
            Text = PlayerName,
            Foreground = Brushes.White,
            FontFamily = new FontFamily("avares://NegiCraftLauncher.App/Assets/Fonts#Jersey 10"),
            FontSize = 12.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        TextOptions.SetTextRenderingMode(_nameText, TextRenderingMode.Alias);

        _nametag = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(107, 0, 0, 0)), // rgba(0,0,0,.42)
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(9, 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 0, 0),
            IsHitTestVisible = false,
            Child = _nameText
        };

        Children.Add(_shadow);
        Children.Add(_skinRender);
        Children.Add(_nametag);
    }

    private TopLevel? _subscribedTopLevel;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel != null)
        {
            _subscribedTopLevel = topLevel;
            // Use Tunnel strategy so mouse moves across any child controls in the window are intercepted
            topLevel.AddHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribedTopLevel != null)
        {
            _subscribedTopLevel.RemoveHandler(InputElement.PointerMovedEvent, OnWindowPointerMoved);
            _subscribedTopLevel = null;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _isDragging = true;
            _lastMousePos = e.GetPosition(this);
            Cursor = new Cursor(StandardCursorType.SizeWestEast);
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_isDragging)
        {
            var pos = e.GetPosition(this);
            double dx = pos.X - _lastMousePos.X;
            _lastMousePos = pos;

            // ONLY horizontal rotation (Yaw) around vertical Y-axis: rotY += dx * 0.6 deg
            _skinRender.RotateModel((float)(dx * 0.6));
            e.Handled = true;
        }
        else
        {
            UpdateLookAtFromPointer(e);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isDragging)
        {
            _isDragging = false;
            Cursor = new Cursor(StandardCursorType.Hand);
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _isDragging = false;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isDragging)
        {
            UpdateLookAtFromPointer(e);
        }
    }

    private void UpdateLookAtFromPointer(PointerEventArgs e)
    {
        var topLevel = _subscribedTopLevel ?? TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        // Stage width is 160, head centre is at X ≈ 80, Y ≈ 52 in MinecraftSkinPreview
        Point? headInWindow = this.TranslatePoint(new Point(80, 52), topLevel);
        if (!headInWindow.HasValue) return;

        Point mousePos = e.GetPosition(topLevel);

        // Avalonia keeps reporting the cursor position after it leaves the window (and once on
        // activation), which would otherwise leave the head staring at a point off-screen.
        var bounds = topLevel.Bounds;
        if (mousePos.X < 0 || mousePos.Y < 0 || mousePos.X > bounds.Width || mousePos.Y > bounds.Height)
        {
            _skinRender.SetHeadLookAt(0, 0);
            return;
        }

        double dx = mousePos.X - headInWindow.Value.X;
        double dy = mousePos.Y - headInWindow.Value.Y;

        // Target look angles smoothly proportioned to window dimensions (1180x720)
        // dx > 0 (mouse right) -> targetYaw > 0 (head turns right towards mouse)
        // dy > 0 (mouse below) -> targetPitch > 0 (head tilts down towards mouse)
        float targetYawDeg = (float)Math.Clamp(Math.Atan2(dx, 450.0) * (180.0 / Math.PI), -45.0, 45.0);
        float targetPitchDeg = (float)Math.Clamp(Math.Atan2(dy, 350.0) * (180.0 / Math.PI), -24.0, 24.0);

        _skinRender.SetHeadLookAt(targetPitchDeg, targetYawDeg);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PlayerNameProperty && change.NewValue is string newName)
        {
            if (_nameText != null)
            {
                _nameText.Text = newName;
            }
            _skinRender?.LoadFromUsername(newName);
        }
        else if (change.Property == SneakingProperty && change.NewValue is bool sneaking)
        {
            _skinRender.SetSneak(sneaking);
        }
    }
}
