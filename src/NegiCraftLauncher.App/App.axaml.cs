using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NegiCraftLauncher.App.ViewModels;
using NegiCraftLauncher.App.Views;
using NegiCraftLauncher.Pet;

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
            var args = System.Environment.GetCommandLineArgs();
            var petIdx = System.Array.FindIndex(args, a => string.Equals(a, "--pet", System.StringComparison.OrdinalIgnoreCase));
            if (petIdx >= 0)
            {
                var playerName = (petIdx + 1 < args.Length && !args[petIdx + 1].StartsWith('-'))
                    ? args[petIdx + 1]
                    : "pingplus";
                var petWindow = new PetWindow(playerName);
                desktop.MainWindow = petWindow;
                base.OnFrameworkInitializationCompleted();
                return;
            }

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