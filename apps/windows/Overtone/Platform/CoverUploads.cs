using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Overtone.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Overtone.Platform;

/// <summary>
/// Public URLs for artwork no catalogue has (local files, rare releases), so Discord
/// can show the cover the user actually sees. Discord only takes image links, so the
/// cover (the 256 px display copy, as JPEG) goes to Litterbox, catbox.moe's anonymous
/// temporary host, and expires there after 72 hours. Uploads are remembered by the
/// artwork's hash in %LOCALAPPDATA%\Overtone\Cache\covers.json, so each cover is sent
/// once per expiry, not once per play. Only used while Discord presence is on.
/// </summary>
public sealed class CoverUploads
{
    private const string Endpoint = "https://litterbox.catbox.moe/resources/internals/api.php";
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(72);
    /// <summary>Re-upload this long before the host deletes the file, so a link never dies mid-song.</summary>
    private static readonly TimeSpan Margin = TimeSpan.FromHours(3);
    private static readonly string FilePath = Path.Combine(Settings.DataDir, "Cache", "covers.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly ConditionalWeakTable<Artwork, string> Hashes = new();

    private readonly Dictionary<string, Entry> _entries = Load();
    private readonly Dictionary<string, Task<string?>> _pending = new();

    private sealed record Entry(string Url, DateTimeOffset Expires);

    /// <summary>A short stable id for the artwork's pixels.</summary>
    public static string Hash(Artwork art) =>
        Hashes.GetValue(art, a => Convert.ToHexString(SHA256.HashData(a.Rgba))[..20]);

    /// <summary>A live public URL for <paramref name="art"/>, uploading it if needed. Null if the upload fails.</summary>
    public Task<string?> UrlForAsync(Artwork art)
    {
        var hash = Hash(art);
        if (_entries.TryGetValue(hash, out var e) && e.Expires - Margin > DateTimeOffset.UtcNow) return Task.FromResult<string?>(e.Url);
        // Two songs from one album resolve at once: one upload.
        if (_pending.TryGetValue(hash, out var running)) return running;
        var task = UploadAsync(hash, art);
        _pending[hash] = task;
        return task;
    }

    private async Task<string?> UploadAsync(string hash, Artwork art)
    {
        try
        {
            var jpeg = await EncodeAsync(art);
            using var form = new MultipartFormDataContent
            {
                { new StringContent("fileupload"), "reqtype" },
                { new StringContent("72h"), "time" },
                { new ByteArrayContent(jpeg) { Headers = { ContentType = new("image/jpeg") } }, "fileToUpload", "cover.jpg" },
            };
            using var response = await Http.PostAsync(Endpoint, form);
            var url = (await response.Content.ReadAsStringAsync()).Trim();
            if (!response.IsSuccessStatusCode || !url.StartsWith("https://", StringComparison.Ordinal) || url.Contains(' '))
                throw new HttpRequestException($"cover upload failed: {(int)response.StatusCode} {url[..Math.Min(url.Length, 200)]}");
            _entries[hash] = new Entry(url, DateTimeOffset.UtcNow + Lifetime);
            Save();
            return url;
        }
        catch (Exception ex)
        {
            Settings.LogError(ex);
            return null;
        }
        finally
        {
            _pending.Remove(hash);
        }
    }

    private static async Task<byte[]> EncodeAsync(Artwork art)
    {
        using var stream = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { ["ImageQuality"] = new BitmapTypedValue(0.9f, Windows.Foundation.PropertyType.Single) };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, options);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)art.DisplaySize, (uint)art.DisplaySize, 96, 96, art.DisplayBgra);
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)bytes.Length, InputStreamOptions.None);
        return bytes;
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var all = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new();
                var now = DateTimeOffset.UtcNow;
                return all.Where(kv => kv.Value.Expires > now).ToDictionary(kv => kv.Key, kv => kv.Value);
            }
        }
        catch (Exception)
        {
            // A corrupt cache only costs a re-upload.
        }
        return new();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries));
        }
        catch (Exception)
        {
        }
    }
}
