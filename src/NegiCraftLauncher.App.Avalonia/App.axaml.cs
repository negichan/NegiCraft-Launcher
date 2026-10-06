using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using NegiCraftLauncher.App.Avalonia.Views;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Avalonia;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // A bare pet process used to be reachable here via --pet. That is now the standalone
            // NegiPet.exe (src/NegiCraftLauncher.Pet.App.Avalonia), so this entry point only starts the launcher.

            // 共享层不依赖任何 UI 框架：调度器与主题都得由平台侧注入。
            AppDispatcher.Current = new Services.AvaloniaUiDispatcher();

            // 壁纸解码器也在这里注入：VM 只持有平台中立的像素，调色才有唯一一份实现
            // （见 ViewModels/IBitmapDecoder.cs）。必须赶在构造 VM 之前定好 —— 构造里就会去解背景图。
            var vm = new MainWindowViewModel(new Services.AvaloniaBitmapDecoder());

            // The VM applies the saved theme before anyone subscribes, so seed it here.
            ApplyTheme(vm.IsDark);
            vm.ThemeChanged += ApplyTheme;

            var window = new MainWindow
            {
                DataContext = vm,
            };
            desktop.ShutdownMode = global::Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = window;
            Services.DebugBridge.StartIfNeeded(window);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void ApplyTheme(bool dark) =>
        Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
}
