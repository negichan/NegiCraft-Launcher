using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>
/// 把各家格式的元数据读成 <see cref="ModpackManifest"/>。分派只看包里有哪些元数据文件，
/// 谁的包用哪个文件名都是公开约定：<c>modrinth.index.json</c> → Modrinth，
/// <c>manifest.json</c> 带不带 <c>addons</c> 分 MCBBS / CurseForge，<c>modpack.json</c> → HMCL，
/// <c>mmc-pack.json</c> → MultiMC，都不认但能翻出 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> → 普通压缩包。
/// </summary>
public static class ModpackParser
{
    public static ModpackManifest Parse(ModpackArchive arc, string fallbackName)
    {
        if (arc.Locate("modrinth.index.json") is not null) return ParseModrinth(arc, fallbackName);
        if (arc.Locate("mcbbs.packmeta") is not null) return ParseMcbbs(arc, "mcbbs.packmeta", fallbackName);

        if (arc.Locate("manifest.json") is not null)
        {
            var manifest = ReadJson(arc, "manifest.json");
            return TryGet(manifest, "addons", out _)
                ? ParseMcbbs(arc, "manifest.json", fallbackName)
                : ParseCurseForge(arc, manifest, fallbackName);
        }

        if (arc.Locate("modpack.json") is not null) return ParseHmcl(arc, fallbackName);
        if (arc.Locate("mmc-pack.json") is not null) return ParseMultiMc(arc, fallbackName);

        var bundled = arc.FindBundledVersionId();
        if (bundled is not null)
        {
            return new ModpackManifest
            {
                Format = ModpackFormat.Generic,
                MinecraftVersion = bundled,
                BundledVersionId = bundled,
                Name = fallbackName,
            };
        }

        throw new NotSupportedException(
            $"认不出「{fallbackName}」是整合包：包里既没有 modrinth.index.json、manifest.json、mmc-pack.json，也没有 versions/ 结构。");
    }

    private static ModpackManifest ParseModrinth(ModpackArchive arc, string fallbackName)
    {
        var root = ReadJson(arc, "modrinth.index.json");
        var dependencies = Child(root, "dependencies");

        var manifest = new ModpackManifest
        {
            Format = ModpackFormat.Modrinth,
            MinecraftVersion = Require(VersionOf(Child(dependencies, "minecraft")), "modrinth.index.json 里没有 minecraft 版本"),
            Name = Str(root, "name") ?? fallbackName,
            VersionText = Str(root, "versionId"),
            Loader = LoaderFrom(VersionOf(Child(dependencies, "neoforge")), "neoforge")
                     ?? LoaderFrom(VersionOf(Child(dependencies, "neo-forge")), "neoforge")
                     ?? LoaderFrom(VersionOf(Child(dependencies, "forge")), "forge")
                     ?? LoaderFrom(VersionOf(Child(dependencies, "fabric-loader")), "fabric")
                     ?? LoaderFrom(VersionOf(Child(dependencies, "quilt_loader")), "quilt")
                     ?? LoaderFrom(VersionOf(Child(dependencies, "optifine")), "optifine"),
        };

        foreach (var folder in new[] { "overrides", "client-overrides" })
        {
            // 字段名各家可以自定目录，取 JSON 里写的值；没写就用默认目录名。
            var name = Str(root, folder);
            if (name is not null || arc.HasFolder(folder)) manifest.OverrideFolders.Add(name ?? folder);
        }

        foreach (var file in Arr(root, "files"))
        {
            var env = ChildOfAny(file, "envs", "env");
            if (Str(env, "client") == "unsupported") continue;

            var path = Sanitize(Str(file, "path"));
            if (path is null) continue;

            var hashes = Child(file, "hashes");
            manifest.Files.Add(new ModpackFile
            {
                RelativePath = path,
                Urls = Arr(file, "downloads").Where(u => u.ValueKind == JsonValueKind.String)
                    .Select(u => u.GetString()!).ToList(),
                Sha1 = Str(hashes, "sha1"),
                Size = Num(file, "fileSize"),
                Required = Str(env, "client") != "optional",
            });
        }

        return manifest;
    }

