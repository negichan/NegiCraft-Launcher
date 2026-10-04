using System.ComponentModel;
using NegiCraftLauncher.Pet;

namespace NegiCraftLauncher.Pet.App;

/// <summary>
/// 独立桌宠的宿主：没有账号，也没有启动器。只有名字值得保存，所以走
/// <see cref="PetSettings"/>（<c>%APPDATA%\NCL\pet.json</c>）；
/// 进程内托管的那份把名字存在启动器的设置里。
///
/// <para>与 Avalonia 版 <c>Pet.App/StandalonePetHost.cs</c> 逻辑一致，只换了命名空间。</para>
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

    /// <summary>没有启动器，就没有账号可以列。</summary>
    public IReadOnlyList<string> AccountNames => Array.Empty<string>();

    public bool CanOpenLauncher => false;

    /// <summary>独立版没有启动器设置页，所以这个值只由桌宠自己的右键菜单改，存在 pet.json 里。</summary>
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

    /// <summary>永远走不到：<see cref="CanOpenLauncher"/> 为 false 时菜单项是隐藏的。</summary>
    public void OpenLauncher()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
