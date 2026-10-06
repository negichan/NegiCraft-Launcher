using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.Pet.Debug;

namespace NegiCraftLauncher.Pet.App;

/// <summary>
/// 独立桌宠（<c>NegiPet.exe</c>）的入口。对应 Avalonia 侧
/// <c>Pet.App/App.axaml.cs</c>，行为逐条对齐。
/// </summary>
public partial class App : Application
{
    /// <summary>用户没选名字、命令行也没给时用的名字。</summary>
    private const string FallbackName = "pingplus";

    private PetWindow? _petWindow;
    private StandalonePetTray? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandledException;

        // NegiPet.exe [名字]
        var args = Environment.GetCommandLineArgs();
        var cliName = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;

        var settings = PetSettings.Load();

        if (!string.IsNullOrWhiteSpace(cliName))
        {
            settings.CustomName = cliName;
            settings.Save();
        }

        // 需求：独立版一开始也没有名字，没啥能显示的，所以没有名字或者皮肤时我们要跳出设置窗口让他设置才行
        if (!settings.HasConfiguredIdentity && !PetDebugMailbox.IsEnabled)
        {
            // 切换为显式退出，避免设置向导关窗瞬间触发 OnLastWindowClose 导致应用关闭
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var setup = new PetSettingsWindow(settings, isFirstRunSetup: true);
            setup.ShowDialog();
            if (setup.CloseResult != true)
            {
                Shutdown();
                return;
            }
            ShutdownMode = ShutdownMode.OnLastWindowClose;
        }

        var effectiveName = !string.IsNullOrWhiteSpace(settings.CustomName)
            ? settings.CustomName
            : FallbackName;

        var host = new StandalonePetHost(effectiveName);
        var petWindow = new PetWindow(host.EffectiveName, host)
        {
            // 只有独立版给桌宠上图标；共用的 PetWindow.xaml 故意不带图标，
            // 这样进程内托管的那份仍然保持启动器的身份。
            // 注意 pack URI 的 authority 是**程序集名 NegiPet**，不是工程名。
            Icon = new BitmapImage(new Uri("pack://application:,,,/NegiPet;component/Assets/NegiPet.ico")),
        };

        _petWindow = petWindow;

        // 应用所选皮肤、缩放、视线追踪与形变配置
        petWindow.ApplySettings(settings);

        // 没有启动器帮忙定位，不然桌宠会落在左上角。
        petWindow.PlaceAtDefaultCorner();

        // ShutdownMode 在 App.xaml 里声明为 OnLastWindowClose。
        MainWindow = petWindow;
        petWindow.Show();

        // 桌宠是个没有标题栏、贴在桌面上的小人，收起 / 结束它都得先找到它 —— 所以独立版要有托盘。
        _tray = new StandalonePetTray(petWindow);

        // 与启动器同一套文件邮箱桥，只是去掉所有启动器专属动词。
        // 只在 --debug 时打开；邮箱目录是 %TEMP%\ncl-pet-debug。
        StandaloneDebugBridge.StartIfNeeded(petWindow, _tray);
    }

    /// <summary>
    /// 托盘图标是 Explorer 持有的，进程不显式释放就会**残留**在托盘里直到 Explorer 刷新。
    /// 挂在 OnExit 上而不是只挂在「退出桌宠」那一项上 —— 桌宠自己菜单里的「关闭桌宠」走的是
    /// OnLastWindowClose，那条路也得把图标收掉。
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _tray = null;
        _petWindow = null;

        base.OnExit(e);
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
