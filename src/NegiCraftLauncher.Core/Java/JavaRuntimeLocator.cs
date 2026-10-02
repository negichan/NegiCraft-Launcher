using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NegiCraftLauncher.Core.Java;

public sealed partial class JavaRuntime
{
    /// <summary>Absolute path to the console-less launcher used to start the game.</summary>
    public required string ExecutablePath { get; init; }

    public required int MajorVersion { get; init; }

    public required string Label { get; init; }

    public bool Is64Bit { get; init; } = true;

    public string HomeDirectory => Directory.GetParent(Directory.GetParent(ExecutablePath)!.FullName)!.FullName;

    public override string ToString() => $"Java {MajorVersion} — {Label}";
}

/// <summary>
/// Finds installed Java runtimes and picks one that can run a given Minecraft version.
/// </summary>
public static partial class JavaRuntimeLocator
{
    private static readonly string[] WindowsVendorRoots =
    [
        @"C:\Program Files\Java",
        @"C:\Program Files (x86)\Java",
        @"C:\Program Files\Eclipse Adoptium",
        @"C:\Program Files\Microsoft",
        @"C:\Program Files\Zulu",
        @"C:\Program Files\BellSoft",
        @"C:\Program Files\Amazon Corretto",
        @"C:\Program Files\JetBrains",
        @"C:\Program Files\Android\Android Studio\jbr",
    ];

    private static List<JavaRuntime>? _cache;

    public static string ManagedJavaRoot => Path.Combine(NclPaths.AppDataRoot, "java");

    public static IReadOnlyList<JavaRuntime> FindAll(bool refresh = false)
    {
        if (_cache is not null && !refresh) return _cache;

        var found = new List<JavaRuntime>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? executable, string? label = null)
        {
            if (string.IsNullOrWhiteSpace(executable)) return;
            try
            {
                var full = Path.GetFullPath(executable!);
                if (!File.Exists(full)) return;
                if (!seenPaths.Add(full)) return;

                var runtime = Probe(full, label);
                if (runtime is not null) found.Add(runtime);
            }
            catch (Exception)
            {
            }
        }

        Consider(PathFromJavaHome());
        foreach (var fromPath in FromSearchPath()) Consider(fromPath);

        foreach (var root in CandidateRoots())
        {
            if (!Directory.Exists(root)) continue;

            // Vendor layouts are either <root>\bin\javaw.exe or <root>\<build>\bin\javaw.exe.
            Consider(Path.Combine(root, "bin", "javaw.exe"), Path.GetFileName(root));
            foreach (var child in SafeDirectories(root))
            {
                var exe = Path.Combine(child, "bin", "javaw.exe");
                Consider(exe, Path.GetFileName(child));
            }
        }

        Consider(Path.Combine(ManagedJavaRoot, "bin", "javaw.exe"), "NCL 托管");
        foreach (var child in SafeDirectories(ManagedJavaRoot))
        {
            Consider(Path.Combine(child, "bin", "javaw.exe"), Path.GetFileName(child));
        }

        _cache = found
            .OrderByDescending(r => r.MajorVersion)
            .ThenBy(r => r.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return _cache;
    }

