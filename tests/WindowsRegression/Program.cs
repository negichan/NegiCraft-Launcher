using System.Reflection;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftSkinRender;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;
using NegiCraftLauncher.Skin.Controls;
using NegiCraftLauncher.Skin.Rendering;
using NegiCraftLauncher.Theme;

internal static class Program
{
    private static int _failed;
    private static int _checked;

    [STAThread]
    private static int Main()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/NegiCraftLauncher.Theme;component/Theme.xaml")
        });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
        var frame = new DispatcherFrame();
        var run = Run(() => frame.Continue = false);
        Dispatcher.PushFrame(frame);
        run.GetAwaiter().GetResult();
        app.Shutdown();
        Console.WriteLine($"RESULT: {_checked - _failed}/{_checked} passed");
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        _checked++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
        if (!ok) _failed++;
    }

    private static async Task Run(Action done)
    {
        Window? host = null;
        PetWindow? pet = null;
        try
        {
            if (Environment.GetCommandLineArgs().Contains("--fox-only"))
            {
                await CheckFoxSqueeze();
                return;
            }

            var renderer = new SkinRenderSoftware { Width = 110, Height = 171 };
            renderer.SetSkin(SkinTexture.Decode(DefaultSkins.Bytes(false))!);
            var pose = new SkinPoseDriver(renderer);
            pose.Reset();
            pose.Reset();
            pose.Update(0);
            Check("repeated reset preserves initial yaw", ModelYawMatches(renderer, pose.CurrentYawDeg));
            var startupMotion = new PetMotion();
            startupMotion.SetMode(PetInteractionMode.Control);
            startupMotion.BeginControlMode(double.NaN, double.NaN);
            var startupContext = new PetMotionContext(
                new PetStage(160, 320, 1, 75),
                new PetWorkArea(0, 0, 1920, 1080),
                new PetPoint(double.NaN, double.NaN),
                new PetPoint(50, 50),
                24);
            startupMotion.Tick(1.0 / 60, startupContext);
            Check("unplaced startup does not emit invalid head look", !startupMotion.HasHeadLook);
            startupMotion.Tick(1.0 / 60, startupContext with { Window = new PetPoint(500, 300) });
            Check("startup adopts first finite window position",
                startupMotion.GroundX == 500 && startupMotion.GroundY == 300);
            Check("startup head look stays finite",
                float.IsFinite(startupMotion.HeadYaw) && float.IsFinite(startupMotion.HeadPitch));

            foreach (var gpu in new[] { false, true })
            {
                pet = new PetWindow("", null);
                pet.ApplySettings(new PetSettings
                {
                    SkinPlayerName = "Steve", UseGpu = gpu,
                    SpineFlexible = true, InteractionMode = "Control"
                });
                pet.SetVirtualCursor(50, 50);
                pet.PlaceAtDefaultCorner();
                pet.Show();
                await Task.Delay(120);
                Check($"startup head matrix is finite (GPU={gpu})",
                    HeadMatrixIsFinite(pet.Preview));
                pet.Close();
                pet = null;
            }

            {
                host = new Window { Width = 420, Height = 320, ShowActivated = false, ShowInTaskbar = false };
                var panel = new Grid();
                var style = new Style(typeof(Border));
                style.Setters.Add(new Setter(UIElement.OpacityProperty, 0.0));
                style.Setters.Add(new Setter(UIElement.IsHitTestVisibleProperty, false));
                var pop = new Border { Width = 180, Height = 120, Background = Brushes.Green, Style = style };
                panel.Children.Add(pop);
                host.Content = panel;
                host.Show();
                await Task.Delay(60);
                Pop.SetIsOpen(pop, true);
                Check("popover interactive immediately", pop.IsHitTestVisible);
                var transform = pop.RenderTransform;
                await Task.Delay(70);
                Check("animation reuses transform", ReferenceEquals(transform, pop.RenderTransform));
                var before = transform.Value;
                var opacity = pop.Opacity;
                Pop.SetIsOpen(pop, false);
                Check("closing disables hit testing", !pop.IsHitTestVisible);
                Check("close preserves animated pose", Near(before, transform.Value));
                Check("close preserves animated opacity", Math.Abs(pop.Opacity - opacity) < 0.02);
                await Task.Delay(25);
                before = transform.Value;
                opacity = pop.Opacity;
                Pop.SetIsOpen(pop, true);
                Check("reopen during close interactive", pop.IsHitTestVisible);
                Check("reopen preserves animated pose", Near(before, transform.Value));
                Check("reopen preserves animated opacity", Math.Abs(pop.Opacity - opacity) < 0.02);
                await Task.Delay(450);
                Check("settled has no clip", pop.Clip is null);
                Check("settled releases animation clocks", !HasClocks(pop, transform));
                Check("settled releases temporary cache", pop.CacheMode is null);
                Pop.SetIsOpen(pop, false);
                await Task.Delay(220);
                Check("closed opacity zero", pop.Opacity == 0);
                Check("closed releases animation clocks", !HasClocks(pop, transform));
                Check("closed releases temporary cache", pop.CacheMode is null);
                Pop.SetIsOpen(pop, true);
                panel.Children.Remove(pop);
                await Task.Delay(100);
                Check("unload disables hit testing", !pop.IsHitTestVisible);
                Check("unload releases animation clocks", !HasClocks(pop, transform) && pop.CacheMode is null);
                panel.Children.Add(pop);
                await Task.Delay(450);
                Check("reload restores open popover", pop.IsHitTestVisible && pop.Opacity == 1);

                panel.Children.Remove(pop);
                var trigger = new Button
                {
                    Width = 80, Height = 30, Margin = new Thickness(230, 20, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
                };
                pop.HorizontalAlignment = HorizontalAlignment.Left;
                pop.VerticalAlignment = VerticalAlignment.Top;
                pop.Margin = new Thickness(15, 50, 0, 0);
                Pop.SetIsOpen(pop, false);
                Pop.SetTrigger(pop, trigger);
                Pop.SetPin(pop, "UnderRight");
                Pop.SetPinGap(pop, 6);
                panel.Children.Add(trigger);
                panel.Children.Add(pop);
                await Task.Delay(60);
                Pop.SetIsOpen(pop, true);
                await Task.Delay(280);
                var expected = trigger.TranslatePoint(new Point(trigger.ActualWidth - pop.ActualWidth, trigger.ActualHeight + 6), panel);
                var actual = pop.TranslatePoint(new Point(), panel);
                Check($"pinned popover aligns under trigger (expected={expected}, actual={actual})", (expected - actual).Length < 0.1);
                Pop.SetIsOpen(pop, false);
                await Task.Delay(30);
                Pop.SetIsOpen(pop, true);
                await Task.Delay(280);
                actual = pop.TranslatePoint(new Point(), panel);
                Check($"interrupted pinned popover retains alignment (expected={expected}, actual={actual})", (expected - actual).Length < 0.1);
                host.Close();
                host = null;
            }

            await CheckSettingsSlide();
            await CheckUiMotion();
            await CheckFoxSqueeze();

            pet = new PetWindow("", null) { Left = 500, Top = 300 };
            pet.ApplySkin(NegiCraftLauncher.Raster.DefaultSkins.Bytes(false));
            pet.Show();
            CheckPetMouseGestures(pet);
            {
                pet.IsControlMode = true;
                foreach (var gpu in new[] { false, true })
                {
                    pet.Preview.UseGpu = gpu;
                    var startX = pet.Left;
                    pet.SetSimulatedKey("d", true);
                    await Task.Delay(300);
                    pet.SetSimulatedKey("d", false);
                    await Task.Delay(100);
                    Check($"movement updates coordinates (GPU={gpu})", pet.Left > startX + 20);
                    var stoppedX = pet.Left;
                    await Task.Delay(100);
                    Check($"release stops movement (GPU={gpu})", Math.Abs(pet.Left - stoppedX) < 0.1);
                }
                var physics = PetPhysicsSelfTest.Run();
                Console.WriteLine(physics);
                Check("existing physics assertions", !physics.Contains("FAIL"));
            }
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine(ex);
        }
        finally
        {
            try
            {
                pet?.Close();
                host?.Close();
            }
            finally { done(); }
        }
    }

    private static async Task CheckSettingsSlide()
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(UIElement.OpacityProperty, 0.0));
        var dialog = new Border { Width = 280, Height = 180, Background = Brushes.Gray, Style = style };
        Pop.SetAnchor(dialog, "Center");
        Pop.SetSlideOnly(dialog, true);
        var host = new Window
        {
            Width = 420, Height = 320, Content = dialog,
            ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            host.Show();
            await Task.Delay(60);
            var size = dialog.RenderSize;
            Matrix Pose() => dialog.RenderTransform?.Value ?? Matrix.Identity;
            Pop.SetIsOpen(dialog, true);
            var transform = dialog.RenderTransform ?? Transform.Identity;
            if (SystemParameters.ClientAreaAnimation)
            {
                Check("settings slide starts just below its resting position",
                    Math.Abs(Pose().OffsetY - 14) < 0.01);
                var previousY = 14.0;
                var previousOpacity = 0.0;
                var monotonic = true;
                var fixedSize = true;
                var elapsed = Stopwatch.StartNew();
                var observedLongGlide = false;
                for (var frame = 0; frame < 20; frame++)
                {
                    await Task.Delay(16);
                    var matrix = Pose();
                    monotonic &= matrix.OffsetY >= -0.01 && matrix.OffsetY <= previousY + 0.01 &&
                                 dialog.Opacity >= previousOpacity - 0.01;
                    fixedSize &= Math.Abs(matrix.M11 - 1) < 0.001 && Math.Abs(matrix.M22 - 1) < 0.001 &&
                                 dialog.RenderSize == size;
                    previousY = matrix.OffsetY;
                    previousOpacity = dialog.Opacity;
                    if (elapsed.ElapsedMilliseconds is >= 100 and < 220)
                        observedLongGlide |= HasClocks(dialog, transform);
                }
                Check("settings entry remains animated for a slower glide", observedLongGlide);
                Check("settings slides upward and fades in without overshooting", monotonic);
                Check("settings opening keeps its size without scaling", fixedSize);
            }
            Check("settings settles at its original position and releases clocks",
                dialog.Opacity == 1 && Pose().IsIdentity &&
                !HasClocks(dialog, transform) && dialog.CacheMode is null);

            Pop.SetIsOpen(dialog, false);
            Check("settings closing immediately disables interaction", !dialog.IsHitTestVisible);
            if (SystemParameters.ClientAreaAnimation)
            {
                var previousY = 0.0;
                var previousOpacity = 1.0;
                var monotonic = true;
                var maxY = 0.0;
                var fixedSize = true;
                var elapsed = Stopwatch.StartNew();
                var observedLongGlide = false;
                for (var frame = 0; frame < 17; frame++)
                {
                    await Task.Delay(16);
                    if (dialog.Opacity <= 0.001) continue;
                    var matrix = Pose();
                    monotonic &= matrix.OffsetY >= previousY - 0.01 && matrix.OffsetY <= 12.01 &&
                                 dialog.Opacity <= previousOpacity + 0.01;
                    fixedSize &= Math.Abs(matrix.M11 - 1) < 0.001 && Math.Abs(matrix.M22 - 1) < 0.001 &&
                                 dialog.RenderSize == size;
                    maxY = Math.Max(maxY, matrix.OffsetY);
                    previousY = matrix.OffsetY;
                    previousOpacity = dialog.Opacity;
                    if (elapsed.ElapsedMilliseconds is >= 80 and < 170)
                        observedLongGlide |= HasClocks(dialog, transform);
                }
                Check("settings exit remains animated for a slower glide", observedLongGlide);
                Check("settings slides slightly downward while fading out", monotonic && maxY > 0.1);
                Check("settings closing keeps its size without scaling", fixedSize);
            }
            Check("settings closed state releases clocks and cache",
                dialog.Opacity == 0 && !HasClocks(dialog, transform) && dialog.CacheMode is null);

            Pop.SetIsOpen(dialog, true);
            await Task.Delay(35);
            Pop.SetIsOpen(dialog, false);
            await Task.Delay(25);
            var pose = Pose();
            var opacity = dialog.Opacity;
            Pop.SetIsOpen(dialog, true);
            Check("settings interrupted slide preserves its pose and opacity",
                Near(pose, Pose()) && Math.Abs(dialog.Opacity - opacity) < 0.02);
            await Task.Delay(320);
            Check("settings interrupted slide settles without residual animation",
                dialog.Opacity == 1 && Pose().IsIdentity &&
                !HasClocks(dialog, transform) && dialog.CacheMode is null);
        }
        finally { host.Close(); }
    }

    private static async Task CheckUiMotion()
    {
        var button = new Button { Width = 150, Height = 40, Content = "Launch" };
        var page = new Grid { Visibility = Visibility.Collapsed, Opacity = 0.85 };
        var nested = new Grid();
        page.Children.Add(nested);
        PageTransition.SetEnabled(page, true);
        PageTransition.SetEnabled(nested, true);
        var panel = new Grid();
        panel.Children.Add(page);
        panel.Children.Add(button);
        var host = new Window
        {
            Width = 400, Height = 260, Content = panel, ShowActivated = false, ShowInTaskbar = false
        };
        var pressedKey = (DependencyPropertyKey)typeof(System.Windows.Controls.Primitives.ButtonBase)
            .GetField("IsPressedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        try
        {
            host.Show();
            await Task.Delay(200);
            var root = (FrameworkElement)button.Template.FindName("Root", button);
            var transform = (TransformGroup)root.RenderTransform;
            var scale = (ScaleTransform)transform.Children[0];
            var size = button.RenderSize;
            Check("standard button enables motion on template root", ButtonMotion.GetEnabled(root));
            button.SetValue(pressedKey, true);
            await Task.Delay(100);
            Check("held button settles into press", Math.Abs(scale.ScaleX - 0.965) < 0.001);
            Check("button hit area and layout stay fixed", button.RenderSize == size && button.RenderTransform.Value.IsIdentity);
            var before = transform.Value;
            button.SetValue(pressedKey, false);
            if (SystemParameters.ClientAreaAnimation)
                Check($"button release preserves current pose (before={before}, after={transform.Value})", Near(before, transform.Value));
            await Task.Delay(200);
            ButtonMotion.SetHovered(root, true);
            await Task.Delay(160);
            Check("button hover settles smoothly", Math.Abs(scale.ScaleX - 1.012) < 0.001);
            Check("button releases completed clocks", !HasClocks(root, transform));
            button.SetValue(pressedKey, true);
            host.Hide();
            Check("hidden button resets motion", scale.ScaleX == 1 && !HasClocks(root, transform));
            button.SetValue(pressedKey, false);
            ButtonMotion.SetHovered(root, false);
            host.Show();
            await Task.Delay(100);

            var priorCache = new BitmapCache(1.25);
            page.CacheMode = priorCache;
            page.Visibility = Visibility.Visible;
            if (SystemParameters.ClientAreaAnimation)
            {
                Check("page caches during transition", page.CacheMode is BitmapCache && !ReferenceEquals(page.CacheMode, priorCache));
                Check("nested page avoids duplicate transition", nested.CacheMode is null && !nested.HasAnimatedProperties);
            }
            await Task.Delay(260);
            Check("page restores prior cache", ReferenceEquals(page.CacheMode, priorCache));
            Check("page preserves configured opacity", Math.Abs(page.Opacity - 0.85) < 0.001);
            Check("page settles without clocks", !HasClocks(page, page.RenderTransform));
            PageTransition.SetSuppressEnterAnimation(panel, true);
            panel.Visibility = Visibility.Hidden;
            panel.Visibility = Visibility.Visible;
            await Task.Delay(25);
            Check("modal return preserves page opacity and position", Math.Abs(page.Opacity - 0.85) < 0.001 &&
                page.RenderTransform.Value.IsIdentity && !HasClocks(page, page.RenderTransform));
            Check("modal return suppresses nested page entry", !HasClocks(nested, nested.RenderTransform));
            Check("modal return preserves the configured cache", ReferenceEquals(page.CacheMode, priorCache));
            PageTransition.SetSuppressEnterAnimation(panel, false);
            await Task.Delay(25);
            Check("ending modal suppression does not replay entry", !HasClocks(page, page.RenderTransform) &&
                !HasClocks(nested, nested.RenderTransform));
            page.Visibility = Visibility.Collapsed;
            page.Visibility = Visibility.Visible;
            if (SystemParameters.ClientAreaAnimation)
                Check("ordinary page navigation still animates", page.HasAnimatedProperties);
            await Task.Delay(260);
            for (var i = 0; i < 4; i++)
            {
                page.Visibility = Visibility.Collapsed;
                Check("hidden page releases transition", !HasClocks(page, page.RenderTransform) && ReferenceEquals(page.CacheMode, priorCache));
                page.Visibility = Visibility.Visible;
                await Task.Delay(25);
            }
            panel.Children.Remove(page);
            await Task.Delay(50);
            Check("unloaded page restores cache and opacity", ReferenceEquals(page.CacheMode, priorCache) &&
                Math.Abs(page.Opacity - 0.85) < 0.001 && !HasClocks(page, page.RenderTransform));
            panel.Children.Add(page);
            await Task.Delay(260);
            Check("reloaded page stays visible", Math.Abs(page.Opacity - 0.85) < 0.001 && !HasClocks(page, page.RenderTransform));
        }
        finally { host.Close(); }
    }

    private static async Task CheckFoxSqueeze()
    {
        var art = new Border { Width = 36, Height = 36, Background = Brushes.Coral };
        var fox = new SqueezeButton { Width = 42, Height = 36, Content = art };
        var panel = new Grid();
        panel.Children.Add(fox);
        var host = new Window
        {
            Width = 200, Height = 160, Content = panel,
            ShowActivated = false, ShowInTaskbar = false
        };
        var pressedKey = (DependencyPropertyKey)typeof(System.Windows.Controls.Primitives.ButtonBase)
            .GetField("IsPressedPropertyKey", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        void Press(bool down) => fox.SetValue(pressedKey, down);

        try
        {
            host.Show();
            await Task.Delay(60);
            var transform = (TransformGroup)art.RenderTransform;
            var scale = (ScaleTransform)transform.Children[0];
            var tilt = (RotateTransform)transform.Children[1];
            var size = fox.RenderSize;

            Press(true);
            await Task.Delay(140);
            Check("fox holds a deep squash while pressed", scale.ScaleX > 1.4 && scale.ScaleY < 0.6);
            Check("fox hit area stays fixed", fox.RenderSize == size && fox.RenderTransform.Value.IsIdentity);
            Press(false);
            var rebound = await ObserveScale(220);
            if (SystemParameters.ClientAreaAnimation)
                Check($"fox has a pronounced rebound (peak={rebound.Max:F3})", rebound.Max > 1.3);

            var before = scale.ScaleY;
            Press(true);
            if (SystemParameters.ClientAreaAnimation)
                Check("fox repeated press preserves current pose", Math.Abs(scale.ScaleY - before) < 0.02);
            await Task.Delay(110);
            Check("fox repeated press interrupts rebound", scale.ScaleY < 0.6);
            Press(false);
            await Task.Delay(650);
            Check("fox returns to rest", scale.ScaleX == 1 && scale.ScaleY == 1 && tilt.Angle == 0);
            Check("fox releases completed animation clocks", !scale.HasAnimatedProperties && !tilt.HasAnimatedProperties);

            Press(true);
            Press(false);
            var tap = await ObserveScale(190);
            if (SystemParameters.ClientAreaAnimation)
                Check($"fox short tap still deeply squeezes (min={tap.Min:F3})", tap.Min < 0.65);
            host.Hide();
            Check("hidden fox resets motion", scale.ScaleX == 1 && scale.ScaleY == 1 && !scale.HasAnimatedProperties);
            host.Show();
            Press(true);
            await Task.Delay(110);
            panel.Children.Remove(fox);
            await Task.Delay(50);
            Check("unloaded fox clears animations", scale.ScaleX == 1 && scale.ScaleY == 1 && !tilt.HasAnimatedProperties);
            Press(false);
            panel.Children.Add(fox);
            await Task.Delay(50);
            Check("reloaded fox starts at rest", scale.ScaleX == 1 && scale.ScaleY == 1);

            async Task<(double Min, double Max)> ObserveScale(int milliseconds)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var min = scale.ScaleY;
                var max = min;
                while (watch.ElapsedMilliseconds < milliseconds)
                {
                    await Task.Delay(10);
                    min = Math.Min(min, scale.ScaleY);
                    max = Math.Max(max, scale.ScaleY);
                }
                return (min, max);
            }
        }
        finally
        {
            host.Close();
        }
    }

    private static bool ModelYawMatches(SkinRenderSoftware renderer, float yaw)
    {
        var matrix = renderer.DebugMatrices().Model;
        var radians = yaw * MathF.PI / 180;
        return Math.Abs(matrix.M11 - MathF.Cos(radians)) < 1e-5
            && Math.Abs(matrix.M13 + MathF.Sin(radians)) < 1e-5;
    }

    private static bool HeadMatrixIsFinite(SkinPreviewControl preview)
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var software = (SkinRenderSoftware)typeof(SkinPreviewControl)
            .GetField("_software", flags)!.GetValue(preview)!;
        var gpu = (SkinRenderGpu?)typeof(SkinPreviewControl)
            .GetField("_gpu", flags)!.GetValue(preview);
        return MatrixIsFinite(software.DebugMatrices().Head)
            && (gpu is null || MatrixIsFinite(gpu.MatrixOf(ModelPartType.Head)));
    }

    private static bool MatrixIsFinite(System.Numerics.Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14) &&
        float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24) &&
        float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);

    private static void CheckPetMouseGestures(PetWindow pet)
    {
        var root = (FrameworkElement)pet.FindName("RootPanel");
        var menu = (MenuItem)pet.FindName("MenuSneak");
        var motion = (PetMotion)typeof(PetWindow)
            .GetField("_motion", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(pet)!;
        var origin = new Point(pet.Left, pet.Top);

        void SetManualSneak(bool on)
        {
            if (motion.ManualSneakToggle != on)
                menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        }

        void Press(int clicks)
        {
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent
            };
            // WPF normally supplies this internal setter through its input pipeline.
            typeof(MouseButtonEventArgs).GetProperty(nameof(MouseButtonEventArgs.ClickCount))!
                .GetSetMethod(nonPublic: true)!.Invoke(args, new object[] { clicks });
            root.RaiseEvent(args);
            Check($"press is captured (clicks={clicks})", Mouse.Captured == root && pet.Preview.IsDangling);
        }

        void Move()
        {
            // Offset the recorded press point instead of moving the user's actual cursor.
            var field = typeof(PetWindow).GetField("_leftPressCursor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var point = field.GetValue(pet)!;
            var x = point.GetType().GetField("X")!;
            x.SetValue(point, (int)x.GetValue(point)! - 20);
            field.SetValue(pet, point);
            root.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
            {
                RoutedEvent = UIElement.MouseMoveEvent
            });
            Check("move crosses drag threshold",
                (bool)typeof(PetWindow).GetField("_isLeftDragging", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(pet)!);
        }

        void Release(bool loseCapture)
        {
            if (loseCapture) Mouse.Capture(null);
            else root.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonUpEvent
            });
            Check("release clears grab and capture", !pet.Preview.IsDangling && Mouse.Captured != root);
            pet.Left = origin.X;
            pet.Top = origin.Y;
        }

        bool IsStanding() => !motion.ManualSneakToggle && !motion.Sneaking && !pet.Preview.Sneaking;

        try
        {
            foreach (var (clicks, drag, loseCapture) in new[]
                     {
                         (1, false, false), (2, false, false), (1, true, false),
                         (2, true, false), (2, true, true)
                     })
            {
                SetManualSneak(false);
                // A second press can still be reported as a double-click before dragging begins.
                if (clicks == 2) { Press(1); Release(false); }
                Press(clicks);
                Check($"mouse press does not crouch (clicks={clicks}, drag={drag})", IsStanding());
                if (drag) Move();
                Release(loseCapture);
                Check($"mouse release does not crouch (clicks={clicks}, drag={drag}, lost={loseCapture})", IsStanding());
            }

            SetManualSneak(true);
            Check("menu explicitly enables crouch", motion.ManualSneakToggle && pet.Preview.Sneaking);
            Press(2);
            Move();
            Release(false);
            Check("double-click drag preserves explicit menu crouch", motion.ManualSneakToggle && pet.Preview.Sneaking);
            SetManualSneak(false);
            Check("menu explicitly disables crouch", IsStanding());

            pet.SetSimulatedKey("shift", true);
            Check("Shift still enables crouch", motion.Sneaking && pet.Preview.Sneaking && !motion.ManualSneakToggle);
            Press(2);
            Move();
            Release(false);
            Check("drag preserves held Shift without toggling manual crouch",
                motion.Sneaking && pet.Preview.Sneaking && !motion.ManualSneakToggle);
            pet.SetSimulatedKey("shift", false);
            Check("releasing Shift restores standing after drag", IsStanding());
        }
        finally
        {
            Mouse.Capture(null);
            pet.SetSimulatedKey("shift", false);
            SetManualSneak(false);
            pet.Left = origin.X;
            pet.Top = origin.Y;
        }
    }

    private static bool HasClocks(FrameworkElement target, Transform transform) =>
        target.HasAnimatedProperties || transform.HasAnimatedProperties ||
        transform is TransformGroup group && group.Children.Any(item => item.HasAnimatedProperties);

    private static bool Near(Matrix a, Matrix b) =>
        Math.Abs(a.M11 - b.M11) < 0.02 && Math.Abs(a.M22 - b.M22) < 0.02 &&
        Math.Abs(a.OffsetX - b.OffsetX) < 0.1 && Math.Abs(a.OffsetY - b.OffsetY) < 0.1;
}
