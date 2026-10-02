using System.Collections.Generic;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Java;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core.Launch;

/// <summary>Everything needed to turn a resolved version into a concrete command line.</summary>
public sealed class LaunchContext
{
    public required ResolvedVersion Version { get; init; }
    public required GameAccount Account { get; init; }
    public required JavaRuntime Java { get; init; }
    public required string GameDirectory { get; init; }
    public required string NativesDirectory { get; init; }
    public required string LibrariesRoot { get; init; }

    /// <summary>Absolute paths, libraries first and the client jar last.</summary>
    public required IReadOnlyList<string> Classpath { get; init; }

    public required int MaxMemoryMb { get; init; }

    /// <summary>Local log4j config, when the version ships one.</summary>
    public string? LoggingConfigPath { get; init; }
    public string? LoggingArgument { get; init; }

    public string LauncherName { get; init; } = "NegiCraftLauncher";
    public string LauncherVersion { get; init; } = "1.0.0";

    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];
    public IReadOnlyList<string> ExtraGameArguments { get; init; } = [];
}

/// <summary>A ready-to-start process description.</summary>
public sealed record LaunchPlan(
    string JavaExecutable,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments)
{
    public string CommandLine => JavaExecutable + " " + string.Join(' ', Arguments);
}
