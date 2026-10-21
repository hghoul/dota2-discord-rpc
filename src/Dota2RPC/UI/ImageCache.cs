using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Dota2RPC.Core;

namespace Dota2RPC.UI;

/// <summary>Загрузка картинок для предпросмотра (те же URL, что получает Discord) с кэшем в памяти и на диске.</summary>
internal static class ImageCache
{
    static readonly ConcurrentDictionary<string, Image?> Memory = new();
    static readonly ConcurrentDictionary<string, Task> Loading = new();

    public static event Action? ImageLoaded;

    public static Image? TryGet(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (Memory.TryGetValue(url, out var image)) return image;
        Loading.GetOrAdd(url, u => Task.Run(() => LoadAsync(u)));
        return null;
    }

    static async Task LoadAsync(string url)
    {
        Image? image = null;
        try
        {
            var file = Path.Combine(AppPaths.CacheDir, "img", Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".png");
            byte[] bytes;
            if (File.Exists(file) && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromDays(14))
            {
                bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
            }
            else
            {
                bytes = await Http.Client.GetByteArrayAsync(url).ConfigureAwait(false);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                await File.WriteAllBytesAsync(file, bytes).ConfigureAwait(false);
            }
            using var ms = new MemoryStream(bytes);
            using var decoded = Image.FromStream(ms);
            image = new Bitmap(decoded);
        }
        catch (Exception ex)
        {
            Log.Warn($"Предпросмотр: не удалось загрузить {url}: {ex.Message}");
        }
        Memory[url] = image;
        Loading.TryRemove(url, out _);
        ImageLoaded?.Invoke();
    }
}
