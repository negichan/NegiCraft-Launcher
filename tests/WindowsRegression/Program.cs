using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.Pet;

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
                Pop.SetIsOpen(pop, false);
                Check("closing disables hit testing", !pop.IsHitTestVisible);
                await Task.Delay(25);
                Pop.SetIsOpen(pop, true);
                Check("reopen during close interactive", pop.IsHitTestVisible);
                await Task.Delay(450);
                Check("settled has no clip", pop.Clip is null);
                Check("settled detaches Rendering", !IsRendering(pop));
                Pop.SetIsOpen(pop, false);
                await Task.Delay(220);
                Check("closed opacity zero", pop.Opacity == 0);
                Check("closed detaches Rendering", !IsRendering(pop));
                Pop.SetIsOpen(pop, true);
                panel.Children.Remove(pop);
                await Task.Delay(100);
                Check("unload disables hit testing", !pop.IsHitTestVisible);
                Check("unload detaches Rendering", !IsRendering(pop));
                panel.Children.Add(pop);
                await Task.Delay(450);
                Check("reload restores open popover", pop.IsHitTestVisible && pop.Opacity == 1);
                host.Close();
                host = null;
            }

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

    private static bool IsRendering(FrameworkElement pop)
    {
        var runners = typeof(Pop).GetField("Runners", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var lookup = new object?[] { pop, null };
        runners.GetType().GetMethod("TryGetValue")!.Invoke(runners, lookup);
        var runner = lookup[1]!;
        return (bool)runner.GetType().GetField("_rendering", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runner)!;
    }
}