    private static ModpackManifest ParseCurseForge(ModpackArchive arc, JsonElement root, string fallbackName)
    {
        var manifest = new ModpackManifest
        {
            Format = ModpackFormat.CurseForge,
            MinecraftVersion = Require(Str(Child(root, "minecraft"), "version"), "manifest.json 里没有 minecraft.version"),
            Name = Str(root, "name") ?? fallbackName,
            VersionText = Str(root, "version"),
            Loader = ReadCurseLoader(Child(root, "minecraft")),
        };

        var overrides = Str(root, "overrides");
        if (overrides is not null || arc.HasFolder("overrides")) manifest.OverrideFolders.Add(overrides ?? "overrides");

        foreach (var file in Arr(root, "files"))
        {
            // CurseForge 的字段名是 projectID / fileID（大写 I），别家都是小写。
            var projectId = VersionOf(ChildOfAny(file, "projectID", "projectId"));
            var fileId = VersionOf(ChildOfAny(file, "fileID", "fileId"));
            if (projectId is null || fileId is null) continue;

            var path = Sanitize(Str(file, "file"));
            manifest.Files.Add(new ModpackFile
            {
                // 没给 file 路径的条目留空，安装阶段查到文件名之后再落进 mods/。
                RelativePath = path ?? "",
                ProjectId = projectId,
                FileId = fileId,
                Required = Bool(file, "required", true),
            });
        }

        return manifest;
    }

    private static ModpackManifest ParseMcbbs(ModpackArchive arc, string metadataName, string fallbackName)
    {
        var root = ReadJson(arc, metadataName);
        var minecraft = Child(root, "minecraft");

        var manifest = new ModpackManifest
        {
            Format = ModpackFormat.Mcbbs,
            MinecraftVersion = Require(Str(minecraft, "version"), $"{metadataName} 里没有 minecraft.version"),
            Name = Str(root, "name") ?? fallbackName,
            VersionText = Str(root, "version"),
            Loader = ReadMcbbsLoader(minecraft),
        };

        var overrides = Str(root, "overrides");
        if (overrides is not null || arc.HasFolder("overrides")) manifest.OverrideFolders.Add(overrides ?? "overrides");

        foreach (var file in Arr(root, "files"))
        {
            if (Str(file, "type") == "server") continue;

            var path = Sanitize(Str(file, "path"));
            var url = Str(file, "url");
            if (path is null || string.IsNullOrEmpty(url)) continue;

            manifest.Files.Add(new ModpackFile
            {
                RelativePath = path,
                Urls = { url },
                Sha1 = Str(Child(file, "hashes"), "sha1"),
                Size = Num(file, "size"),
                Required = Bool(file, "required", true),
            });
        }

        return manifest;
    }

    private static ModpackManifest ParseHmcl(ModpackArchive arc, string fallbackName)
    {
        var root = ReadJson(arc, "modpack.json");

        var manifest = new ModpackManifest
        {
            Format = ModpackFormat.Hmcl,
            MinecraftVersion = Require(Str(root, "mcVersion"), "modpack.json 里没有 mcVersion"),
            Name = Str(root, "name") ?? fallbackName,
            VersionText = Str(root, "version"),
        };

        if (arc.HasFolder("mods")) manifest.OverrideFolders.Add("");

        foreach (var mod in Arr(root, "mods"))
        {
            var url = Str(mod, "url");
            var name = Str(mod, "name");
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(name)) continue;

            var folder = Sanitize(Str(mod, "type")) ?? "mods";
            var fileName = Sanitize($"{name}.{Str(mod, "extension") ?? "jar"}");
            if (fileName is null) continue;

            manifest.Files.Add(new ModpackFile
            {
                RelativePath = $"{folder}/{fileName}",
                Urls = { url },
                Required = Bool(mod, "forced", true),
            });
        }

