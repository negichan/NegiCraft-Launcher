using System;
using System.IO;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>
/// 一次导入要用的压缩包。<b>"带启动器的整合包"那种外面还套一层、里面才是 modpack.zip /
/// modpack.mrpack 的包</b>，在这里往下钻一层，之后解析与铺覆盖层都看着真正的那份包，
/// 调用方不用管钻了几层。
/// </summary>
public sealed class ModpackSource : IDisposable
{
    private const int MaxNesting = 2;

    private ModpackSource(ModpackArchive archive, string filePath, string? tempDirectory)
    {
        Archive = archive;
        FilePath = filePath;
        TempDirectory = tempDirectory;
    }

    public ModpackArchive Archive { get; }

    /// <summary>真正那份包的落盘路径（钻层之后可能已经是临时目录里的那一个）。</summary>
    public string FilePath { get; }

    /// <summary>钻层时解出来的临时目录；Dispose 时清掉。</summary>
    public string? TempDirectory { get; }

    public static ModpackSource Open(string filePath) => Open(filePath, 0);

    private static ModpackSource Open(string filePath, int depth)
    {
        var archive = ModpackArchive.Open(filePath);

        if (depth >= MaxNesting) return new ModpackSource(archive, filePath, null);

        var temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NCL", "modpack", Guid.NewGuid().ToString("N"));
        string? inner;
        try
        {
            inner = archive.ExtractNestedPack(temp);
        }
        catch (Exception)
        {
            inner = null;
        }

        if (inner is null)
        {
            TryDelete(temp);
            return new ModpackSource(archive, filePath, null);
        }

        archive.Dispose();
        return Open(inner, depth + 1);
    }

    public ModpackManifest ReadManifest() =>
        ModpackParser.Parse(Archive, System.IO.Path.GetFileNameWithoutExtension(FilePath));

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // 临时目录删不掉不影响正确性，导入不该因此红一次。
        }
    }

    public void Dispose()
    {
        Archive.Dispose();
        if (TempDirectory is { } temp) TryDelete(temp);
    }
}
