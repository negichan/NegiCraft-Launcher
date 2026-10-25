using System.IO;

namespace NegiCraftLauncher.Core;

/// <summary>官方启动器装好的一个游戏目录（.minecraft），连同给它起好的显示名。</summary>
public sealed record OfficialGameRoot(string Path, string Name);

/// <summary>
/// 探测官方 Minecraft 启动器把游戏装在了哪。只看两个固定默认位置：
/// 桌面版的 <c>%APPDATA%\.minecraft</c>，和微软商店版被包虚拟化进
/// <c>%LOCALAPPDATA%\Packages\…\LocalCache\Roaming\.minecraft</c> 的那份。
///
/// <para>官方启动器允许用户自选安装位置，但那个选择记在它自己的配置里、格式不承诺稳定，
/// 不去解析 —— 两个默认位探测不到就当没装，用户仍可「添加文件夹」手动挂。
/// 非 Windows 平台没有这两个目录，自然一无所获。</para>
/// </summary>
public static class OfficialMinecraft
{
    /// <summary>微软商店版启动器的包族名，目录结构固定（别的启动器探的也是它）。</summary>
    private const string StorePackageFamily = "Microsoft.4297127D64EC6_8wekyb3d8bbwe";

    public static IReadOnlyList<OfficialGameRoot> LocateGameRoots() =>
        LocateGameRoots(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>带基目录的重载给测试用：临时目录里摆出同样的结构就能断言。</summary>
    public static IReadOnlyList<OfficialGameRoot> LocateGameRoots(string roamingBase, string localBase)
    {
        var found = new List<OfficialGameRoot>();
        TryAdd(Path.Combine(roamingBase, ".minecraft"), "官方启动器");
        TryAdd(Path.Combine(localBase, "Packages", StorePackageFamily,
            "LocalCache", "Roaming", ".minecraft"), "官方启动器（商店版）");
        return found;

        void TryAdd(string path, string name)
        {
            if (IsPlausibleGameRoot(path)) found.Add(new OfficialGameRoot(path, name));
        }
    }

    /// <summary>有 versions/ 子目录才算真装过游戏 —— 光一个空 .minecraft 不值得挂进来。</summary>
    public static bool IsPlausibleGameRoot(string path) =>
        Directory.Exists(path) && Directory.Exists(Path.Combine(path, "versions"));
}
