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
            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(),
            };
            desktop.MainWindow = window;
            Services.DebugBridge.StartIfNeeded(window);
        }

        base.OnFrameworkInitializationCompleted();
    }
}