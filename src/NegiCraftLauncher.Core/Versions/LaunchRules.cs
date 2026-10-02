using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NegiCraftLauncher.Core.Versions;

/// <summary>
/// The environment a rule is evaluated against. Values mirror the Java system properties the game
/// itself reports, because that is what Mojang wrote the published rules against.
/// </summary>
public sealed record LaunchEnvironment(string OsName, string OsVersion, string Arch, bool Is64Bit)
{
    public static LaunchEnvironment Current { get; } = Detect();

    private static LaunchEnvironment Detect()
    {
        var osName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : "linux";

        // Java reports os.version as "10.0" on modern Windows, and published rules match "^10\\.".
        var osVersion = $"{Environment.OSVersion.Version.Major}.{Environment.OSVersion.Version.Minor}";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "aarch64",
            _ => "amd64",
        };

        return new LaunchEnvironment(osName, osVersion, arch, Environment.Is64BitOperatingSystem);
    }
}

/// <summary>Only the two feature flags the vanilla metadata actually tests.</summary>
public sealed record LaunchFeatures(bool IsDemoMode = false, bool HasCustomResolution = false)
{
    public static readonly LaunchFeatures Default = new();

    public bool? Lookup(string key) => key switch
    {
        "is_demo_mode" => IsDemoMode,
        "has_custom_resolution" => HasCustomResolution,
        _ => null,
    };
}

public sealed class LaunchRule
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "allow";

    [JsonPropertyName("os")]
    public OsRule? Os { get; set; }

    [JsonPropertyName("features")]
    public Dictionary<string, bool>? Features { get; set; }

    public bool Matches(LaunchEnvironment env, LaunchFeatures features)
    {
        if (Os is { } os && !os.Matches(env)) return false;

        if (Features is { Count: > 0 } wanted)
        {
            foreach (var (key, expected) in wanted)
            {
                if (features.Lookup(key) is not { } actual || actual != expected) return false;
            }
        }

        return true;
    }
}

public sealed class OsRule
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    /// <summary>Regex, not a literal — Mojang publishes patterns such as <c>^10\.</c>.</summary>
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("arch")]
    public string? Arch { get; set; }

    public bool Matches(LaunchEnvironment env)
    {
        if (!string.IsNullOrEmpty(Name) && !string.Equals(Name, env.OsName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(Arch) && !string.Equals(Arch, env.Arch, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrEmpty(Version)) return true;

        try
        {
            return Regex.IsMatch(env.OsVersion, Version);
        }
        catch (ArgumentException)
        {
            // An unparseable pattern must not silently drop a required library.
            return true;
        }
    }
}

public static class RuleEvaluator
{
    /// <summary>
    /// Absent rules mean "always include". Otherwise the last matching rule decides, starting from deny.
    /// </summary>
    public static bool Allows(IReadOnlyList<LaunchRule>? rules, LaunchEnvironment? env = null,
        LaunchFeatures? features = null)
    {
        if (rules is null || rules.Count == 0) return true;

        env ??= LaunchEnvironment.Current;
        features ??= LaunchFeatures.Default;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Matches(env, features))
            {
                allowed = !string.Equals(rule.Action, "disallow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }
}
