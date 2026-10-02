using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Java;
using NegiCraftLauncher.Core.Net;
using NegiCraftLauncher.Core.Settings;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core.Launch;

/// <summary>
/// Turns an instance plus an account into a running game process: completes the files, picks a Java
/// runtime, extracts natives, builds the command line and starts the JVM.
/// </summary>
public sealed class GameLauncher
{
    /// <summary>Install work occupies most of the bar; the local steps at the end are near-instant.</summary>
    private const double InstallShare = 0.94;

    private readonly LauncherSettings _settings;
    private readonly InstanceManager _instances;
    private readonly VersionInstaller _installer;
    private readonly IDownloadSource _source;
    private readonly string _launcherVersion;

    public GameLauncher(LauncherSettings settings, InstanceManager instances, IDownloadSource source,
        string launcherVersion = "1.0.0")
    {
        _settings = settings;
        _instances = instances;
        _source = source;
        _launcherVersion = launcherVersion;
        _installer = new VersionInstaller(instances.GameRoot, concurrency: settings.DownloadThreads);
    }

    public VersionInstaller Installer => _installer;

    public async Task<GameSession> LaunchAsync(
        Instance instance,
        GameAccount account,
        IProgress<InstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var scaled = progress is null
            ? null
            : new CallbackProgress<InstallProgress>(p =>
                progress.Report(new InstallProgress(p.Stage, p.Fraction * InstallShare, p.Detail)));

        var resolved = await _installer.InstallAsync(instance.Id, _source, scaled, ct).ConfigureAwait(false);

        progress?.Report(new InstallProgress("检查 Java", InstallShare));
        var java = JavaRuntimeLocator.FindSuitable(
            resolved.RequiredJavaMajor,
            instance.JavaPath ?? _settings.JavaPath);

        if (java is null)
        {
            throw new LaunchException(
                $"没有找到 Java {resolved.RequiredJavaMajor} 或更新的运行时。请在「设置 → Java」中选择，或安装 Java {resolved.RequiredJavaMajor} 后重试。");
        }

        var gameDirectory = _instances.GameDirectoryFor(instance);
        NclPaths.EnsureDirectory(gameDirectory);

        progress?.Report(new InstallProgress("准备原生库", InstallShare + 0.03));
        var nativesDirectory = NclPaths.NativesDirectory(_instances.GameRoot, instance.Id);
        var resolution = LibraryResolver.Resolve(resolved.Profile.Libraries, resolved.LibrariesRoot, _source);
        NativeLibraryExtractor.Extract(resolution.Natives, nativesDirectory);

        var classpath = BuildClasspath(resolved, resolution);

        var loggingConfig = ResolveLoggingConfig(resolved);
        var plan = ArgumentBuilder.Build(new LaunchContext
        {
            Version = resolved,
            Account = account,
            Java = java,
            GameDirectory = gameDirectory,
            NativesDirectory = nativesDirectory,
            LibrariesRoot = resolved.LibrariesRoot,
            Classpath = classpath,
            MaxMemoryMb = instance.MaxMemoryMb ?? _settings.MaxMemoryMb,
            LoggingConfigPath = loggingConfig?.Path,
            LoggingArgument = loggingConfig?.Argument,
            LauncherVersion = _launcherVersion,
        });

        progress?.Report(new InstallProgress("启动进程", 1));
        var session = Start(plan, instance);
        _instances.Touch(instance);
        return session;
    }

    private IReadOnlyList<string> BuildClasspath(ResolvedVersion resolved, LibraryResolution resolution)
    {
        var missing = resolution.Classpath.Where(l => !File.Exists(l.LocalPath)).ToList();
        if (missing.Count > 0)
        {
            throw new LaunchException(
                $"有 {missing.Count} 个库文件缺失（例如 {Path.GetFileName(missing[0].RelativePath)}），请重新下载该实例。");
        }

        if (!File.Exists(resolved.ClientJarPath))
        {
            throw new LaunchException($"找不到游戏核心 {Path.GetFileName(resolved.ClientJarPath)}，请重新下载该实例。");
        }

        var classpath = new List<string>(resolution.Classpath.Count + 1);
        classpath.AddRange(resolution.Classpath.Select(l => l.LocalPath));
        classpath.Add(resolved.ClientJarPath);
        return classpath;
    }

    private static (string Path, string Argument)? ResolveLoggingConfig(ResolvedVersion resolved)
    {
        if (resolved.Profile.Logging is not { } logging || !logging.TryGetValue("client", out var entry))
        {
            return null;
        }

        if (string.IsNullOrEmpty(entry.Argument) || string.IsNullOrEmpty(entry.File.Id)) return null;

        var path = Path.Combine(resolved.AssetsRoot, entry.File.Id);
        return File.Exists(path) ? (path, entry.Argument) : null;
    }

    private static GameSession Start(LaunchPlan plan, Instance instance)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = plan.JavaExecutable,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        // ArgumentList rather than a pre-joined string: game directories routinely contain spaces and
        // non-ASCII characters, and hand-rolled quoting gets both wrong.
        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo };

        try
        {
            if (!process.Start())
            {
                throw new LaunchException("无法启动 Java 进程。");
            }
        }
        catch (LaunchException)
        {
            process.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            process.Dispose();
            throw new LaunchException($"启动 Java 失败：{ex.Message}", ex);
        }

        var logFile = Path.Combine(NclPaths.LogDirectory, "game",
            $"{instance.Id}_{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var session = new GameSession(process, instance.Id, logFile);
        session.BeginReading();
        return session;
    }
}
