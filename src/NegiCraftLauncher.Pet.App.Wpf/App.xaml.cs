using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NegiCraftLauncher.Pet.Wpf;

namespace NegiCraftLauncher.Pet.App.Wpf;

/// <summary>
/// 独立桌宠（<c>NegiPet.exe</c>）的入口。对应 Avalonia 侧
/// <c>Pet.App/App.axaml.cs</c>，行为逐条对齐。
/// </summary>
public partial class App : Application
{
    /// <summary>用户没选名字、命令行也没给时用的名字。</summary>
    private const string FallbackName = "pingplus";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        // NegiPet.exe [名字]
        var args = Environment.GetCommandLineArgs();
        var cliName = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : FallbackName;

        var host = new StandalonePetHost(cliName);
        var petWindow = new PetWindow(host.EffectiveName, host)
        {
            // 只有独立版给桌宠上图标；共用的 PetWindow.xaml 故意不带图标，
            // 这样进程内托管的那份仍然保持启动器的身份。
            // 注意 pack URI 的 authority 是**程序集名 NegiPet**，不是工程名。
            Icon = new BitmapImage(new Uri("pack://application:,,,/NegiPet;component/Assets/NegiPet.ico")),
        };

        // 没有启动器帮忙定位，不然桌宠会落在左上角。
        petWindow.PlaceAtDefaultCorner();

        // ShutdownMode 在 App.xaml 里声明为 OnLastWindowClose。
        MainWindow = petWindow;
        petWindow.Show();

        // 与启动器同一套文件邮箱桥，只是去掉所有启动器专属动词。
        // 只在 --debug 时打开；邮箱目录是 %TEMP%\ncl-pet-debug。
        StandaloneDebugBridge.StartIfNeeded(petWindow);
    }

    /// <summary>
    /// 与 Avalonia 版 <c>Program.cs</c> 里的 try/catch 等价 —— 那边是包住整个
    /// <c>StartWithClassicDesktopLifetime</c>，WPF 的入口是自动生成的，所以改成
    /// 挂 <c>DispatcherUnhandledException</c>，落盘位置保持 <c>crash.log</c>。
    /// </summary>
    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            File.WriteAllText("crash.log", e.Exception.ToString());
        }
        catch
        {
            // 写日志失败就算了，别在异常处理里再抛一次。
        }
    }
}
