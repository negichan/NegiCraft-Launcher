using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Launch;
using NegiCraftLauncher.Core.Net;
using NegiCraftLauncher.Core.Settings;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core;

/// <summary>
/// The single entry point the UI talks to: owns settings, accounts and instances, resolves the
/// download source once per run, and drives install and launch.
/// </summary>
public sealed class Launcher
{
    private IDownloadSource? _source;

    public LauncherSettings Settings { get; }
    public AccountStore Accounts { get; }
    public InstanceManager Instances { get; }
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
        Instances = new InstanceManager(Settings.EffectiveGameRoot);
    }

    public void Initialize()
    {
        Accounts.Load();
        if (Accounts.Current is null)
        {
            Accounts.AddOffline("Steve");
        }

        Instances.Load();
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

    public Instance? CurrentInstance =>
        Instances.Find(Settings.CurrentInstanceId)
        ?? (Instances.Instances.Count > 0 ? Instances.Instances[0] : null);

    public void SelectInstance(Instance? instance)
    {
        Settings.CurrentInstanceId = instance?.Id;
        Settings.Save();
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
        await installer.InstallAsync(instance.Id, source, progress, ct).ConfigureAwait(false);

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

        instance.Isolated = Settings.VersionIsolation;
        var session = await launcher.LaunchAsync(instance, account, progress, ct).ConfigureAwait(false);

        RunningSession = session;
        session.Exited += _ => RunningSession = null;
        return session;
    }

    public void SaveSettings() => Settings.Save();
}
