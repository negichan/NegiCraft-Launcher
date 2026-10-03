using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;

namespace NegiCraftLauncher.Raster;

/// <summary>一张皮肤：PNG 原始字节 + 是否为细手臂模型。</summary>
public sealed record SkinData(byte[] Bytes, bool IsSlim);

/// <summary>
/// 皮肤获取：内存缓存 → 磁盘缓存（<c>%APPDATA%\NCL\cache\skins</c>）→ 内置资源 → 在线拉取。
///
/// <para>这是从 Avalonia 版 <c>Skin/Services/SkinService.cs</c> 剥出来的平台中立部分：
/// 唯一改动是把 <c>AssetLoader</c>（Avalonia 资源系统）换成程序集内嵌资源，
/// 把 <c>SKBitmap</c> 换成 <see cref="SkinTexture"/>，逻辑与缓存策略逐行保持一致。</para>
/// </summary>
public static class SkinRepository
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, SkinData> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Minotar 在查不到玩家时会返回一个 2425 字节的默认 Steve，必须当成失败。</summary>
    private const int MinotarDummyLength = 2425;

    public static string CacheDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCL", "cache", "skins");

    public static async Task<SkinData?> GetOrFetchAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        if (MemoryCache.TryGetValue(username, out var cached))
        {
            return cached;
        }

        var diskPath = Path.Combine(CacheDirectory, $"{username}.png");
        var modelPath = Path.Combine(CacheDirectory, $"{username}.model");

        // 1. 磁盘缓存
        try
        {
            if (File.Exists(diskPath))
            {
                var bytes = await File.ReadAllBytesAsync(diskPath);
                if (bytes.Length > 0 && bytes.Length != MinotarDummyLength)
                {
                    bool isSlim;
                    if (File.Exists(modelPath))
                    {
                        var modelText = (await File.ReadAllTextAsync(modelPath)).Trim();
                        isSlim = string.Equals(modelText, "slim", StringComparison.OrdinalIgnoreCase);
                    }
                    else
                    {
                        isSlim = InferIsSlim(bytes, username);
                        try
                        {
                            await File.WriteAllTextAsync(modelPath, isSlim ? "slim" : "classic");
                        }
                        catch
                        {
                            // 缓存写不进去不影响这次使用。
                        }
                    }

                    var skinData = new SkinData(bytes, isSlim);
                    MemoryCache[username] = skinData;
                    return skinData;
                }

                if (bytes.Length == MinotarDummyLength)
                {
                    File.Delete(diskPath);
                    if (File.Exists(modelPath)) File.Delete(modelPath);
                }
            }
        }
        catch
        {
            // 缓存目录不可读就当作没有。
        }

        // 2. 内置资源（skin_miku_mew.png 之类）
        try
        {
            var bundled = DefaultSkins.TryRead(username.ToLowerInvariant());
            if (bundled is not null && bundled.Length > 0)
            {
                var isSlim = InferIsSlim(bundled, username);
                var skinData = new SkinData(bundled, isSlim);
                MemoryCache[username] = skinData;
                await WriteCacheAsync(diskPath, modelPath, bundled, isSlim);
                return skinData;
            }
        }
        catch
        {
            // 内置资源缺失不是错误。
        }

        // 3. 在线：官方 → mineskin → minotar
        var online = await FetchFromMojangAsync(username)
                  ?? await FetchFromMineskinAsync(username)
                  ?? await FetchFromMinotarAsync(username);

        if (online is not null && online.Bytes.Length > 0 && online.Bytes.Length != MinotarDummyLength)
        {
            MemoryCache[username] = online;
            await WriteCacheAsync(diskPath, modelPath, online.Bytes, online.IsSlim);
            return online;
        }

        return null;
    }

    private static async Task WriteCacheAsync(string diskPath, string modelPath, byte[] bytes, bool isSlim)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            await File.WriteAllBytesAsync(diskPath, bytes);
            await File.WriteAllTextAsync(modelPath, isSlim ? "slim" : "classic");
        }
        catch
        {
            // 落盘失败不影响本次渲染。
        }
    }

    private static bool InferIsSlim(byte[] bytes, string username)
    {
        var texture = SkinTexture.Decode(bytes);
        if (texture is not null && texture.Width >= 64 && texture.Height >= 64 && texture.Width == texture.Height)
        {
            return texture.IsSlimSkin();
        }

        return IsSlimForPlayerName(username);
    }

    private static async Task<SkinData?> FetchFromMojangAsync(string username)
    {
        try
        {
            var profileJson = await Http.GetStringAsync($"https://api.mojang.com/users/profiles/minecraft/{username}");
            using var profileDoc = JsonDocument.Parse(profileJson);
            if (!profileDoc.RootElement.TryGetProperty("id", out var idElem)) return null;
            var uuid = idElem.GetString() ?? "";
            if (string.IsNullOrEmpty(uuid)) return null;

            var sessionJson = await Http.GetStringAsync($"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}");
            using var sessionDoc = JsonDocument.Parse(sessionJson);
            if (!sessionDoc.RootElement.TryGetProperty("properties", out var props)) return null;

            foreach (var prop in props.EnumerateArray())
            {
                if (prop.TryGetProperty("name", out var nameElem) && nameElem.GetString() == "textures"
                    && prop.TryGetProperty("value", out var valElem))
                {
                    var base64 = valElem.GetString() ?? "";
                    var decoded = Convert.FromBase64String(base64);
                    using var texDoc = JsonDocument.Parse(decoded);
                    if (texDoc.RootElement.TryGetProperty("textures", out var textures)
                        && textures.TryGetProperty("SKIN", out var skin)
                        && skin.TryGetProperty("url", out var urlElem))
                    {
                        var isSlim = false;
                        if (skin.TryGetProperty("metadata", out var metaElem)
                            && metaElem.TryGetProperty("model", out var modelElem)
                            && string.Equals(modelElem.GetString(), "slim", StringComparison.OrdinalIgnoreCase))
                        {
                            isSlim = true;
                        }

                        var skinUrl = urlElem.GetString() ?? "";
                        if (!string.IsNullOrEmpty(skinUrl))
                        {
                            var bytes = await Http.GetByteArrayAsync(skinUrl.Replace("http://", "https://"));
                            if (bytes.Length > 0 && bytes.Length != MinotarDummyLength)
                            {
                                return new SkinData(bytes, isSlim);
                            }
                        }
                    }
                }
            }
        }
        catch
        {
            // 网络失败就交给下一个来源。
        }

        return null;
    }

    private static async Task<SkinData?> FetchFromMineskinAsync(string username)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://mineskin.eu/skin/{username}");
            if (bytes.Length > 0 && bytes.Length != MinotarDummyLength)
            {
                return new SkinData(bytes, InferIsSlim(bytes, username));
            }
        }
        catch
        {
        }

        return null;
    }

    private static async Task<SkinData?> FetchFromMinotarAsync(string username)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://minotar.net/skin/{username}");
            if (bytes.Length > 0 && bytes.Length != MinotarDummyLength)
            {
                return new SkinData(bytes, InferIsSlim(bytes, username));
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// 离线玩家没有 profile 声明模型，所以用 PCL 的 <c>McSkinSex(McLoginLegacyUuid(name))</c>
    /// 算法挑默认皮肤（Alex = 细手臂，Steve = 宽手臂）。
    /// </summary>
    public static bool IsSlimForPlayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;

        if (MemoryCache.TryGetValue(name, out var cached))
        {
            return cached.IsSlim;
        }

        var modelPath = Path.Combine(CacheDirectory, $"{name}.model");
        if (File.Exists(modelPath))
        {
            try
            {
                var txt = File.ReadAllText(modelPath).Trim();
                if (string.Equals(txt, "slim", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(txt, "classic", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch
            {
            }
        }

        if (name.Contains("alex", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("steve", StringComparison.OrdinalIgnoreCase)) return false;

        return McSkinSex(McLoginLegacyUuid(name)) == "Alex";
    }

    /// <summary>与 PCL（MeloongCore.StringExtensions.GetStableHashCode）逐位对齐的稳定哈希。</summary>
    public static ulong GetStableHashCode(string str)
    {
        var result = 5381UL;
        foreach (var v in str) result = (result << 5) ^ result ^ v;
        return result ^ 0xA98F501BC684032FUL;
    }

    private static string EnsureLength(string? str, char code, int length)
    {
        str ??= "";
        return str.Length > length ? str[..length] : str.PadLeft(length, code);
    }

    /// <summary>PCL 的离线玩家 UUID 生成（ModLaunch.McLoginLegacyUuid）。</summary>
    public static string McLoginLegacyUuid(string name)
    {
        var part1 = EnsureLength(name.Length.ToString("X"), '0', 16);
        var part2 = EnsureLength(GetStableHashCode(name).ToString("X"), '0', 16);
        var fullUuid = part1 + part2;
        return fullUuid[..12] + "3" + fullUuid.Substring(13, 3) + "9" + fullUuid[17..];
    }

    /// <summary>PCL 的离线皮肤性别判定（ModMinecraft.McSkinSex）。返回 "Alex" 或 "Steve"。</summary>
    public static string McSkinSex(string uuid)
    {
        if (uuid.Length != 32) return "Steve";
        var a = int.Parse(uuid[7].ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
        var b = int.Parse(uuid[15].ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
        var c = int.Parse(uuid[23].ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
        var d = int.Parse(uuid[31].ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
        return (a ^ b ^ c ^ d) % 2 != 0 ? "Alex" : "Steve";
    }
}
