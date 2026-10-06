using System;
using System.IO;
using System.Text.Json;

namespace NegiCraftLauncher.Pet;

/// <summary>
/// Name storage for the standalone pet.
///
/// The launcher-embedded pet keeps its name in the launcher's own settings.json (via
/// <see cref="IPetHost"/>), because there the name belongs to the launcher. The standalone build
/// has no launcher to own it, so it keeps <c>%APPDATA%\NCL\pet.json</c> instead. The two stores are
/// deliberately independent — nothing syncs them.
/// </summary>
public sealed class PetSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>User-chosen name; null means "use the default name".</summary>
    public string? CustomName { get; set; }

    /// <summary>
    /// 桌宠是否用 GPU 硬件渲染（WPF <c>Viewport3D</c> 后端）。
    ///
    /// <para>只有 Windows 侧有这条后端；Avalonia 侧本来就走 OpenGL，读到 true 也无处可用。
    /// 默认 false —— 软件光栅化是回归基线，GPU 是可选加速项。</para>
    /// </summary>
    public bool UseGpu { get; set; }

    /// <summary>本地皮肤文件绝对路径。如果有效且存在，启动时优先使用。</summary>
    public string? SkinPath { get; set; }

    /// <summary>用于获取皮肤的玩家名称或预设名称（如 steve, alex, miku_mew 或正版用户名）。</summary>
    public string? SkinPlayerName { get; set; }

    /// <summary>皮肤模型类型：auto, classic (4px), slim (3px)。</summary>
    public string SkinModel { get; set; } = "auto";

    /// <summary>显示缩放比例，默认 1.0 (100%)。</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>视线是否跟随鼠标。</summary>
    public bool LookAtMouse { get; set; } = true;

    /// <summary>细分脊椎与四肢自由弯曲形变。</summary>
    public bool SpineFlexible { get; set; } = true;

    /// <summary>是否保持窗口置顶。</summary>
    public bool Topmost { get; set; } = true;

    /// <summary>默认交互模式 (Free, Control, FollowMouse)。</summary>
    public string InteractionMode { get; set; } = "Free";

    /// <summary>
    /// 检查是否已有明确配置的名字或有效皮肤。
    /// 当没有名字或者没有皮肤时，首次启动应主动跳出设置窗口让用户配置。
    /// </summary>
    public bool HasConfiguredIdentity =>
        !string.IsNullOrWhiteSpace(CustomName) &&
        ((!string.IsNullOrWhiteSpace(SkinPath) && File.Exists(SkinPath)) || !string.IsNullOrWhiteSpace(SkinPlayerName));

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCL", "pet.json");

    /// <summary>Reads the settings, falling back to defaults when absent or unreadable.</summary>
    public static PetSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<PetSettings>(File.ReadAllText(FilePath), JsonOptions)
                       ?? new PetSettings();
            }
        }
        catch
        {
            // A corrupt or unreadable file must not stop the pet from starting.
        }

        return new PetSettings();
    }

    /// <summary>Best-effort persist; failures are non-fatal.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
        }
    }
}
