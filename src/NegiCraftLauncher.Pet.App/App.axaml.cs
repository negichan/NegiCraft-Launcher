using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using NegiCraftLauncher.Pet;

namespace NegiCraftLauncher.Pet.App;

public partial class App : Application
{
    /// <summary>Name used when the user has not chosen one and none was passed on the command line.</summary>
    private const string FallbackName = "pingplus";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // NegiPet.exe [名字]
            var args = Environment.GetCommandLineArgs();
            var cliName = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : FallbackName;

            var host = new StandalonePetHost(cliName);
            var petWindow = new PetWindow(host.EffectiveName, host)
            {
                // Only the standalone build gets the pet icon; the shared PetWindow.axaml stays icon-less
                // so the embedded pet keeps the launcher's identity.
                // Note the authority is the *assembly* name (NegiPet), not the project name.
                Icon = new WindowIcon(AssetLoader.Open(
                    new Uri("avares://NegiPet/Assets/NegiPet.ico")))
            };

            // Without a launcher to position it, the pet would otherwise land at the top-left corner.
            petWindow.PlaceAtDefaultCorner();

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.MainWindow = petWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
