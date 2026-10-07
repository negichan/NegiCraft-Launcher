using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftSkinRender;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;
using NegiCraftLauncher.Skin.Controls;
using NegiCraftLauncher.Skin.Rendering;

internal static class Program
{
    private static int _failed;

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
        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
        if (!ok) _failed++;
    }

    private static async Task Run(Action done)
    {
        Window? host = null;
        PetWindow? pet = null;
        try
        {
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

    private static bool IsRendering(FrameworkElement pop)
    {
        var runners = typeof(Pop).GetField("Runners", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var lookup = new object?[] { pop, null };
        runners.GetType().GetMethod("TryGetValue")!.Invoke(runners, lookup);
        var runner = lookup[1]!;
        return (bool)runner.GetType().GetField("_rendering", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runner)!;
    }
}
