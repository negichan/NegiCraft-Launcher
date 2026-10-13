using System;
using System.Collections.Generic;

namespace NegiCraftLauncher.Core.Modpacks;

/// <summary>压缩包到底是哪一种整合包。识别只看根目录（或往里一层）有哪些元数据文件。</summary>
public enum ModpackFormat
{
    /// <summary>Modrinth / Prism 的 <c>.mrpack</c>（<c>modrinth.index.json</c>）。</summary>
    Modrinth,

    /// <summary>CurseForge（<c>manifest.json</c>，不带 <c>addons</c>）。</summary>
    CurseForge,

    /// <summary>MCBBS（<c>mcbbs.packmeta</c>，或带 <c>addons</c> 的 <c>manifest.json</c>）。</summary>
    Mcbbs,

    /// <summary>HMCL（<c>modpack.json</c>）。</summary>
    Hmcl,

    /// <summary>MultiMC / Prism Launcher（<c>mmc-pack.json</c>）。</summary>
    MultiMc,

    /// <summary>就是一整份 <c>.minecraft</c> 的普通压缩包。</summary>
    Generic,
}

/// <summary>整合包要求的加载器。版本号缺省（CurseForge 的 <c>recommended</c>）时留空，由安装阶段现查。</summary>
public sealed record LoaderRequirement(string Kind, string? Version);

/// <summary>整合包里要落进实例目录的一个文件（模组、材质包……）。</summary>
public sealed class ModpackFile
{
    /// <summary>相对实例根目录的路径，例如 <c>mods/sodium.jar</c>。已经过路径合法性检查。</summary>
    public required string RelativePath { get; init; }

    public List<string> Urls { get; init; } = new();

    public string? Sha1 { get; init; }

    public long Size { get; init; }

    /// <summary>非必需的条目下载失败就当没有，不必让整个导入红掉。</summary>
    public bool Required { get; init; } = true;

    /// <summary>CurseForge 只给两个 id，下载地址要现查才填得进 <see cref="Urls"/>。</summary>
    public string? ProjectId { get; init; }

    public string? FileId { get; init; }
}

/// <summary>各家格式解析完之后统一成的样子：装什么版本、要不要加载器、要下哪些文件、覆盖层在哪。</summary>
public sealed class ModpackManifest
{
    public required ModpackFormat Format { get; init; }

    public required string MinecraftVersion { get; init; }

    public string Name { get; init; } = "";

    public string? VersionText { get; init; }

    public LoaderRequirement? Loader { get; init; }

    public List<ModpackFile> Files { get; } = new();

    /// <summary>
    ///     覆盖层在压缩包里的目录名，可能有几份（Modrinth 同时给 <c>overrides</c> 与
    ///     <c>client-overrides</c>，后者铺在后面盖住前者）。空串表示"就是包根"。
    /// </summary>
    public List<string> OverrideFolders { get; } = new();

    /// <summary>
    ///     普通压缩包专用的那个版本 id：包里的 <c>versions/&lt;id&gt;/&lt;id&gt;.json</c>。
    ///     这种包已经把版本元数据打包带上了，所以不需要再去版本清单里找。
    /// </summary>
    public string? BundledVersionId { get; init; }

    public string FormatLabel => Format switch
    {
        ModpackFormat.Modrinth => "Modrinth 整合包",
        ModpackFormat.CurseForge => "CurseForge 整合包",
        ModpackFormat.Mcbbs => "MCBBS 整合包",
        ModpackFormat.Hmcl => "HMCL 整合包",
        ModpackFormat.MultiMc => "MultiMC 整合包",
        _ => "普通压缩包",
    };

    /// <summary>
    /// 加载器写法各家不同（<c>fabric-loader</c> / <c>net.fabricmc.fabric-loader</c> / <c>fabric-1.20.1-0.14.x</c>），
    /// 归一成 fabric / quilt / forge / neoforge / optifine；认不出来的原样返回，安装阶段据此报错。
    /// </summary>
    public static string NormalizeLoaderKind(string? raw)
    {
        var text = (raw ?? "").ToLowerInvariant();

        if (text.Contains("neoforge") || text.Contains("neo-forge")) return "neoforge";
        if (text.Contains("fabric")) return "fabric";
        if (text.Contains("quilt")) return "quilt";
        if (text.Contains("optifine")) return "optifine";
        if (text.Contains("forge")) return "forge";
        if (text.Contains("vanilla") || text.Contains("原版")) return "vanilla";

        return text;
    }
}
