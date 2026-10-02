using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace NegiCraftLauncher.Core.Launch;

/// <summary>
/// Unpacks the old-style native jars (those declaring a <c>natives</c> map) into a per-instance
/// directory. Modern LWJGL 3 natives ship as plain classpath jars and are extracted by the game.
/// </summary>
public static class NativeLibraryExtractor
{
    public static void Extract(IReadOnlyList<Versions.ResolvedLibrary> natives, string targetDirectory)
    {
        NclPaths.EnsureDirectory(targetDirectory);

        // A previous launch may still hold these files open; failing to clear them is not fatal
        // because every entry below is written with overwrite semantics.
        TryClear(targetDirectory);

        foreach (var library in natives)
        {
            if (!File.Exists(library.LocalPath)) continue;
            ExtractArchive(library.LocalPath, targetDirectory, library.ExtractExcludes);
        }
    }

    private static void ExtractArchive(string archivePath, string targetDirectory,
        IReadOnlyList<string> excludes)
    {
        var root = Path.GetFullPath(targetDirectory);

        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            var relative = entry.FullName.Replace('\\', '/');
            if (excludes.Any(prefix => relative.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;

            var destination = Path.GetFullPath(Path.Combine(root, relative));

            // A jar is downloaded content; without this an entry like ../../foo.dll would escape.
            if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static void TryClear(string directory)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }
        catch (Exception)
        {
        }
    }
}