    /// <summary>
    /// Exact major version wins; a newer runtime is acceptable because Minecraft tolerates it, but an
    /// older one is never offered — running 1.12 on Java 17 fails inside the game, not in the launcher.
    /// </summary>
    public static JavaRuntime? FindSuitable(int requiredMajor, string? preferredPath = null, bool refresh = false)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var preferred = Probe(Path.GetFullPath(preferredPath!), null);
            if (preferred is not null && preferred.MajorVersion >= requiredMajor) return preferred;
        }

        var all = FindAll(refresh);
        return all.FirstOrDefault(r => r.MajorVersion == requiredMajor)
               ?? all.Where(r => r.MajorVersion > requiredMajor).OrderBy(r => r.MajorVersion).FirstOrDefault();
    }

    public static JavaRuntime? Probe(string executablePath, string? label)
    {
        if (!File.Exists(executablePath)) return null;

        var major = ReadVersionFromReleaseFile(executablePath) ?? RunVersionCommand(executablePath);
        if (major is null or <= 0) return null;

        return new JavaRuntime
        {
            ExecutablePath = executablePath,
            MajorVersion = major.Value,
            Label = string.IsNullOrWhiteSpace(label)
                ? Path.GetFileName(Directory.GetParent(Directory.GetParent(executablePath)!.FullName)!.FullName)
                : label!,
        };
    }

    /// <summary>JDK 9 and later ship a machine-readable <c>release</c> file next to bin/, avoiding a process spawn.</summary>
    private static int? ReadVersionFromReleaseFile(string executablePath)
    {
        try
        {
            var home = Directory.GetParent(Directory.GetParent(executablePath)!.FullName)!.FullName;
            var release = Path.Combine(home, "release");
            if (!File.Exists(release)) return null;

            foreach (var line in File.ReadLines(release))
            {
                var match = ReleaseVersionPattern().Match(line);
                if (match.Success && TryParseMajor(match.Groups[1].Value, out var major)) return major;
            }
        }
        catch (Exception)
        {
        }

        return null;
    }

    private static int? RunVersionCommand(string executablePath)
    {
        // javaw has no console, so the probe has to use the sibling java executable.
        var consoleExe = Path.Combine(Path.GetDirectoryName(executablePath)!,
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "java.exe" : "java");
        if (!File.Exists(consoleExe)) return null;

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = consoleExe,
                ArgumentList = { "-version" },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });

            if (process is null) return null;

            // `java -version` writes to stderr on every vendor build.
            var output = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(10_000)) return null;

            var match = VersionOutputPattern().Match(output);
            return match.Success && TryParseMajor(match.Groups[1].Value, out var major) ? major : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool TryParseMajor(string raw, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var parts = raw.Split('.', '-');
        if (parts.Length == 0) return false;

        if (parts[0] == "1" && parts.Length > 1 && int.TryParse(parts[1], out var legacy))
        {
            // 1.8.0_402 is Java 8; everything from 9 on drops the leading "1.".
            major = legacy;
            return legacy > 0;
        }

        if (int.TryParse(parts[0], out major)) return major > 0;
        return false;
    }

    private static string? PathFromJavaHome()
    {
        var home = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (string.IsNullOrWhiteSpace(home)) return null;
        return Path.Combine(home!, "bin", JavaExecutableName());
    }

    private static IEnumerable<string> FromSearchPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) yield break;

        var name = JavaExecutableName();
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = entry.Trim().Trim('"');
            if (trimmed.Length == 0) continue;

            var candidate = Path.GetFileName(trimmed).Equals(name, StringComparison.OrdinalIgnoreCase)
                ? trimmed
                : Path.Combine(trimmed, name);
            if (File.Exists(candidate)) yield return candidate;
        }
    }

    private static IEnumerable<string> CandidateRoots()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            foreach (var root in WindowsVendorRoots) yield return root;

            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(localAppData))
            {
                yield return Path.Combine(localAppData, "Programs", "Eclipse Adoptium");
                yield return Path.Combine(localAppData, "Programs", "Microsoft");
                yield return Path.Combine(localAppData, "Programs", "Zulu");
            }
        }
        else
        {
            yield return "/usr/lib/jvm";
            yield return "/Library/Java/JavaVirtualMachines";
        }
    }

    private static string JavaExecutableName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "javaw.exe" : "java";

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root) : Array.Empty<string>();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    [GeneratedRegex(@"^JAVA_VERSION=""([^""]+)""", RegexOptions.Multiline)]
    private static partial Regex ReleaseVersionPattern();

    [GeneratedRegex(@"version\s+""([^""]+)""")]
    private static partial Regex VersionOutputPattern();
}
