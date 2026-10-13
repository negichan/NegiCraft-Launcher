using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>
/// CurseForge 的包只写 <c>projectID / fileID</c>，下载地址要现查。
///
/// <para>官方 <c>api.curseforge.com</c> 要 <c>x-api-key</c> —— 那个 key 是给网站主申请的配额，
/// 启动器不能拿用户的名字去要一个。这里走社区维护的免密钥索引 <c>api.cfwidget.com</c>：
/// 按 projectId 一次列出它所有文件的名字与大小，文件名拼上
/// <c>edge.forgecdn.net/files/&lt;id/1000&gt;/&lt;id%1000&gt;/&lt;name&gt;</c> 就是 CDN 直链
/// （CDN 那边靠 302 转发，跟普通下载没区别）。</para>
///
/// <para>一个 projectId 查一次就够，所以按项目缓存。</para>
/// </summary>
public sealed class CurseForgeFiles
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, Dictionary<string, CursedFile>> _byProject =
        new(StringComparer.Ordinal);

    public CurseForgeFiles(HttpClient? http = null) => _http = http ?? Net.NclHttp.Shared;

    private sealed record CursedFile(string Name, long Size);

    public async Task<bool> PrepareAsync(string projectId, CancellationToken ct)
    {
        if (_byProject.ContainsKey(projectId)) return true;

        using var response = await _http
            .GetAsync($"https://api.cfwidget.com/minecraft/mods/projects/{projectId}", ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;

        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);

        var files = FindFilesArray(document.RootElement);
        if (files is null) return false;

        var map = new Dictionary<string, CursedFile>(StringComparer.Ordinal);
        foreach (var file in files.Value.EnumerateArray())
        {
            var id = Text(file, "id");
            var name = Text(file, "name");
            if (id is null || string.IsNullOrEmpty(name)) continue;

            map[id] = new CursedFile(name!, file.TryGetProperty("filesize", out var size) && size.TryGetInt64(out var n) ? n : 0);
        }

        _byProject[projectId] = map;
        return map.Count > 0;
    }

    /// <summary>cfwidget 把内容包在 <c>data</c> 里，也有版本直接摊平；两种都认。</summary>
    private static JsonElement? FindFilesArray(JsonElement root)
    {
        foreach (var candidate in new[] { root, Property(root, "data") ?? default }.Where(e => e.ValueKind == JsonValueKind.Object))
        {
            if (Property(candidate, "files") is { } files && files.ValueKind == JsonValueKind.Array) return files;
        }

        return null;
    }

    private static JsonElement? Property(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : null;

    private static string? Text(JsonElement obj, string name) =>
        Property(obj, name) is not { } value
            ? null
            : value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null,
            };

    /// <summary>查不到就返回 false：必需文件由调用方报错，可选文件由调用方跳过。</summary>
    public bool TryResolve(string projectId, string fileId, out string url, out string fileName, out long size)
    {
        url = "";
        fileName = "";
        size = 0;

        if (!_byProject.TryGetValue(projectId, out var map) || !map.TryGetValue(fileId, out var entry)) return false;

        var numeric = long.TryParse(fileId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            ? id
            : -1;
        if (numeric < 0) return false;

        url = string.Format(
            CultureInfo.InvariantCulture,
            "https://edge.forgecdn.net/files/{0:D4}/{1:D3}/{2}",
            numeric / 1000,
            numeric % 1000,
            Uri.EscapeDataString(entry.Name));
        fileName = entry.Name;
        size = entry.Size;
        return true;
    }
}
