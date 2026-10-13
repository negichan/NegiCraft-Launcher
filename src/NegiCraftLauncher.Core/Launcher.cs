using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Launch;
using NegiCraftLauncher.Core.Modpacks;
using NegiCraftLauncher.Core.Net;
using NegiCraftLauncher.Core.Settings;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core;

/// <summary>
/// The single entry point the UI talks to: owns settings, accounts and instances, resolves the
/// download source once per run, and drives install and launch.
///
/// <para>The launcher can watch several game folders at once. <see cref="Instances"/> is the manager
/// for the folder the instances page is currently looking at; switching folders swaps it and rescans,
/// so a manager never holds instances from two roots.</para>
/// </summary>
public sealed class Launcher
{
    private IDownloadSource? _source;

    public LauncherSettings Settings { get; }
    public AccountStore Accounts { get; }
    public InstanceManager Instances { get; private set; }
    public VersionManifestProvider Manifests { get; } = new();

    public GameSession? RunningSession { get; private set; }
    public bool IsGameRunning => RunningSession is { HasExited: false };

    public string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) is { } v ? v : "1.0.0";

    public Launcher()
    {
        NclPaths.EnsureLauncherDirectories();
        Settings = LauncherSettings.Load();
        Accounts = new AccountStore();
        Instances = CreateManager(Settings.ActiveFolder.Path);
    }

    public void Initialize()
    {
        // 老那份不分目录的元数据先挂到默认目录下，之后每个目录只读自己那一段。
        InstanceManager.AdoptLegacyMetadata(Settings.DefaultFolder.Path);
        Accounts.Load();
        Instances.Load();
    }

    public IReadOnlyList<GameFolder> Folders => Settings.GameFolders;

    public GameFolder ActiveFolder => Settings.ActiveFolder;

    private static InstanceManager CreateManager(string gameRoot) => new(gameRoot);

    /// <summary>
    /// 数一数某个目录里现在有几个实例 —— "添加已有目录"那句校验提示要看它。
    /// 不缓存：目录列表本来就短，扫一次比留着一份可能过期的计数便宜。
    /// </summary>
    public int CountInstancesIn(string path)
    {
        var manager = CreateManager(NclPaths.NormaliseGameRoot(path));
        manager.Load();
        return manager.Instances.Count;
    }

    /// <summary>切到另一个游戏目录：重扫它自己的 <c>versions/</c>，并带出它记住的那个当前实例。</summary>
    public void SelectFolder(string path)
    {
        if (!Settings.TrySetActiveFolder(path) ||
            string.Equals(Instances.GameRoot, Settings.ActiveFolder.Path, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Settings.Save();
        Instances = CreateManager(Settings.ActiveFolder.Path);
        Instances.Load();
    }

    public bool TryAddFolder(string path, string? name, out GameFolder? folder, out string error)
    {
        if (!Settings.TryAddFolder(path, name, out folder, out error)) return false;

        Settings.Save();
        return true;
    }

    public bool TryRenameFolder(string path, string name)
    {
        if (!Settings.TryRenameFolder(path, name)) return false;

        Settings.Save();
        return true;
    }

    public bool TrySetDefaultFolder(string path)
    {
        if (!Settings.TrySetDefaultFolder(path)) return false;

        Settings.Save();
        return true;
    }

    /// <summary>移除只是注销：硬盘上的文件与这个目录的实例元数据都留着，添加回来名字还在。</summary>
    public bool TryRemoveFolder(string path, out string error)
    {
        if (!Settings.TryRemoveFolder(path, out error)) return false;

        Settings.Save();
        return true;
    }

    /// <summary>"自动" resolves by timing both sources; explicit choices skip the probe.</summary>
    public async Task<IDownloadSource> ResolveSourceAsync(CancellationToken ct = default)
    {
        if (_source is not null) return _source;

        _source = Settings.DownloadSource == DownloadSource.Auto
            ? await AutoDownloadSource.ProbeAsync(NclHttp.Shared, ct).ConfigureAwait(false)
            : DownloadSources.Resolve(Settings.DownloadSource);

        return _source;
    }

    /// <summary>Drops the resolved source so a settings change takes effect immediately.</summary>
    public void InvalidateSource() => _source = null;

    /// <summary>当前那个目录的当前实例；目录自己记的 id 找不到时退回它第一个实例。</summary>
    public Instance? CurrentInstance =>
        Instances.Find(ActiveFolder.CurrentInstanceId)
        ?? (Instances.Instances.Count > 0 ? Instances.Instances[0] : null);

    public void SelectInstance(Instance? instance)
    {
        ActiveFolder.CurrentInstanceId = instance?.Id;
        Settings.Save();
    }

    /// <summary>重新扫一遍当前目录：导入或删实例之后，卡片上的模组数要跟着变。</summary>
    public void ReloadInstances() => Instances.Load();

    /// <summary>
    /// 导入一份整合包。<paramref name="targetFolderPath"/> 为空就落进正在看的目录；
    /// 普通压缩包那种会返回 <see cref="ModpackImportResult.NewGameFolder"/>，登记与切换交给调用方。
    /// </summary>
    public async Task<ModpackImportResult> ImportModpackAsync(
        string archivePath, string? targetFolderPath, string? displayName,
        IProgress<InstallProgress>? progress = null, CancellationToken ct = default)
    {
        var target = Settings.FindFolder(targetFolderPath) ?? Settings.ActiveFolder;
        var manager = string.Equals(target.Path, Instances.GameRoot, StringComparison.OrdinalIgnoreCase)
            ? Instances
            : CreateManager(target.Path);

        var source = await ResolveSourceAsync(ct).ConfigureAwait(false);
        return await new ModpackInstaller(Settings)
            .ImportAsync(manager, archivePath, displayName, source, progress, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Creates the instance folder and downloads everything it needs, so the result can be launched
    /// straight away.
    /// </summary>
    public async Task<Instance> CreateInstanceAsync(
        string versionId,
        string? displayName = null,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var instance = Instances.Create(versionId, displayName);
        instance.Isolated = Settings.VersionIsolation;

        var source = await ResolveSourceAsync(ct).ConfigureAwait(false);
        var installer = new VersionInstaller(Instances.GameRoot, concurrency: Settings.DownloadThreads);
        await installer.InstallAsync(instance.Id, versionId, source, progress, ct).ConfigureAwait(false);

        Instances.Save();
        return instance;
    }

    public async Task<GameSession> LaunchAsync(
        Instance instance,
        GameAccount account,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (IsGameRunning)
        {
            throw new LaunchException("游戏已经在运行中。");
        }

        var source = await ResolveSourceAsync(ct).ConfigureAwait(false);
        var launcher = new GameLauncher(Settings, Instances, source, Version);

        var session = await launcher.LaunchAsync(instance, account, progress, ct).ConfigureAwait(false);

        RunningSession = session;
        session.Exited += _ => RunningSession = null;
        return session;
    }

    public void SaveSettings() => Settings.Save();
}
