using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using NegiCraftLauncher.ViewModels;
using NegiCraftLauncher.Pet;

namespace NegiCraftLauncher.App.Services;

/// <summary>
/// Bridges the launcher (view model + main window) to the pet's <see cref="IPetHost"/> contract, so
/// the embedded pet keeps its launcher-only behaviour — inherit the current account name, list
/// accounts in the skin menu, reopen the launcher — without the pet library knowing about any of it.
/// </summary>
internal sealed class PetHostAdapter : IPetHost
{
    private readonly MainWindowViewModel _vm;
    private readonly Window _launcherWindow;

    // The VM outlives the pet window, and the pet can be toggled many times per session. Subscribing
    // eagerly in the constructor would strand one adapter (and thus one closed window) per toggle, so
    // only listen while the pet is actually attached.
    private PropertyChangedEventHandler? _listeners;

    public PetHostAdapter(MainWindowViewModel vm, Window launcherWindow)
    {
        _vm = vm;
        _launcherWindow = launcherWindow;
    }

    public string? CustomName
    {
        get => _vm.PetCustomName;
        set => _vm.PetCustomName = value;
    }

    public string EffectiveName => _vm.EffectivePetName;

    public IReadOnlyList<string> AccountNames => _vm.Accounts.Select(a => a.Name).ToList();

    public bool CanOpenLauncher => true;

    // GPU 后端目前只在 Windows(WPF) 侧存在（Viewport3D）。这里照契约把值存进启动器设置，
    // 免得两边共用一个 settings.json 时把 Windows 侧选的值抹掉；Avalonia 的桌宠窗口读到了也不会用。
    public bool UseGpu
    {
        get => _vm.PetUseGpu;
        set => _vm.PetUseGpu = value;
    }

    public void OpenLauncher()
    {
        _launcherWindow.IsVisible = true;
        _launcherWindow.WindowState = WindowState.Normal;
        _launcherWindow.Activate();
    }

    public event PropertyChangedEventHandler? PropertyChanged
    {
        add
        {
            if (_listeners is null) _vm.PropertyChanged += OnVmPropertyChanged;
            _listeners += value;
        }
        remove
        {
            _listeners -= value;
            if (_listeners is null) _vm.PropertyChanged -= OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Rename the launcher's property to the contract's so the pet can match on it.
        if (e.PropertyName == nameof(MainWindowViewModel.EffectivePetName))
        {
            _listeners?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveName)));
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.PetUseGpu))
        {
            _listeners?.Invoke(this, new PropertyChangedEventArgs(nameof(UseGpu)));
        }
    }
}
