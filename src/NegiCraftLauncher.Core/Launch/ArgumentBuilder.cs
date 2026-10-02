using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NegiCraftLauncher.Core.Versions;

namespace NegiCraftLauncher.Core.Launch;

public static partial class ArgumentBuilder
{
    /// <summary>1.18.1 shipped the Log4Shell fix; anything older still resolves lookups in log messages.</summary>
    private static readonly DateTimeOffset Log4ShellFixed = new(2021, 12, 18, 0, 0, 0, TimeSpan.Zero);

    public static LaunchPlan Build(LaunchContext context)
    {
        var mainClass = context.Version.MainClass;
        if (string.IsNullOrWhiteSpace(mainClass))
        {
            throw new InvalidOperationException($"版本 {context.Version.InstanceId} 的元数据没有提供主类");
        }

        var arguments = new List<string>();
        arguments.AddRange(BuildJvmArguments(context));
        // The main class separates the two halves: everything before it is read by the JVM, and
        // omitting it makes the JVM treat the game's own flags as unrecognised options.
        arguments.Add(mainClass);
        arguments.AddRange(BuildGameArguments(context));

        return new LaunchPlan(context.Java.ExecutablePath, NormaliseDirectory(context.GameDirectory), arguments);
    }

    public static IReadOnlyList<string> BuildJvmArguments(LaunchContext context)
    {
        var result = new List<string>();

        result.Add($"-Xmx{context.MaxMemoryMb}M");
        result.Add($"-Xms{Math.Min(1024, context.MaxMemoryMb)}M");

        // Only affects the console streams we capture, never how the game reads its own files.
        if (context.Java.MajorVersion >= 19)
        {
            result.Add("-Dstdout.encoding=UTF-8");
            result.Add("-Dstderr.encoding=UTF-8");
        }
        else
        {
            result.Add("-Dsun.stdout.encoding=UTF-8");
            result.Add("-Dsun.stderr.encoding=UTF-8");
        }

        var released = context.Version.Profile.ReleaseTime;
        if (released is null || released < Log4ShellFixed)
        {
            result.Add("-Dlog4j2.formatMsgNoLookups=true");
        }

        if (!string.IsNullOrEmpty(context.LoggingConfigPath) && !string.IsNullOrEmpty(context.LoggingArgument))
        {
            result.Add(Substitute(context.LoggingArgument!, Placeholders(context)));
        }

        result.AddRange(context.ExtraJvmArguments);

        var profile = context.Version.Profile;
        if (profile.Arguments is { Jvm.Count: > 0 } args)
        {
            result.AddRange(Expand(args.Jvm, context));
        }
        else
        {
            // Pre-1.13 profiles carry no JVM arguments at all, so the launcher supplies the minimum.
            result.Add("-Djava.library.path=" + context.NativesDirectory);
            result.Add("-Dminecraft.launcher.brand=" + context.LauncherName);
            result.Add("-Dminecraft.launcher.version=" + context.LauncherVersion);
            result.Add("-cp");
            result.Add(string.Join(Path.PathSeparator, context.Classpath));
        }

        return result;
    }

    public static IReadOnlyList<string> BuildGameArguments(LaunchContext context)
    {
        var profile = context.Version.Profile;
        var result = new List<string>();

        if (profile.Arguments is { Game.Count: > 0 } args)
        {
            result.AddRange(Expand(args.Game, context));
        }
        else if (!string.IsNullOrWhiteSpace(profile.MinecraftArguments))
        {
            // Split the template before substituting: a substituted game directory may contain spaces.
            foreach (var token in profile.MinecraftArguments!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                result.Add(Substitute(token, Placeholders(context)));
            }
        }
        else
        {
            throw new InvalidOperationException($"版本 {context.Version.InstanceId} 既没有 arguments 也没有 minecraftArguments");
        }

        result.AddRange(context.ExtraGameArguments.Select(a => Substitute(a, Placeholders(context))));
        return result;
    }

    private static IEnumerable<string> Expand(IEnumerable<ArgumentValue> values, LaunchContext context)
    {
        var map = Placeholders(context);
        foreach (var entry in values)
        {
            if (!RuleEvaluator.Allows(entry.Rules)) continue;
            foreach (var raw in entry.Enumerate())
            {
                yield return Substitute(raw, map);
            }
        }
    }

    public static IReadOnlyDictionary<string, string> Placeholders(LaunchContext context)
    {
        var version = context.Version;
        var profile = version.Profile;
        var account = context.Account;

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = account.Name,
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = account.AccessToken,
            ["auth_session"] = account.AccessToken,
            ["user_properties"] = "{}",
            // Offline identities are still reported as msa: modern clients branch on this only to
            // choose a services backend, which offline play never reaches.
            ["user_type"] = "msa",
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["version_name"] = version.InstanceId,
            ["version_type"] = version.InstanceId,
            ["game_directory"] = NormaliseDirectory(context.GameDirectory),
            ["assets_root"] = NormaliseDirectory(version.AssetsRoot),
            ["assets_index_name"] = version.AssetIndexId,
            ["natives_directory"] = NormaliseDirectory(context.NativesDirectory),
            ["launcher_name"] = context.LauncherName,
            ["launcher_version"] = context.LauncherVersion,
            ["classpath"] = string.Join(Path.PathSeparator, context.Classpath),
            ["classpath_sep"] = Path.PathSeparator.ToString(),
            ["library_directory"] = NormaliseDirectory(context.LibrariesRoot),
            ["minecraft_library_directory"] = NormaliseDirectory(context.LibrariesRoot),
            ["primary_jar"] = version.ClientJarPath,
            ["path"] = context.LoggingConfigPath ?? "",
            ["profile_name"] = version.InstanceId,
        };
    }

    /// <summary>
    /// A trailing separator would be swallowed by the quote the process launcher adds around a path
    /// containing spaces, leaving the game with a truncated directory.
    /// </summary>
    private static string NormaliseDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string Substitute(string input, IReadOnlyDictionary<string, string> map)
    {
        if (input.IndexOf("${", StringComparison.Ordinal) < 0) return input;
        return PlaceholderPattern().Replace(input, match =>
            map.TryGetValue(match.Groups[1].Value, out var value) ? value : "");
    }

    [GeneratedRegex(@"\$\{([a-zA-Z0-9_]+)\}")]
    private static partial Regex PlaceholderPattern();
}
