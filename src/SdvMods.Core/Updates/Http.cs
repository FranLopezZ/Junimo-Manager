using System.Net.Http.Headers;

namespace SdvMods.Core.Updates;

public static class Http
{
    public const string AppName = "JunimoManager";
    public static string AppVersion => AppInfo.Version;

    public static HttpClient Create()
    {
        var c = new HttpClient(new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppName, AppVersion));
        return c;
    }

    /// <summary>Downloads to a file, reporting progress 0..1 (or -1 when the size is unknown).</summary>
    public static async Task DownloadAsync(HttpClient http, string url, string destination, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = File.Create(destination);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await input.ReadAsync(buffer, ct)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            progress?.Report(total is > 0 ? (double)read / total.Value : -1);
        }
    }
}


