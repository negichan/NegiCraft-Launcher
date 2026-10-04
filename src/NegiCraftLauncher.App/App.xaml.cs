using System.Windows;
using NegiCraftLauncher.App.Probe;

namespace NegiCraftLauncher.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // --selftest：无头跑完风险探针就退出，结果写到 %TEMP%\ncl-wpf-selftest.txt。
        // 这是 P1 阶段的验证入口，等真正的主窗口接进来之后可以删掉。
        if (Array.IndexOf(e.Args, "--selftest") >= 0)
        {
            SelfTest.Run();
            Shutdown();
            return;
        }

        // --bench：跑软件光栅化器的 ms/帧 实测，结果写到 %TEMP%\ncl-wpf-bench.txt。
        if (Array.IndexOf(e.Args, "--bench") >= 0)
        {
            Bench.Run();
            Shutdown();
            return;
        }

        // --gpu3d：WPF 3D 探针（材质 / 相机 / 分层透明窗）。它自己开窗、自己抓屏、自己 Shutdown，
        // 所以这里**不能**跟着 Shutdown —— 窗口得活着等合成器把帧推出去。
        // 加 --opaque 跑"不透明窗"那一组，用来隔离 AllowsTransparency 这个变量。
        if (Array.IndexOf(e.Args, "--gpu3d") >= 0)
        {
            Gpu3dProbe.Run(e.Args);
            return;
        }

        // --skin <png>：不启窗口，直接出一张软件光栅化的皮肤预览，用来和 GL 版截图对齐。
        // --compare <a.png> --with <b.png>：逐像素比两张预览图。
        if (Array.IndexOf(e.Args, "--skin") >= 0 || Array.IndexOf(e.Args, "--compare") >= 0)
        {
            SkinPreview.Run(e.Args);
            Shutdown();
            return;
        }

        // --theme：把主题画廊摆出来截一张图 + 逐控件检查样式有没有命中。
        if (Array.IndexOf(e.Args, "--theme") >= 0)
        {
            ThemeGallery.Run();
            Shutdown();
            return;
        }

        // 共享层不依赖任何 UI 框架：调度器与主题都得由平台侧注入。
        // 必须赶在构造 VM 之前 —— VM 初始化时就会去拉皮肤与版本列表。
        ViewModels.AppDispatcher.Current = new Services.WpfUiDispatcher(Dispatcher);

        // --ui：把真正的主窗口摆在屏幕外逐页出图（自己收尾，不在这里 Shutdown）。
        if (Array.IndexOf(e.Args, "--ui") >= 0)
        {
            UiShot.Run(e.Args);
            return;
        }

        var vm = new ViewModels.MainWindowViewModel();

        var window = new MainWindow { DataContext = vm };
        MainWindow = window;
        window.Show();

        // 只在 --debug 启动时开本地控制通道（%TEMP%\ncl-debug）。
        // 必须在 Show 之后：桥要拿窗口的 HWND，也要能读到已经建好的可视树。
        Services.DebugBridge.StartIfNeeded(window);
    }
}
