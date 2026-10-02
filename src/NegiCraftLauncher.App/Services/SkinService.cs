using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace NegiCraftLauncher.App.Services;

public static class SkinService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static uint[] LoadSkinPixelsFromFile(string filePath)
    {
        var pixels = new uint[64 * 64];
        if (!File.Exists(filePath))
        {
            return CreateDefaultSteveSkin();
        }

        try
        {
            using var stream = File.OpenRead(filePath);
            var bmp = new Bitmap(stream);
            var wb = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var fb = wb.Lock())
            {
                bmp.CopyPixels(new PixelRect(0, 0, Math.Min(64, bmp.PixelSize.Width), Math.Min(64, bmp.PixelSize.Height)),
                               fb.Address, fb.RowBytes * 64, fb.RowBytes);
                unsafe
                {
                    uint* ptr = (uint*)fb.Address;
                    for (int i = 0; i < 64 * 64; i++)
                    {
                        // In BGRA format: B is byte 0, G is 1, R is 2, A is 3
                        pixels[i] = ptr[i];
                    }
                }
            }
            return pixels;
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

    public static async Task<byte[]?> FetchSkinBytesOnlineAsync(string username)
    {
        try
        {
            string url = $"https://minotar.net/skin/{username}";
            return await Http.GetByteArrayAsync(url);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<uint[]?> FetchSkinOnlineAsync(string username)
    {
        try
        {
            string url = $"https://minotar.net/skin/{username}";
            var bytes = await Http.GetByteArrayAsync(url);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            var wb = new WriteableBitmap(new PixelSize(64, 64), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            var pixels = new uint[64 * 64];
            using (var fb = wb.Lock())
            {
                bmp.CopyPixels(new PixelRect(0, 0, 64, 64), fb.Address, fb.RowBytes * 64, fb.RowBytes);
                unsafe
                {
                    uint* ptr = (uint*)fb.Address;
                    for (int i = 0; i < 64 * 64; i++)
                    {
                        pixels[i] = ptr[i];
                    }
                }
            }
            return pixels;
        }
        catch
        {
            return null;
        }
    }

    // The head is drawn as two layers: the face at 48px and the hat layer at 56px, so the
    // outer cube overhangs the head instead of being flattened onto it.
    private const int AvatarSize = 64;
    private const int FaceSize = 48;
    private const int HatSize = 56;

    public static WriteableBitmap CreateAvatarFromSkin(uint[] skinPixels)
    {
        var bmp = new WriteableBitmap(new PixelSize(AvatarSize, AvatarSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var fb = bmp.Lock();
        unsafe
        {
            uint* ptr = (uint*)fb.Address;
            for (int i = 0; i < AvatarSize * AvatarSize; i++) ptr[i] = 0;

            // Head front: (8, 8), 8x8
            Blot(ptr, skinPixels, 8, (AvatarSize - FaceSize) / 2, FaceSize / 8, blend: false);
            // Hat front: (40, 8), 8x8, drawn larger and blended over the face
            Blot(ptr, skinPixels, 40, (AvatarSize - HatSize) / 2, HatSize / 8, blend: true);
        }
        return bmp;
    }

    private static unsafe void Blot(uint* dst, uint[] src, int srcX, int offset, int scale, bool blend)
    {
        for (int py = 0; py < 8; py++)
        {
            for (int px = 0; px < 8; px++)
            {
                uint col = src[(8 + py) * 64 + (srcX + px)];
                if ((col >> 24) == 0) continue;

                uint a = (col >> 24) & 0xFF;
                for (int y = 0; y < scale; y++)
                {
                    int dy = offset + py * scale + y;
                    for (int x = 0; x < scale; x++)
                    {
                        int dx = offset + px * scale + x;
                        ref uint at = ref dst[dy * AvatarSize + dx];
                        if (!blend)
                        {
                            at = col;
                            continue;
                        }
                        uint da = at >> 24;
                        at = ((a + da * (255 - a) / 255) << 24)
                           | (((col >> 16 & 0xFF) + (at >> 16 & 0xFF) * (255 - a) / 255) << 16)
                           | (((col >> 8 & 0xFF) + (at >> 8 & 0xFF) * (255 - a) / 255) << 8)
                           | ((col & 0xFF) + (at & 0xFF) * (255 - a) / 255);
                    }
                }
            }
        }
    }

    public static uint[] CreateDefaultSteveSkin()
    {
        var pixels = new uint[64 * 64];
        // Default Steve skin fallback
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 0xFFC49A76; // base skin
        return pixels;
    }
}
