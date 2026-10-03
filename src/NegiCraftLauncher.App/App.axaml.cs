using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NegiCraftLauncher.App.ViewModels;
using NegiCraftLauncher.App.Views;

namespace NegiCraftLauncher.App;

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
            // NegiPet.exe (src/NegiCraftLauncher.Pet.App), so this entry point only starts the launcher.
            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = window;
            Services.DebugBridge.StartIfNeeded(window);
        }

        base.OnFrameworkInitializationCompleted();
    }
}