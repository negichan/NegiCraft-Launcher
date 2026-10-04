using System.ComponentModel;
using System.Windows;
using NegiCraftLauncher.Pet;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Wpf.Services;

/// <summary>
/// 把启动器（VM + 主窗口）接到桌宠的 <see cref="IPetHost"/> 契约上，让进程内托管的桌宠
/// 保留启动器专属能力 —— 继承当前账号名、在换肤菜单里列出账号、重新打开启动器 ——
/// 而桌宠本体完全不知道启动器的存在。
///
/// <para>与 Avalonia 版 <c>App/Services/PetHostAdapter.cs</c> 逻辑一字不差，
/// 只有 <c>Avalonia.Controls.Window</c> → <see cref="Window"/> 这一处平台替换。</para>
/// </summary>
internal sealed class PetHostAdapter : IPetHost
{
    private readonly MainWindowViewModel _vm;
    private readonly Window _launcherWindow;

    // VM 活得比桌宠窗口久，而桌宠一个会话里可以被开关很多次。在构造函数里就订阅的话，
    // 每开关一次都会留下一个收不到消息的适配器（连带一个已关闭的窗口），
    // 所以只在桌宠真正挂上来的时候才监听。
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

    public void OpenLauncher()
    {
        // WPF 的 Window.IsVisible 是只读的（Avalonia 那边可写），恢复显示要用 Show()。
        // 窗口本来就可见时 Show() 是空操作，不会重复触发 Loaded。
        _launcherWindow.Show();
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
        // 把启动器的属性名改成契约里的名字，桌宠才能匹配上。
        if (e.PropertyName == nameof(MainWindowViewModel.EffectivePetName))
        {
            _listeners?.Invoke(this, new PropertyChangedEventArgs(nameof(EffectiveName)));
        }
    }
}
