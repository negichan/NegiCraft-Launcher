using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>
/// 整合包压缩包的读取层。<b>只认 zip</b> —— <c>.zip</c> 与 <c>.mrpack</c> 都是 zip 容器，
/// rar / 7z 要额外的原生解压器，带不进启动器，遇到就明确报错而不是闷头失败。
///
/// <para>导出工具几乎总要再包一层目录（<c>MyPack/manifest.json</c>），所以元数据文件在根目录
/// 往里最多找一层，找到就把那一层定成"包根"（<see cref="Base"/>），之后所有路径都相对它。</para>
/// </summary>
public sealed class ModpackArchive : IDisposable
{
    private readonly ZipArchive _archive;
    private readonly Dictionary<string, ZipArchiveEntry> _byName;

    private ModpackArchive(ZipArchive archive)
    {
        _archive = archive;
        _byName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            _byName[Normalise(entry.FullName)] = entry;
        }
    }

    /// <summary>包根在压缩包里的前缀：<c>""</c> 或 <c>"MyPack/"</c>（总是带尾斜杠）。</summary>
    public string Base { get; private set; } = "";

    public static ModpackArchive Open(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is ".rar" or ".7z" or ".tar" or ".gz")
        {
            throw new NotSupportedException($"{extension} 这种压缩格式启动器解不了，请用 zip 或 mrpack 重新打包。");
        }

        var archive = ZipFile.OpenRead(path);
        try
        {
            return new ModpackArchive(archive);
        }
        catch (Exception)
        {
            archive.Dispose();
            throw;
        }
    }

    private static string Normalise(string fullName) => fullName.Replace('\\', '/');

    private string Full(string relative) => Base + relative.TrimStart('/', '\\');

    public ZipArchiveEntry? Entry(string relative) =>
        _byName.GetValueOrDefault(Normalise(Full(relative)));

    public bool Exists(string relative) => Entry(relative) is not null;

    public string? ReadText(string relative)
    {
        var entry = Entry(relative);
        if (entry is null) return null;

        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 找元数据文件：先在包根，再往里翻一层目录。找到就把包根推进到它所在那一层。
    /// 返回传进来的 <paramref name="fileName"/>（相对包根），没找到返回 null。
    /// </summary>
    public string? Locate(string fileName)
    {
        if (_byName.ContainsKey(Full(fileName))) return fileName;

        foreach (var name in _byName.Keys)
        {
            if (!name.EndsWith("/" + fileName, StringComparison.OrdinalIgnoreCase)) continue;
            if (name[..^fileName.Length].Count(c => c == '/') != 1) continue;

            Base = name[..name.LastIndexOf('/')];
            return fileName;
        }

        return null;
    }

    public bool HasFolder(string relativeFolder)
    {
        var prefix = FolderPrefix(relativeFolder);
        return _byName.Keys.Any(name => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>包根下 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> 里的 <c>&lt;id&gt;</c>：普通压缩包（一整份 .minecraft）靠它认。</summary>
    public string? FindBundledVersionId()
    {
        foreach (var name in _byName.Keys)
        {
            if (!name.StartsWith(Base, StringComparison.OrdinalIgnoreCase)) continue;

            var segments = name[Base.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length != 3) continue;
            if (!segments[0].Equals("versions", StringComparison.OrdinalIgnoreCase)) continue;
            if (!segments[2].Equals(segments[1] + ".json", StringComparison.OrdinalIgnoreCase)) continue;

            return segments[1];
        }

        return null;
    }

    /// <summary>包里揣着一份整合包（"带启动器的整合包"：<c>modpack.zip</c> / <c>modpack.mrpack</c>）时解出来，返回它的落盘路径。</summary>
    public string? ExtractNestedPack(string destinationDirectory)
    {
        foreach (var name in new[] { "modpack.mrpack", "modpack.zip" })
        {
            if (Locate(name) is null) continue;

            var entry = Entry(name)!;
            Directory.CreateDirectory(destinationDirectory);
            var target = Path.Combine(destinationDirectory, name);
            entry.ExtractToFile(target, overwrite: true);
            return target;
        }

        return null;
    }

    /// <summary>
    /// 把包根下某个目录整个铺到目标目录（覆盖层）。CurseForge 老包会把覆盖层直接摊在包根，
    /// 所以 <c>"."</c> / <c>"./"</c> / 空串都表示"就是包根"。返回铺了多少个文件。
    /// </summary>
    public int ExtractFolder(string relativeFolder, string targetDirectory)
    {
        var prefix = FolderPrefix(relativeFolder);
        var written = 0;

        foreach (var (name, entry) in _byName)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = name[prefix.Length..];
            if (relative.Length == 0 || relative.EndsWith('/') || !IsSafeRelativePath(relative)) continue;

            Write(entry, targetDirectory, relative, ref written);
        }

        return written;
    }

    /// <summary>整个包根摊到一个目录（普通压缩包那条路）。</summary>
    public int ExtractEverything(string targetDirectory) => ExtractFolder("", targetDirectory);

    private static void Write(ZipArchiveEntry entry, string targetDirectory, string relative, ref int written)
    {
        var target = Path.Combine(targetDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        entry.ExtractToFile(target, overwrite: true);
        written++;
    }

    private string FolderPrefix(string relativeFolder)
    {
        var trimmed = relativeFolder.Replace('\\', '/').Trim().TrimStart('.').Trim('/');
        return trimmed.Length == 0 ? Base : Base + trimmed + "/";
    }

    /// <summary>
    ///     压缩包里的路径是导出工具（或者一个恶意包）给的，必须挡住 <c>..</c> 与绝对路径 ——
    /// 否则 <c>overrides/../../startup.bat</c> 就能把文件写到启动器目录外面。
    /// </summary>
    public static bool IsSafeRelativePath(string relative) =>
        relative.Length > 0 &&
        !relative.Contains("..", StringComparison.Ordinal) &&
        !relative.Contains(':') &&
        !relative.StartsWith('/') &&
        !relative.StartsWith('\\') &&
        !Path.IsPathRooted(relative);

    public void Dispose() => _archive.Dispose();
}
