using System;
using System.Collections.Generic;
using System.ComponentModel;
using NegiCraftLauncher.Pet;

namespace NegiCraftLauncher.Pet.App.Avalonia;

/// <summary>
/// The standalone pet's host: no accounts, no launcher. The name is the only thing worth keeping, so
/// it is persisted through <see cref="PetSettings"/> (the launcher-embedded pet stores it in the
/// launcher's settings instead).
/// </summary>
internal sealed class StandalonePetHost : IPetHost
{
    private readonly PetSettings _settings = PetSettings.Load();
    private readonly string _fallbackName;

    public StandalonePetHost(string fallbackName) => _fallbackName = fallbackName;

    public string? CustomName
    {
        get => _settings.CustomName;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_settings.CustomName == trimmed) return;

            _settings.CustomName = trimmed;
            _settings.Save();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveName)));
        }
    }

    public string EffectiveName =>
        string.IsNullOrWhiteSpace(_settings.CustomName) ? _fallbackName : _settings.CustomName;

    /// <summary>No launcher, so no accounts to offer.</summary>
    public IReadOnlyList<string> AccountNames => Array.Empty<string>();

    public bool CanOpenLauncher => false;

    /// <summary>The standalone build has no settings page, so this is only ever changed from the pet's own menu.</summary>
    public bool UseGpu
    {
        get => _settings.UseGpu;
        set
        {
            if (_settings.UseGpu == value) return;

            _settings.UseGpu = value;
            _settings.Save();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseGpu)));
        }
    }

    /// <summary>Never reachable: the menu item is hidden when <see cref="CanOpenLauncher"/> is false.</summary>
    public void OpenLauncher()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
