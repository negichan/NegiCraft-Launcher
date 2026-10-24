using System.Reflection;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MinecraftSkinRender;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Modpacks;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.Raster.Rendering;
using NegiCraftLauncher.Skin.Controls;
using NegiCraftLauncher.Skin.Rendering;
using NegiCraftLauncher.Theme;
using NegiCraftLauncher.ViewModels;

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
            if (Environment.GetCommandLineArgs().Contains("--pet-texture-only"))
            {
                GpuTextureChecks.Run(Check);
                return;
            }

            if (Environment.GetCommandLineArgs().Contains("--fox-only"))
            {
                await CheckFoxSqueeze();
                return;
            }

            GpuTextureChecks.Run(Check);

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
                // 动画被系统关掉时 Pop 会当场落地并把 RenderTransform 归零（见 Pop.Settle），
                // 所以下面读姿态得能接住 null —— 否则这台机器上直接空引用崩掉。
                Matrix Pose() => pop.RenderTransform?.Value ?? Matrix.Identity;
                var before = Pose();
                var opacity = pop.Opacity;
                Pop.SetIsOpen(pop, false);
                Check("closing disables hit testing", !pop.IsHitTestVisible);
                Check("close preserves animated pose", Near(before, Pose()));
                Check("close preserves animated opacity", Math.Abs(pop.Opacity - opacity) < 0.02);
                await Task.Delay(25);
                before = Pose();
                opacity = pop.Opacity;
                Pop.SetIsOpen(pop, true);
                Check("reopen during close interactive", pop.IsHitTestVisible);
                Check("reopen preserves animated pose", Near(before, Pose()));
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

            await CheckPopoverMotion();
            await CheckUiMotion();
            await CheckPageDirection();
            await CheckMotionPreference();
            await CheckMenuSeparator();
            await CheckInstanceExport();
            CheckInstanceDeletion();
            CheckInstanceUiGuards();
            CheckModMetaReader();
            CheckOfficialLocator();
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

    /// <summary>
    /// 浮层开合的动效：入场从下往上滑 + 轻微缩放过冲，出场往回滑一点并淡出。
    /// 中途打断（开着关、关着开）要保住当下的姿态，不能跳。
    /// </summary>
    private static async Task CheckPopoverMotion()
    {
        var style = new Style(typeof(Border));
        style.Setters.Add(new Setter(UIElement.OpacityProperty, 0.0));
        var popover = new Border { Width = 280, Height = 180, Background = Brushes.Gray, Style = style };
        Pop.SetAnchor(popover, "Center");
        var host = new Window
        {
            Width = 420, Height = 320, Content = popover,
            ShowActivated = false, ShowInTaskbar = false,
        };
        try
        {
            host.Show();
            await Task.Delay(60);
            var size = popover.RenderSize;
            Matrix Pose() => popover.RenderTransform?.Value ?? Matrix.Identity;
            Pop.SetIsOpen(popover, true);
            var transform = popover.RenderTransform ?? Transform.Identity;
            if (SystemParameters.ClientAreaAnimation)
            {
                Check("popover entry starts just below its resting position",
                    Math.Abs(Pose().OffsetY - 8) < 0.01);
                Check("popover entry starts slightly scaled down",
                    Pose().M11 < 0.99);
                var previousY = 8.0;
                var previousOpacity = 0.0;
                var monotonic = true;
                var fixedLayout = true;
                var maxScale = 0.0;
                for (var frame = 0; frame < 20; frame++)
                {
                    await Task.Delay(16);
                    var matrix = Pose();
                    monotonic &= matrix.OffsetY >= -0.01 && matrix.OffsetY <= previousY + 0.01 &&
                                 popover.Opacity >= previousOpacity - 0.01;
                    // 缩放是渲染变换，不动布局：排版尺寸全程不变才是"没跳"。
                    fixedLayout &= popover.RenderSize == size;
                    maxScale = Math.Max(maxScale, matrix.M11);
                    previousY = matrix.OffsetY;
                    previousOpacity = popover.Opacity;
                }
                Check("popover slides upward and fades in without overshooting", monotonic);
                Check("popover opening keeps its layout size", fixedLayout);
                Check("popover opening scales up past its resting size", maxScale > 1.0);
            }
            Check("popover settles at its original position and releases clocks",
                popover.Opacity == 1 && Pose().IsIdentity &&
                !HasClocks(popover, transform) && popover.CacheMode is null);

            Pop.SetIsOpen(popover, false);
            Check("popover closing immediately disables interaction", !popover.IsHitTestVisible);
            if (SystemParameters.ClientAreaAnimation)
            {
                var previousY = 0.0;
                var previousOpacity = 1.0;
                var monotonic = true;
                var maxY = 0.0;
                var fixedLayout = true;
                for (var frame = 0; frame < 17; frame++)
                {
                    await Task.Delay(16);
                    if (popover.Opacity <= 0.001) continue;
                    var matrix = Pose();
                    monotonic &= matrix.OffsetY >= previousY - 0.01 && matrix.OffsetY <= 12.01 &&
                                 popover.Opacity <= previousOpacity + 0.01;
                    fixedLayout &= popover.RenderSize == size;
                    maxY = Math.Max(maxY, matrix.OffsetY);
                    previousY = matrix.OffsetY;
                    previousOpacity = popover.Opacity;
                }
                Check("popover slides slightly downward while fading out", monotonic && maxY > 0.1);
                Check("popover closing keeps its layout size", fixedLayout);
            }
            Check("popover closed state releases clocks and cache",
                popover.Opacity == 0 && !HasClocks(popover, transform) && popover.CacheMode is null);

            Pop.SetIsOpen(popover, true);
            await Task.Delay(35);
            Pop.SetIsOpen(popover, false);
            await Task.Delay(25);
            var pose = Pose();
            var opacity = popover.Opacity;
            Pop.SetIsOpen(popover, true);
            // 动画被系统关掉时浮层是当场落地的（没有"中途"可言），这条只在动画开着时判。
            if (SystemParameters.ClientAreaAnimation)
                Check("popover interrupted slide preserves its pose and opacity",
                    Near(pose, Pose()) && Math.Abs(popover.Opacity - opacity) < 0.02);
            await Task.Delay(320);
            Check("popover interrupted slide settles without residual animation",
                popover.Opacity == 1 && Pose().IsIdentity &&
                !HasClocks(popover, transform) && popover.CacheMode is null);
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

            // 空闲按钮不该起动画：ButtonMotion 挂在每个按钮的模板根上，换页时一屏按钮
            // 全部"变可见"，每个都无条件起三条 tween 的话，一次换页就是几百条空转
            // （实测 492 条）。判据是"当前值已经等于目标值就不起"。
            ButtonMotion.SetHovered(root, false);
            await Task.Delay(250);
            Check("idle button rests at its neutral pose",
                Math.Abs(scale.ScaleX - 1) < 0.001 && !HasClocks(root, transform));

            // 真正触发 Update() 的那条路是"变可见"（换页 ⇒ 整屏按钮 IsVisibleChanged）。
            // 此时按钮既没按也没悬停，不该起任何 tween。
            var beforeIdle = MotionEngine.StartedCount;
            host.Hide();
            host.Show();
            await Task.Delay(250);
            Check("an idle button becoming visible starts no animation",
                MotionEngine.StartedCount == beforeIdle);
            Check("the idle button still rests at its neutral pose",
                Math.Abs(scale.ScaleX - 1) < 0.001 && !HasClocks(root, transform));

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

    /// <summary>
    /// 页面切换的方向：按侧栏竖直顺序，往下走从上方进来，往回走从下方进来，
    /// 首页只淡入不位移。
    ///
    /// <para>这里测的是<b>共享层那份判据</b>（<c>PageOrder</c>）—— 两端视图层都读它，
    /// 是这个改动里唯一能脱离 UI 单独验的部分。视图层怎么把方向画出来由探针
    /// （<c>--ui</c> 的 page direction 审计）负责。</para>
    /// </summary>
    private static async Task CheckPageDirection()
    {
        var order = typeof(NegiCraftLauncher.ViewModels.PageOrder);

        PageDirection Between(string? from, string to) =>
            (PageDirection)order.GetMethod("Between")!
                .Invoke(null, new object?[] { from, to })!;

        Check("page direction: down the sidebar slides in from above",
            Between("home", "instances") == PageDirection.FromAbove);
        Check("page direction: down again stays from above",
            Between("instances", "download") == PageDirection.FromAbove);
        Check("page direction: back up slides in from below",
            Between("download", "instances") == PageDirection.FromBelow);
        Check("page direction: the home page only fades in",
            Between("instances", "home") == PageDirection.None &&
            Between("settings", "home") == PageDirection.None);
        Check("page direction: settings sits at the bottom of the sidebar",
            Between("download", "settings") == PageDirection.FromAbove &&
            Between("settings", "download") == PageDirection.FromBelow);
        Check("page direction: the instance config page is one level below the list",
            Between("instances", "instance_manage") == PageDirection.FromAbove);
        Check("page direction: an unknown origin does not move the page",
            Between(null, "instances") == PageDirection.None);
        Check("page direction: staying on the same page does not move it",
            Between("instances", "instances") == PageDirection.None);

        await Task.CompletedTask;
    }

    /// <summary>
    /// 「界面动画」那一档：跟随系统 / 总是开 / 总是关。
    /// 判据在共享层，两端用的是同一份。
    /// </summary>
    private static async Task CheckMotionPreference()
    {
        bool? Override(MotionPreference p) => p.Override();

        Check("motion preference: follow the system means 'do not intervene'",
            Override(MotionPreference.System) is null);
        Check("motion preference: always on overrides the system switch",
            Override(MotionPreference.AlwaysOn) == true);
        Check("motion preference: always off overrides the system switch",
            Override(MotionPreference.AlwaysOff) == false);

        // 存坏了不能让应用起不来：认不出的值退回"跟随系统"。
        Check("motion preference: an unknown stored value falls back to following the system",
            MotionPreferenceExtensions.Parse("nonsense") == MotionPreference.System &&
            MotionPreferenceExtensions.Parse(null) == MotionPreference.System &&
            MotionPreferenceExtensions.Parse("alwaysOn") == MotionPreference.AlwaysOn &&
            MotionPreferenceExtensions.Parse("alwaysOff") == MotionPreference.AlwaysOff);

        // 存进去的形式要能原样读回来。
        Check("motion preference: the stored form round-trips",
            MotionPreferenceExtensions.Parse(MotionPreference.AlwaysOn.ToStorage()) == MotionPreference.AlwaysOn &&
            MotionPreferenceExtensions.Parse(MotionPreference.AlwaysOff.ToStorage()) == MotionPreference.AlwaysOff &&
            MotionPreferenceExtensions.Parse(MotionPreference.System.ToStorage()) == MotionPreference.System);

        await Task.CompletedTask;
    }

    /// <summary>
    /// 实例右键菜单里的 <c>Separator</c>：真实弹出时必须命中主题的菜单分隔线样式。
    /// 画廊里那个是放在 StackPanel 里的假菜单，<c>MenuItem.SeparatorStyleKey</c> 这条键只有
    /// 真 ContextMenu 才会走，两个资源键漏一个就退化成空白行。
    /// </summary>
    private static async Task CheckMenuSeparator()
    {
        var menu = new ContextMenu();
        menu.Items.Add(new MenuItem { Header = "第一项" });
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = "第二项" });

        var host = new Window
        {
            Width = 200, Height = 200, Left = -32000, Top = -32000,
            ShowActivated = false, ShowInTaskbar = false,
        };

        try
        {
            host.Show();
            menu.PlacementTarget = host;
            menu.IsOpen = true;
            await Task.Delay(180);

            var separator = menu.Items.OfType<Separator>().Single();
            var color = (separator.Background as SolidColorBrush)?.Color;
            Check($"context-menu separator is a line, not a blank row (height={separator.Height}, bg={color})",
                separator.Height == 1 &&
                separator.Margin == new Thickness(4) &&
                color == Color.FromRgb(0x3F, 0x3F, 0x46));
        }
        finally
        {
            menu.IsOpen = false;
            host.Close();
        }
    }

    /// <summary>
    /// 导出整合包：Forge 1.16 的 profile 没有继承链，MC 版本只能从 clientVersion / 加载器坐标里认；
    /// 导出的包要能被现有导入解析器原样读回来，且版本本体、日志、存档与启动器元数据都不能进去。
    /// </summary>
    private static async Task CheckInstanceExport()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "NCL", "export-check", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(sandbox, "game");
        var version = Path.Combine(game, "versions", "TestPack");

        try
        {
            Directory.CreateDirectory(Path.Combine(version, "mods"));
            Directory.CreateDirectory(Path.Combine(version, "config"));
            Directory.CreateDirectory(Path.Combine(version, "logs"));
            Directory.CreateDirectory(Path.Combine(version, "crash-reports"));
            Directory.CreateDirectory(Path.Combine(version, "saves", "world"));
            Directory.CreateDirectory(Path.Combine(version, "screenshots"));
            Directory.CreateDirectory(Path.Combine(version, "natives"));

            File.WriteAllText(Path.Combine(version, "TestPack.json"), """
                {
                  "id": "TestPack",
                  "clientVersion": "1.16.5",
                  "type": "release",
                  "mainClass": "net.minecraft.client.main.Main",
                  "libraries": [ { "name": "net.minecraftforge:forge:1.16.5-36.2.20" } ]
                }
                """);
            File.WriteAllText(Path.Combine(version, "TestPack.jar"), "version body");
            File.WriteAllText(Path.Combine(version, "icon.png"), "icon");
            File.WriteAllText(Path.Combine(version, "mods", "foo.jar"), "mod");
            File.WriteAllText(Path.Combine(version, "config", "test.toml"), "config");
            File.WriteAllText(Path.Combine(version, "logs", "latest.log"), "log");
            File.WriteAllText(Path.Combine(version, "saves", "world", "level.dat"), "save");

            var manager = new InstanceManager(game, Path.Combine(sandbox, "instances.json"));
            manager.Load();
            var instance = manager.Instances.Single();
            instance.Isolated = true;

            var isolatedTarget = Path.Combine(sandbox, "isolated.mrpack");
            var result = await InstanceExporter.ExportAsync(manager, instance, isolatedTarget);
            Check("export writes an .mrpack and cleans up the .part file",
                File.Exists(isolatedTarget) && !File.Exists(isolatedTarget + ".part") && result.FileCount == 2);

            using (var archive = ModpackArchive.Open(isolatedTarget))
            {
                var manifest = ModpackParser.Parse(archive, "TestPack");
                Check("export produces a pack the importer recognizes",
                    manifest.Format == ModpackFormat.Modrinth && manifest.Name == "TestPack");
                Check("export recovers MC 1.16.5 without an inheritance chain", manifest.MinecraftVersion == "1.16.5");
                Check("export records the Forge version without the MC prefix",
                    manifest.Loader is { Kind: "forge", Version: "36.2.20" });
                Check("export keeps mods and config",
                    archive.Exists("overrides/mods/foo.jar") && archive.Exists("overrides/config/test.toml"));
                Check("export puts every file in overrides, not the download list", manifest.Files.Count == 0);
                Check("export drops the version body, icon, logs, saves and screenshots",
                    !archive.Exists("overrides/TestPack.json") && !archive.Exists("overrides/TestPack.jar") &&
                    !archive.Exists("overrides/icon.png") && !archive.HasFolder("overrides/logs") &&
                    !archive.HasFolder("overrides/crash-reports") && !archive.HasFolder("overrides/saves") &&
                    !archive.HasFolder("overrides/screenshots") && !archive.HasFolder("overrides/natives"));
            }

            // 共用目录：整个 .minecraft 都是实例的数据目录，版本/库/资源与账号文件必须排除。
            instance.Isolated = false;
            Directory.CreateDirectory(Path.Combine(game, "libraries"));
            Directory.CreateDirectory(Path.Combine(game, "assets"));
            Directory.CreateDirectory(Path.Combine(game, "saves"));
            File.WriteAllText(Path.Combine(game, "libraries", "lib.jar"), "lib");
            File.WriteAllText(Path.Combine(game, "saves", "level.dat"), "save");
            File.WriteAllText(Path.Combine(game, "launcher_accounts.json"), "{}");
            File.WriteAllText(Path.Combine(game, "options.txt"), "fov:70");

            var sharedTarget = Path.Combine(sandbox, "shared.mrpack");
            await InstanceExporter.ExportAsync(manager, instance, sharedTarget);

            using var shared = ModpackArchive.Open(sharedTarget);
            Check("shared-root export excludes versions, libraries, saves and launcher accounts",
                !shared.HasFolder("overrides/versions") && !shared.HasFolder("overrides/libraries") &&
                !shared.HasFolder("overrides/assets") && !shared.HasFolder("overrides/saves") &&
                !shared.Exists("overrides/launcher_accounts.json"));
            Check("shared-root export includes game settings",
                shared.Exists("overrides/options.txt"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响检查结果。
            }
        }
    }

    /// <summary>
    /// 删除实例 = versions/&lt;id&gt; 整目录进回收站、实例表里除名。回收站本身的内容不在此断言
    /// （那要扒 Shell 命名空间），这里守住的是「删完原位必须没了、不抛异常、不留半棵目录」。
    /// </summary>
    private static void CheckInstanceDeletion()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "NCL", "delete-check", Guid.NewGuid().ToString("N"));
        var game = Path.Combine(sandbox, "game");
        var version = Path.Combine(game, "versions", "DelPack");

        try
        {
            Directory.CreateDirectory(Path.Combine(version, "saves", "world"));
            File.WriteAllText(Path.Combine(version, "DelPack.json"), """
                {
                  "id": "DelPack",
                  "clientVersion": "1.20.1",
                  "type": "release",
                  "mainClass": "net.minecraft.client.main.Main"
                }
                """);
            File.WriteAllText(Path.Combine(version, "saves", "world", "level.dat"), "save");

            var manager = new InstanceManager(game, Path.Combine(sandbox, "instances.json"));
            manager.Load();
            manager.Delete(manager.Instances.Single().Id);

            Check("instance delete removes the version directory", !Directory.Exists(version));
            Check("instance delete forgets the instance", manager.Instances.Count == 0);

            // 单文件 / 单目录走同一条回收站路：外壳调用失败也会兜底成永久删除，原位同样必须空。
            var stray = Path.Combine(sandbox, "stray.txt");
            File.WriteAllText(stray, "x");
            NegiCraftLauncher.Core.RecycleBin.Delete(stray);
            Check("recycle delete removes the original file", !File.Exists(stray));

            var folder = Path.Combine(sandbox, "folder");
            Directory.CreateDirectory(Path.Combine(folder, "inner"));
            File.WriteAllText(Path.Combine(folder, "inner", "a.txt"), "a");
            NegiCraftLauncher.Core.RecycleBin.Delete(folder);
            Check("recycle delete removes the original directory", !Directory.Exists(folder));
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响检查结果。
            }
        }
    }

    /// <summary>
    /// 两份 UI 壳（WPF / Avalonia）上这次交互改动的防回归：保存按钮、管理页分隔线、成功横幅
    /// 不能回来；播放禁用态、刷新 / 清筛选 / 批量开关、内存上限、拖放文案的绑定不能丢。
    /// 走源码扫描而不是开真窗口 —— 这些都是「XAML 里写没写」的问题，编译器不管。
    /// </summary>
    private static void CheckInstanceUiGuards()
    {
        var root = FindRepoRoot();
        if (root is null)
        {
            Check("ui guards locate the repository", false);
            return;
        }

        var wpf = File.ReadAllText(Path.Combine(root, "src", "NegiCraftLauncher.App", "MainWindow.xaml"));
        var wpfFiles = File.ReadAllText(Path.Combine(root, "src", "NegiCraftLauncher.App", "MainWindow.Files.cs"));
        var ava = File.ReadAllText(Path.Combine(root, "src", "NegiCraftLauncher.App.Avalonia", "Views", "MainWindow.axaml"));
        var avaFiles = File.ReadAllText(Path.Combine(root, "src", "NegiCraftLauncher.App.Avalonia", "Views", "MainWindow.Files.cs"));

        const string Splitter = """<Rectangle Grid.Column="1" Width="1" Fill="#26262b" HorizontalAlignment="Center" VerticalAlignment="Stretch"/>""";

        Check("both shells drop the explicit save button", !wpf.Contains("保存配置") && !ava.Contains("保存配置"));
        Check("both shells drop the manage-page splitter line", !wpf.Contains(Splitter) && !ava.Contains(Splitter));
        Check("file pickers stop toasting success", !wpfFiles.Contains("已添加") && !avaFiles.Contains("已添加"));
        Check("both shells grey launch entry points while the game runs",
            wpf.Contains("CanQuickLaunch") && ava.Contains("CanQuickLaunch"));
        Check("both shells expose refresh / clear-filter / bulk mod toggles",
            wpf.Contains("RefreshInstancesListCommand") && ava.Contains("RefreshInstancesListCommand") &&
            wpf.Contains("ClearInstanceFiltersCommand") && ava.Contains("ClearInstanceFiltersCommand") &&
            wpf.Contains("EnableAllModsCommand") && ava.Contains("EnableAllModsCommand"));
        Check("both shells cap the memory slider by physical RAM",
            wpf.Contains("MemorySliderMax") && ava.Contains("MemorySliderMax"));
        Check("drop overlay text follows what is being dropped",
            wpf.Contains("DropOverlayText") && ava.Contains("DropOverlayText"));

        // 其他启动器 式交互：卡片和列表行都是单击设当前、双击启动，行尾/卡片的齿轮进配置。
        Check("cards and rows select on click and launch on double-click",
            Count(wpf, "OnInstanceRowDoubleClick") >= 2 && Count(ava, "OnInstanceRowDoubleTapped") >= 2 &&
            Count(wpf, "DataContext.MakeCurrentInstanceCommand") >= 2 &&
            Count(ava, "DataContext).MakeCurrentInstanceCommand") >= 2);
        Check("config entry sits beside every launch button",
            Count(wpf, "CogOutline") >= 2 && Count(ava, "CogOutline") >= 2);

        // 概览统计卡与模组元数据。
        Check("overview shows creation date and folder size stats",
            wpf.Contains("InstanceCreatedText") && ava.Contains("InstanceCreatedText") &&
            wpf.Contains("InstanceFolderSizeText") && ava.Contains("InstanceFolderSizeText"));
        Check("mod rows surface parsed mod names and versions",
            wpf.Contains("ModVersionText") && ava.Contains("ModVersionText") &&
            wpf.Contains("HasModVersion") && ava.Contains("HasModVersion"));

        // 删除只在概览的危险区出现一次（文件与管理已瘦身）。
        Check("delete lives only in the overview danger zone",
            Count(wpf, "DeleteEditingInstanceCommand") == 1 && Count(ava, "DeleteEditingInstanceCommand") == 1);

        // Java 与内存不再单列一页，拆成了独立的「设置」tab（隔离 / Java / 内存）。
        Check("settings split into its own tab beside the overview",
            !wpf.Contains("""ConfigTab, Converter={StaticResource Equals}, ConverterParameter=Java""") &&
            !ava.Contains("""ConfigTab, Converter={x:Static ObjectConverters.Equal}, ConverterParameter='Java'""") &&
            wpf.Contains("""ConfigTab, Converter={StaticResource Equals}, ConverterParameter=设置""") &&
            ava.Contains("""ConfigTab, Converter={x:Static ObjectConverters.Equal}, ConverterParameter='设置'"""));
    }

    private static int Count(string haystack, string needle) =>
        haystack.Split(new[] { needle }, StringSplitOptions.None).Length - 1;

    /// <summary>
    /// 模组 jar 元数据读取：fabric / forge 两种清单解析出名字与版本，
    /// 没有清单的 jar 与根本不是 zip 的文件都安静地退回 null（调用方亮文件名）。
    /// </summary>
    private static void CheckModMetaReader()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "NCL", "modmeta-check", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(sandbox);

            var fabricJar = Path.Combine(sandbox, "fabric-api.jar");
            using (var zip = System.IO.Compression.ZipFile.Open(fabricJar, System.IO.Compression.ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("fabric.mod.json").Open())
            using (var writer = new StreamWriter(entry))
            {
                writer.Write("""{ "id": "fabric-api", "name": "Fabric API", "version": "0.100.0+1.21" }""");
            }

            var fabric = NegiCraftLauncher.Core.ModMetaReader.Read(fabricJar);
            Check("modmeta reads fabric.mod.json", fabric is { Name: "Fabric API", Version: "0.100.0+1.21" });

            var forgeJar = Path.Combine(sandbox, "examplemod.jar");
            using (var zip = System.IO.Compression.ZipFile.Open(forgeJar, System.IO.Compression.ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("META-INF/mods.toml").Open())
            using (var writer = new StreamWriter(entry))
            {
                writer.Write("""
                    modLoader="javafml"
                    loaderVersion="[40,)"
                    [[mods]]
                    modId="examplemod"
                    version="1.0.0"
                    displayName="Example Mod"
                    """);
            }

            var forge = NegiCraftLauncher.Core.ModMetaReader.Read(forgeJar);
            Check("modmeta reads META-INF/mods.toml", forge is { Name: "Example Mod", Version: "1.0.0" });

            var bareJar = Path.Combine(sandbox, "bare.jar");
            using (System.IO.Compression.ZipFile.Open(bareJar, System.IO.Compression.ZipArchiveMode.Create)) { }
            Check("modmeta returns null for a jar without manifests", NegiCraftLauncher.Core.ModMetaReader.Read(bareJar) is null);

            var notZip = Path.Combine(sandbox, "broken.jar");
            File.WriteAllText(notZip, "this is not a zip");
            Check("modmeta returns null for a corrupt jar", NegiCraftLauncher.Core.ModMetaReader.Read(notZip) is null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
            }
            catch (Exception) { }
        }
    }

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "NegiCraftLauncher.App", "MainWindow.xaml")))
                return dir.FullName;
        }
        return null;
    }

    /// <summary>
    /// 官方启动器目录探测：桌面版与商店版两个默认位能被认出；
    /// 光有 .minecraft 没有 versions/ 的不算装过游戏。
    /// </summary>
    private static void CheckOfficialLocator()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), "NCL", "official-check", Guid.NewGuid().ToString("N"));
        try
        {
            var roaming = Path.Combine(sandbox, "roaming");
            var local = Path.Combine(sandbox, "local");
            Directory.CreateDirectory(Path.Combine(roaming, ".minecraft", "versions"));

            var found = NegiCraftLauncher.Core.OfficialMinecraft.LocateGameRoots(roaming, local);
            Check("official desktop .minecraft is located",
                found.Count == 1 && found[0].Name == "官方启动器" &&
                found[0].Path == Path.Combine(roaming, ".minecraft"));

            Directory.CreateDirectory(Path.Combine(local, "Packages",
                "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Roaming", ".minecraft", "versions"));
            found = NegiCraftLauncher.Core.OfficialMinecraft.LocateGameRoots(roaming, local);
            Check("microsoft store package location is located",
                found.Count == 2 && found[1].Name == "官方启动器（商店版）");

            // 空 .minecraft（没有 versions/）不认；基目录不存在更是一无所获。
            Directory.Delete(Path.Combine(roaming, ".minecraft", "versions"));
            found = NegiCraftLauncher.Core.OfficialMinecraft.LocateGameRoots(roaming, local);
            Check("a .minecraft without versions is ignored", found.Count == 1 && found[0].Name.Contains("商店版"));

            // 挂上 IsOfficial 的目录在 Settings 层拒绝移除；普通目录照常可移。
            var settings = NegiCraftLauncher.Core.Settings.LauncherSettings.Load(Path.Combine(sandbox, "settings.json"));
            Directory.CreateDirectory(Path.Combine(roaming, ".minecraft", "versions"));
            if (settings.TryAddFolder(Path.Combine(roaming, ".minecraft"), "官方启动器", out var official, out _))
            {
                official!.IsOfficial = true;
                var blocked = settings.TryRemoveFolder(Path.Combine(roaming, ".minecraft"), out var reason);
                Check("official folder refuses removal", !blocked && reason.Contains("官方"));
            }
            else
            {
                Check("official folder refuses removal", false);
            }

            var plainRoot = Path.Combine(sandbox, "plain");
            Directory.CreateDirectory(Path.Combine(plainRoot, "versions"));
            settings.TryAddFolder(plainRoot, "自建", out _, out _);
            Check("non-official folder still removable", settings.TryRemoveFolder(plainRoot, out _));

            // 默认命名：上一级目录名（.minecraft 尾段没区分度）；自建默认目录叫「当前文件夹」。
            Check("suggested names use the parent folder, not the .minecraft tail",
                NegiCraftLauncher.Core.Settings.LauncherSettings.SuggestFolderName(Path.Combine(sandbox, "roaming", ".minecraft")) == "roaming");
            var fresh = NegiCraftLauncher.Core.Settings.LauncherSettings.Load(Path.Combine(sandbox, "fresh-settings.json"));
            Check("the seeded own folder is named 当前文件夹",
                fresh.GameFolders.Count == 1 && fresh.GameFolders[0].Name == "当前文件夹");
        }
        finally
        {
            try
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
            }
            catch (Exception) { }
        }
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

    private static bool HasClocks(FrameworkElement target, Transform? transform) =>
        target.HasAnimatedProperties || transform?.HasAnimatedProperties == true ||
        transform is TransformGroup group && group.Children.Any(item => item.HasAnimatedProperties);

    private static bool Near(Matrix a, Matrix b) =>
        Math.Abs(a.M11 - b.M11) < 0.02 && Math.Abs(a.M22 - b.M22) < 0.02 &&
        Math.Abs(a.OffsetX - b.OffsetX) < 0.1 && Math.Abs(a.OffsetY - b.OffsetY) < 0.1;
}
