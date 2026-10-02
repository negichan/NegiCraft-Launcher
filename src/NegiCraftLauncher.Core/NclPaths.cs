using System;
using System.IO;

namespace NegiCraftLauncher.Core;

/// <summary>
/// Launcher-owned locations. Everything the launcher writes lives under <see cref="AppDataRoot"/>;
/// the Minecraft tree it manages lives under a separate, user-movable game root.
/// </summary>
public static class NclPaths
{
    public static readonly string AppDataRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCL");

    public static readonly string DefaultGameRoot = Path.Combine(AppDataRoot, "minecraft");

    public static readonly string SettingsFile = Path.Combine(AppDataRoot, "settings.json");
    public static readonly string AccountsFile = Path.Combine(AppDataRoot, "accounts.json");
    public static readonly string InstancesFile = Path.Combine(AppDataRoot, "instances.json");
    public static readonly string LogDirectory = Path.Combine(AppDataRoot, "logs");
    public static readonly string CacheDirectory = Path.Combine(AppDataRoot, "cache");

    public static readonly string VersionManifestCache =
        Path.Combine(CacheDirectory, "version_manifest_v2.json");

    public static string VersionsDirectory(string gameRoot) => Path.Combine(gameRoot, "versions");
    public static string LibrariesDirectory(string gameRoot) => Path.Combine(gameRoot, "libraries");
    public static string AssetsDirectory(string gameRoot) => Path.Combine(gameRoot, "assets");
    public static string AssetIndexesDirectory(string gameRoot) => Path.Combine(AssetsDirectory(gameRoot), "indexes");
    public static string AssetObjectsDirectory(string gameRoot) => Path.Combine(AssetsDirectory(gameRoot), "objects");

    public static string InstanceDirectory(string gameRoot, string instanceId) =>
        Path.Combine(VersionsDirectory(gameRoot), instanceId);

    public static string InstanceVersionJson(string gameRoot, string instanceId) =>
        Path.Combine(InstanceDirectory(gameRoot, instanceId), instanceId + ".json");

    public static string InstanceClientJar(string gameRoot, string instanceId) =>
        Path.Combine(InstanceDirectory(gameRoot, instanceId), instanceId + ".jar");

    /// <summary>Directory the JVM process is started in; kept inside the game tree so relative log paths land there.</summary>
    public static string RuntimeDirectory(string gameRoot) => Path.Combine(gameRoot, "runtime");

    public static string NativesDirectory(string gameRoot, string instanceId) =>
        Path.Combine(RuntimeDirectory(gameRoot), "natives", instanceId);

    public static void EnsureDirectory(string path)
    {
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
    }

    public static void EnsureLauncherDirectories()
    {
        EnsureDirectory(AppDataRoot);
        EnsureDirectory(LogDirectory);
    }
}
