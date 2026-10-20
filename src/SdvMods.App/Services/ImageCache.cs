using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using QRCoder;
using SdvMods.Core.Settings;

namespace SdvMods.App.Services;

/// <summary>Downloads remote images once into the cache folder and decodes them (downscaled) for the UI.</summary>
public sealed class ImageCache
{
    readonly HttpClient _http;
    readonly SemaphoreSlim _gate = new(4);
    readonly ConcurrentDictionary<string, Task<Bitmap?>> _memory = new();
    static string Dir => Path.Combine(AppPaths.CacheDir, "images");

    public ImageCache(HttpClient http) => _http = http;

    public Task<Bitmap?> GetAsync(string? url, int decodeWidth = 320)
        => string.IsNullOrEmpty(url) ? Task.FromResult<Bitmap?>(null) : _memory.GetOrAdd(url + "|" + decodeWidth, _ => LoadAsync(url, decodeWidth));

    async Task<Bitmap?> LoadAsync(string url, int width)
    {
        await _gate.WaitAsync();
        try
        {
            var file = Path.Combine(Dir, Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
            if (!File.Exists(file))
            {
                Directory.CreateDirectory(Dir);
                var bytes = await _http.GetByteArrayAsync(url);
                var tmp = file + ".tmp";
                await File.WriteAllBytesAsync(tmp, bytes);
                File.Move(tmp, file, overwrite: true);
            }
            return await Task.Run(() =>
            {
                using var fs = File.OpenRead(file);
                return Bitmap.DecodeToWidth(fs, width);
            });
        }
        catch { return null; }
        finally { _gate.Release(); }
    }

    /// <summary>Renders a QR code as a bitmap.</summary>
    public static Bitmap Qr(string text)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(10);
        return new Bitmap(new MemoryStream(png));
    }
}
