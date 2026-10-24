using System.IO;
using System.IO.Compression;
using System.Text.Json;

namespace NegiCraftLauncher.Core;

/// <summary>从 mod jar 里解析出的显示信息：真实的模组名与版本（没有就退回文件名）。</summary>
public sealed record ModMeta(string Name, string? Version);

/// <summary>
/// 模组 jar 的元数据读取器，按常见程度依次尝试四种清单：
/// <c>fabric.mod.json</c> → <c>quilt.mod.json</c> → <c>META-INF/mods.toml</c>（Forge 1.13+）
/// → <c>mcmod.info</c>（Forge 1.12-）。全都没有（或 jar 坏了）返回 null，调用方退回文件名。
///
/// <para>只读 zip 目录里的一个小文本条目，不解压、不校验签名；读不到任何字段都不抛 ——
/// 模组列表的兜底永远是把文件名亮出来，而不是红一行。</para>
/// </summary>
public static class ModMetaReader
{
    public static ModMeta? Read(string jarPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(jarPath);
            return Read(zip);
        }
        catch
        {
            return null;
        }
    }

    private static ModMeta? Read(ZipArchive zip)
    {
        if (ReadFabric(zip.GetEntry("fabric.mod.json")) is { } fabric) return fabric;
        if (ReadQuilt(zip.GetEntry("quilt.mod.json")) is { } quilt) return quilt;
        if (ReadModsToml(zip.GetEntry("META-INF/mods.toml")) is { } toml) return toml;
        if (ReadMcModInfo(zip.GetEntry("mcmod.info")) is { } legacy) return legacy;
        return null;
    }

    private static ModMeta? ReadFabric(ZipArchiveEntry? entry)
    {
        if (entry is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(OpenText(entry));
            var root = doc.RootElement;
            var name = String(root, "name") ?? String(root, "id");
            if (name is null) return null;
            return new ModMeta(name, String(root, "version"));
        }
        catch { return null; }
    }

    private static ModMeta? ReadQuilt(ZipArchiveEntry? entry)
    {
        if (entry is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(OpenText(entry));
            var root = doc.RootElement;
            if (!root.TryGetProperty("quiltLoader", out var loader)) return null;

            // 名字优先取 metadata.name（给人看的），没有再退 id。
            var name = loader.TryGetProperty("metadata", out var metadata) ? String(metadata, "name") : null;
            name ??= String(loader, "id");
            if (name is null) return null;
            return new ModMeta(name, String(loader, "version"));
        }
        catch { return null; }
    }

    private static ModMeta? ReadModsToml(ZipArchiveEntry? entry)
    {
        if (entry is null) return null;
        try
        {
            // 最小解析：只看第一个 [[mods]] 块里的 modId / displayName / version 三行。
            string? id = null, displayName = null, version = null;
            var inModsBlock = false;
            foreach (var raw in OpenText(entry).Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("[[mods]]", System.StringComparison.Ordinal)) { inModsBlock = true; continue; }
                if (line.StartsWith('[') && inModsBlock) break;   // 进了下一个表，[[mods]] 段结束

                if (!inModsBlock || !line.Contains('=')) continue;
                var value = TomlValue(line);
                if (value is null) continue;
                if (line.TrimStart().StartsWith("displayName", System.StringComparison.Ordinal)) displayName = value;
                else if (line.TrimStart().StartsWith("modId", System.StringComparison.Ordinal)) id = value;
                else if (line.TrimStart().StartsWith("version", System.StringComparison.Ordinal)) version = value;
            }

            var name = displayName ?? id;
            return name is null ? null : new ModMeta(name, version);
        }
        catch { return null; }
    }

    private static ModMeta? ReadMcModInfo(ZipArchiveEntry? entry)
    {
        if (entry is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(OpenText(entry));
            // 老格式是 JSON 数组，个别 mod 写成单个对象 —— 两种都认。
            var root = doc.RootElement.ValueKind == JsonValueKind.Array
                ? (doc.RootElement.EnumerateArray().FirstOrDefault() is { } first ? (JsonElement?)first : null)
                : doc.RootElement;
            if (root is not { } element) return null;

            var name = String(element, "name") ?? String(element, "modid");
            if (name is null) return null;
            return new ModMeta(name, String(element, "version"));
        }
        catch { return null; }
    }

    private static string OpenText(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>取 <c>key = "value"</c> 或 <c>key = 'value'</c> 里引号内的部分。</summary>
    private static string? TomlValue(string line)
    {
        var eq = line.IndexOf('=');
        if (eq < 0) return null;
        var rest = line[(eq + 1)..].Trim();
        if (rest.Length < 2) return null;
        var quote = rest[0];
        if (quote is not ('"' or '\'')) return null;
        var end = rest.IndexOf(quote, 1);
        return end > 1 ? rest[1..end] : null;
    }
}
