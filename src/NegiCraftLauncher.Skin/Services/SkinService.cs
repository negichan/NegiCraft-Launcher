using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Platform;
using SkiaSharp;

namespace NegiCraftLauncher.Skin.Services;

public static class SkinService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly ConcurrentDictionary<string, SkinData> MemorySkinCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets skin data (bytes and model type) for the username, using memory cache, disk cache (%APPDATA%\NCL\cache\skins),
    /// or downloading from Mojang/mineskin/minotar. Returns null if offline / not found.
    /// </summary>
    public static async Task<SkinData?> GetOrFetchSkinDataAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;

        if (MemorySkinCache.TryGetValue(username, out var cached))
        {
            return cached;
        }

        // Check disk cache (reject corrupted 2425-byte Minotar dummy Steve)
        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCL", "cache", "skins");
        string diskPath = Path.Combine(cacheDir, $"{username}.png");
        string modelPath = Path.Combine(cacheDir, $"{username}.model");

        try
        {
            if (File.Exists(diskPath))
            {
                var bytes = await File.ReadAllBytesAsync(diskPath);
                if (bytes.Length > 0 && bytes.Length != 2425)
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
                        catch { }
                    }

                    var skinData = new SkinData(bytes, isSlim);
                    MemorySkinCache[username] = skinData;
                    return skinData;
                }
                else if (bytes.Length == 2425)
                {
                    File.Delete(diskPath);
                    if (File.Exists(modelPath)) File.Delete(modelPath);
                }
            }
        }
        catch { }

        // Check bundled assets (e.g. skin_miku_mew.png)
        try
        {
            string assetUri = $"avares://NegiCraftLauncher.Skin/Assets/skin_{username.ToLowerInvariant()}.png";
            if (AssetLoader.Exists(new Uri(assetUri)))
            {
                using var stream = AssetLoader.Open(new Uri(assetUri));
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                var bytes = ms.ToArray();
                if (bytes.Length > 0)
                {
                    bool isSlim = InferIsSlim(bytes, username);
                    var skinData = new SkinData(bytes, isSlim);
                    MemorySkinCache[username] = skinData;
                    try
                    {
                        Directory.CreateDirectory(cacheDir);
                        await File.WriteAllBytesAsync(diskPath, bytes);
                        await File.WriteAllTextAsync(modelPath, isSlim ? "slim" : "classic");
                    }
                    catch { }
                    return skinData;
                }
            }
        }
        catch { }

        // Fetch online from official Mojang, mineskin, or minotar
        var onlineData = await FetchFromMojangAsync(username)
                      ?? await FetchFromMineskinAsync(username)
                      ?? await FetchFromMinotarAsync(username);

        if (onlineData != null && onlineData.Bytes.Length > 0 && onlineData.Bytes.Length != 2425)
        {
            MemorySkinCache[username] = onlineData;
            try
            {
                Directory.CreateDirectory(cacheDir);
                await File.WriteAllBytesAsync(diskPath, onlineData.Bytes);
                await File.WriteAllTextAsync(modelPath, onlineData.IsSlim ? "slim" : "classic");
            }
            catch { }
            return onlineData;
        }

        return null;
    }

    /// <summary>
    /// Gets skin bytes for the username. Retained for backward compatibility.
    /// </summary>
    public static async Task<byte[]?> GetOrFetchSkinBytesAsync(string username) =>
        (await GetOrFetchSkinDataAsync(username))?.Bytes;

    private static async Task<SkinData?> FetchFromMojangAsync(string username)
    {
        try
        {
            var profileJson = await Http.GetStringAsync($"https://api.mojang.com/users/profiles/minecraft/{username}");
            using var profileDoc = System.Text.Json.JsonDocument.Parse(profileJson);
            if (!profileDoc.RootElement.TryGetProperty("id", out var idElem)) return null;
            string uuid = idElem.GetString() ?? "";
            if (string.IsNullOrEmpty(uuid)) return null;

            var sessionJson = await Http.GetStringAsync($"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}");
            using var sessionDoc = System.Text.Json.JsonDocument.Parse(sessionJson);
            if (!sessionDoc.RootElement.TryGetProperty("properties", out var props)) return null;

            foreach (var prop in props.EnumerateArray())
            {
                if (prop.TryGetProperty("name", out var nameElem) && nameElem.GetString() == "textures"
                    && prop.TryGetProperty("value", out var valElem))
                {
                    string base64 = valElem.GetString() ?? "";
                    byte[] decoded = Convert.FromBase64String(base64);
                    using var texDoc = System.Text.Json.JsonDocument.Parse(decoded);
                    if (texDoc.RootElement.TryGetProperty("textures", out var textures)
                        && textures.TryGetProperty("SKIN", out var skin)
                        && skin.TryGetProperty("url", out var urlElem))
                    {
                        bool isSlim = false;
                        if (skin.TryGetProperty("metadata", out var metaElem)
                            && metaElem.TryGetProperty("model", out var modelElem)
                            && string.Equals(modelElem.GetString(), "slim", StringComparison.OrdinalIgnoreCase))
                        {
                            isSlim = true;
                        }

                        string skinUrl = urlElem.GetString() ?? "";
                        if (!string.IsNullOrEmpty(skinUrl))
                        {
                            var bytes = await Http.GetByteArrayAsync(skinUrl.Replace("http://", "https://"));
                            if (bytes != null && bytes.Length > 0 && bytes.Length != 2425)
                            {
                                return new SkinData(bytes, isSlim);
                            }
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static async Task<SkinData?> FetchFromMineskinAsync(string username)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://mineskin.eu/skin/{username}");
            if (bytes != null && bytes.Length > 0 && bytes.Length != 2425)
            {
                bool isSlim = InferIsSlim(bytes, username);
                return new SkinData(bytes, isSlim);
            }
        }
        catch { }
        return null;
    }

    private static async Task<SkinData?> FetchFromMinotarAsync(string username)
    {
        try
        {
            var bytes = await Http.GetByteArrayAsync($"https://minotar.net/skin/{username}");
            // Minotar returns a 2425-byte default Steve when username is not found or rate-limited; reject it!
            if (bytes != null && bytes.Length > 0 && bytes.Length != 2425)
            {
                bool isSlim = InferIsSlim(bytes, username);
                return new SkinData(bytes, isSlim);
            }
        }
        catch { }
        return null;
    }

    private static bool InferIsSlim(byte[] bytes, string username)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            using var bmp = SKBitmap.Decode(ms);
            if (bmp != null)
            {
                return MinecraftSkinRender.SkinTypeChecker.IsSlimSkin(bmp);
            }
        }
        catch { }
        return IsSlimForPlayerName(username);
    }

    /// <summary>
    /// Chooses the render model for a texture.
    ///
    /// A 64x32 (1.7-era) texture must render as <see cref="MinecraftSkinRender.SkinType.Old"/>: its UV
    /// layout is entirely different, so handing it to the 1.8 model misaligns every face and draws the
    /// limb overlay layer — which the legacy format never defines — as stray triangles, leaving the pet
    /// looking like its limbs are detached or missing. Only 64x64 textures get the
    /// classic/slim distinction, where <paramref name="isSlim"/> is the authoritative profile hint
    /// (or, for offline names, a heuristic).
    /// </summary>
    public static MinecraftSkinRender.SkinType ResolveSkinType(byte[] bytes, bool isSlim)
    {
        if (IsLegacyTexture(bytes)) return MinecraftSkinRender.SkinType.Old;
        return isSlim ? MinecraftSkinRender.SkinType.NewSlim : MinecraftSkinRender.SkinType.New;
    }

    /// <summary>True for a 1.7-era 64x32 texture (width is exactly twice the height).</summary>
    public static bool IsLegacyTexture(byte[] bytes)
    {
        try
        {
            using var bmp = SKBitmap.Decode(bytes);
            return bmp != null && bmp.Width == bmp.Height * 2;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<byte[]?> FetchSkinBytesOnlineAsync(string username) =>
        await GetOrFetchSkinBytesAsync(username);

    public static async Task<uint[]?> FetchSkinOnlineAsync(string username)
    {
        var bytes = await GetOrFetchSkinBytesAsync(username);
        return bytes != null ? LoadSkinPixelsFromBytes(bytes) : null;
    }

    public static uint[] LoadSkinPixelsFromFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return CreateDefaultSteveSkin();
        }

        try
        {
            var bytes = File.ReadAllBytes(filePath);
            return LoadSkinPixelsFromBytes(bytes) ?? CreateDefaultSteveSkin();
        }
        catch
        {
            return CreateDefaultSteveSkin();
        }
    }

    public static byte[]? LoadSkinBytesFromFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                return File.ReadAllBytes(filePath);
            }
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Robustly decodes a skin texture from PNG bytes into 64x64 uint ARGB/BGRA pixels via SkiaSharp.
    /// Handles both modern 64x64 and legacy 64x32 textures.
    /// </summary>
    public static uint[]? LoadSkinPixelsFromBytes(byte[] bytes)
    {
        try
        {
            using var skBmp = SKBitmap.Decode(bytes);
            if (skBmp == null) return null;

            var pixels = new uint[64 * 64];
            int w = Math.Min(64, skBmp.Width);
            int h = Math.Min(64, skBmp.Height);

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = skBmp.GetPixel(x, y);
                    // Little-endian Bgra8888 representation:
                    // B: byte 0, G: byte 1, R: byte 2, A: byte 3
                    pixels[y * 64 + x] = ((uint)c.Alpha << 24)
                                       | ((uint)c.Red << 16)
                                       | ((uint)c.Green << 8)
                                       | (uint)c.Blue;
                }
            }

            return pixels;
        }
        catch
        {
            return null;
        }
    }

    public static uint[] LoadSkinPixelsFromStream(Stream stream)
    {
        try
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return LoadSkinPixelsFromBytes(ms.ToArray()) ?? CreateDefaultSteveSkin();
        }
        catch
        {
            return CreateDefaultSteveSkin();
        }
    }

    /// <summary>
    /// Computes a stable hash code matching PCL's implementation (MeloongCore.StringExtensions.GetStableHashCode).
    /// </summary>
    public static ulong GetStableHashCode(string str)
    {
        ulong result = 5381;
        foreach (char v in str) result = (result << 5) ^ result ^ (ulong)v;
        return result ^ 0xA98F501BC684032FUL;
    }

    private static string EnsureLength(string? str, char code, int length)
    {
        str ??= "";
        return str.Length > length ? str[..length] : str.PadLeft(length, code);
    }

    /// <summary>
    /// PCL's offline player UUID generation (ModLaunch.McLoginLegacyUuid).
    /// </summary>
    public static string McLoginLegacyUuid(string name)
    {
        string part1 = EnsureLength(name.Length.ToString("X"), '0', 16);
        string part2 = EnsureLength(GetStableHashCode(name).ToString("X"), '0', 16);
        string fullUuid = part1 + part2;
        return fullUuid[..12] + "3" + fullUuid.Substring(13, 3) + "9" + fullUuid[17..];
    }

    /// <summary>
    /// PCL's offline skin gender / model determination (ModMinecraft.McSkinSex).
    /// Returns "Alex" (slim) or "Steve" (classic).
    /// </summary>
    public static string McSkinSex(string uuid)
    {
        if (uuid.Length != 32) return "Steve";
        int a = int.Parse(uuid[7].ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
        int b = int.Parse(uuid[15].ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
        int c = int.Parse(uuid[23].ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
        int d = int.Parse(uuid[31].ToString(), System.Globalization.NumberStyles.AllowHexSpecifier);
        return ((a ^ b ^ c ^ d) % 2 != 0) ? "Alex" : "Steve";
    }

    /// <summary>
    /// Offline players have no profile property stating the model, so the default skin is
    /// picked using PCL's McSkinSex(McLoginLegacyUuid(name)) algorithm (Alex = slim/少女, Steve = classic).
    /// </summary>
    public static bool IsSlimForPlayerName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (MemorySkinCache.TryGetValue(name, out var cached))
        {
            return cached.IsSlim;
        }

        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NCL", "cache", "skins");
        string modelPath = Path.Combine(cacheDir, $"{name}.model");
        if (File.Exists(modelPath))
        {
            try
            {
                var txt = File.ReadAllText(modelPath).Trim();
                if (string.Equals(txt, "slim", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(txt, "classic", StringComparison.OrdinalIgnoreCase)) return false;
            }
            catch { }
        }

        if (name.Contains("alex", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.Contains("steve", StringComparison.OrdinalIgnoreCase)) return false;

        var uuid = McLoginLegacyUuid(name);
        return McSkinSex(uuid) == "Alex";
    }

    /// <summary>The bundled vanilla default texture for the requested model.</summary>
    public static uint[] CreateDefaultSkin(bool slim)
    {
        try
        {
            var bytes = DefaultSkinBytes(slim);
            return LoadSkinPixelsFromBytes(bytes) ?? FallbackSolidSkin();
        }
        catch
        {
            return FallbackSolidSkin();
        }
    }

    private static uint[] FallbackSolidSkin()
    {
        var pixels = new uint[64 * 64];
        for (var i = 0; i < pixels.Length; i++) pixels[i] = 0xFFC49A76;
        return pixels;
    }

    public static uint[] CreateDefaultSteveSkin() => CreateDefaultSkin(slim: false);
    public static uint[] CreateDefaultAlexSkin() => CreateDefaultSkin(slim: true);

    /// <summary>The same default as PNG bytes, for callers that ingest encoded skins.</summary>
    public static byte[] DefaultSkinBytes(bool slim)
    {
        using var stream = AssetLoader.Open(
            new Uri($"avares://NegiCraftLauncher.Skin/Assets/{(slim ? "skin_alex" : "skin_steve")}.png"));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}

public sealed record SkinData(byte[] Bytes, bool IsSlim);