        return manifest;
    }

    private static ModpackManifest ParseMultiMc(ModpackArchive arc, string fallbackName)
    {
        var root = ReadJson(arc, "mmc-pack.json");

        var game = default(string?);
        LoaderRequirement? loader = null;

        foreach (var component in Arr(root, "components"))
        {
            var uid = Str(component, "uid") ?? "";
            var version = Str(component, "version");

            if (uid.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase)) game = version;
            else if (uid.Contains("neoforged", StringComparison.OrdinalIgnoreCase)) loader = new LoaderRequirement("neoforge", version);
            else if (uid.Contains("fabric", StringComparison.OrdinalIgnoreCase)) loader = new LoaderRequirement("fabric", version);
            else if (uid.Contains("quilt", StringComparison.OrdinalIgnoreCase)) loader = new LoaderRequirement("quilt", version);
            else if (uid.Contains("forge", StringComparison.OrdinalIgnoreCase)) loader = new LoaderRequirement("forge", version);
        }

        // instance.cfg 里的 name= 才是用户看到的那个名字，mmc-pack.json 不带显示名。
        var instanceName = ReadInstanceCfgName(arc);

        return new ModpackManifest
        {
            Format = ModpackFormat.MultiMc,
            MinecraftVersion = Require(game, "mmc-pack.json 里没有 net.minecraft 版本"),
            Name = instanceName ?? Str(root, "name") ?? fallbackName,
            Loader = loader,
        };
    }

    private static string? ReadInstanceCfgName(ModpackArchive arc)
    {
        var text = arc.ReadText("instance.cfg");
        if (text is null) return null;

        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) continue;

            var value = trimmed[5..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static LoaderRequirement? ReadCurseLoader(JsonElement minecraft)
    {
        var loaders = Arr(minecraft, "modLoaders").ToList();
        if (loaders.Count == 0) return null;

        var chosen = loaders.FirstOrDefault(l => Bool(l, "primary", false));
        if (chosen.ValueKind != JsonValueKind.Object) chosen = loaders[0];
        var id = Str(chosen, "id") ?? "";

        var kind = ModpackManifest.NormalizeLoaderKind(id.Split('-')[0]);
        if (kind is "vanilla" or "") return null;

        // id 形如 forge-1.19.2-43.2.0（Forge 把游戏版本也写进去了）或 neoforge-20.2.67、fabric-0.14.21。
        var rest = id[(id.IndexOf('-') + 1)..];
        var forgeStyle = rest.Split('-');
        var version = forgeStyle.Length > 2 ? string.Join('-', forgeStyle[1..]) : rest;

        // "recommended" 是个占位而不是版本，安装阶段按游戏版本现查最新。
        return version.Contains("recommended", StringComparison.OrdinalIgnoreCase)
            ? new LoaderRequirement(kind, null)
            : new LoaderRequirement(kind, version);
    }

    private static LoaderRequirement? ReadMcbbsLoader(JsonElement minecraft)
    {
        foreach (var info in Arr(minecraft, "modInfos"))
        {
            var kind = ModpackManifest.NormalizeLoaderKind(Str(info, "type") ?? Str(info, "name"));
            if (kind is "" or "vanilla") continue;

            return new LoaderRequirement(kind, Str(info, "version"));
        }

        return null;
    }

    private static JsonElement ReadJson(ModpackArchive arc, string name)
    {
        var text = arc.ReadText(name);
        if (text is null) throw new InvalidDataException($"整合包里找不到 {name}");

        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{name} 不是合法的 JSON：{ex.Message}", ex);
        }
    }

    private static string Require(string? value, string message) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException(message) : value.Trim();

    internal static bool TryGet(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        // 先按原样试：绝大多数键就是标准拼写，省下一次遍历。
        if (obj.TryGetProperty(name, out value)) return true;

        foreach (var property in obj.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;

            value = property.Value;
            return true;
        }

        value = default;
        return false;
    }

    private static JsonElement Child(JsonElement obj, string name) =>
        TryGet(obj, name, out var value) && value.ValueKind != JsonValueKind.Null ? value : default;

    private static JsonElement ChildOfAny(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGet(obj, name, out var value) && value.ValueKind != JsonValueKind.Null) return value;
        }

        return default;
    }

    private static IEnumerable<JsonElement> Arr(JsonElement obj, string name) =>
        TryGet(obj, name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
            : Enumerable.Empty<JsonElement>();

    private static string? Str(JsonElement obj, string name)
    {
        if (!TryGet(obj, name, out var value)) return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static long Num(JsonElement obj, string name) =>
        TryGet(obj, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n)
            ? n
            : 0;

    private static bool Bool(JsonElement obj, string name, bool fallback)
    {
        if (!TryGet(obj, name, out var value)) return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static LoaderRequirement? LoaderFrom(string? version, string kind) =>
        string.IsNullOrWhiteSpace(version) ? null : new LoaderRequirement(kind, version.Trim());

    /// <summary>JSON 里的数字版本号（1.21 会被读成 number）统一成字符串。</summary>
    private static string? VersionOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        _ => null,
    };

    /// <summary>
    /// 包里的文件路径必须落在实例目录内：挡掉 <c>..</c>、绝对路径与盘符，非法的条目直接丢。
    /// 分隔符统一成 <c>/</c>，后面拼路径再按平台换。
    /// </summary>
    private static string? Sanitize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var path = raw!.Replace('\\', '/').TrimStart('/');
        return ModpackArchive.IsSafeRelativePath(path) ? path : null;
    }
}
