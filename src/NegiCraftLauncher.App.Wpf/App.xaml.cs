using System.Windows;
using NegiCraftLauncher.App.Wpf.Probe;

namespace NegiCraftLauncher.App.Wpf;

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

        MainWindow = new MainWindow();
        MainWindow.Show();
    }
}
