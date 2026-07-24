using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;

namespace HelloCrab.Core.Services.Images;

public sealed class ImageCacheService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _cacheDirectory;
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _memoryCache = new(StringComparer.Ordinal);

    public ImageCacheService()
    {
        _httpClient = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "(KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd(
            "image/avif,image/webp,image/apng,image/*,*/*;q=0.8");

        _cacheDirectory = Path.Combine(AppContext.BaseDirectory, "image-cache");
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法在程序目录创建图片缓存目录：{_cacheDirectory}。请将 HelloCrab 放到当前用户可写的目录。",
                ex);
        }
    }

    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// 删除 image-cache 目录中的磁盘文件。已经显示在界面中的 Bitmap 继续保留在内存中，
    /// 避免清理缓存时让当前头像或封面突然失效；程序重新请求图片时会重新写入缓存。
    /// </summary>
    public Task<ImageCacheClearResult> ClearDiskCacheAsync(
        CancellationToken cancellationToken = default)
        => Task.Run(() => ClearDiskCacheCore(cancellationToken), cancellationToken);

    private ImageCacheClearResult ClearDiskCacheCore(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_cacheDirectory);

        var deletedFileCount = 0;
        var failedFileCount = 0;
        long releasedBytes = 0;

        var files = Directory
            .EnumerateFiles(_cacheDirectory, "*", SearchOption.AllDirectories)
            .ToArray();

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var fileInfo = new FileInfo(file);
                var length = fileInfo.Exists ? fileInfo.Length : 0;
                File.SetAttributes(file, FileAttributes.Normal);
                File.Delete(file);
                releasedBytes += length;
                deletedFileCount++;
            }
            catch (FileNotFoundException)
            {
                // 文件已被其他清理流程删除，按成功处理即可。
            }
            catch (DirectoryNotFoundException)
            {
                // 目录已被其他清理流程删除，按成功处理即可。
            }
            catch
            {
                failedFileCount++;
            }
        }

        foreach (var directory in Directory
                     .EnumerateDirectories(_cacheDirectory, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static path => path.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any())
                    Directory.Delete(directory);
            }
            catch
            {
                // 清空缓存的核心目标是删除文件，残留空目录不影响后续使用。
            }
        }

        Directory.CreateDirectory(_cacheDirectory);
        return new ImageCacheClearResult(
            _cacheDirectory,
            deletedFileCount,
            failedFileCount,
            releasedBytes);
    }

    public async Task<Bitmap?> LoadAsync(
        string? url,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _))
            return null;

        // 下载失败不能永久缓存一个 null Task，否则 HeadUrl 后续即使恢复可用，
        // 桌面端和远程端也永远不会再次请求。实际下载不绑定某个调用者的取消令牌，
        // 调用者只取消自己的等待。
        var task = _memoryCache.GetOrAdd(
            url,
            static (key, service) => service.LoadCoreAsync(key, CancellationToken.None),
            this);

        try
        {
            var bitmap = await task.WaitAsync(cancellationToken);
            if (bitmap is null)
                RemoveCachedTask(url, task);

            return bitmap;
        }
        catch
        {
            RemoveCachedTask(url, task);
            throw;
        }
    }

    private void RemoveCachedTask(string url, Task<Bitmap?> task)
    {
        if (_memoryCache.TryGetValue(url, out var current)
            && ReferenceEquals(current, task))
        {
            _memoryCache.TryRemove(url, out _);
        }
    }

    private async Task<Bitmap?> LoadCoreAsync(string url, CancellationToken cancellationToken)
    {
        var filePath = Path.Combine(_cacheDirectory, CreateCacheKey(url) + ".img");
        try
        {
            if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
            {
                var tempPath = filePath + ".tmp";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Referrer = ResolveReferrer(url);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(
                                 tempPath,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 64 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await input.CopyToAsync(output, 64 * 1024, cancellationToken);
                    await output.FlushAsync(cancellationToken);
                }

                File.Move(tempPath, filePath, true);
            }

            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            return new Bitmap(stream);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 非零但损坏的缓存文件也必须删除，否则每次都会重复解码坏文件。
            TryDelete(filePath);
            TryDelete(filePath + ".tmp");
            return null;
        }
    }


    private static Uri ResolveReferrer(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Host.Contains("hdslb.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("biliimg.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("bilivideo.com", StringComparison.OrdinalIgnoreCase)))
        {
            return new Uri("https://www.bilibili.com/");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out uri)
            && (uri.Host.Contains("tiktokcdn.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("byteoversea.com", StringComparison.OrdinalIgnoreCase)))
        {
            return new Uri("https://www.tiktok.com/");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out uri)
            && (uri.Host.Contains("yximgs", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("gifshow", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("kuaishou", StringComparison.OrdinalIgnoreCase)))
        {
            return new Uri("https://www.kuaishou.com/");
        }


        if (Uri.TryCreate(url, UriKind.Absolute, out uri)
            && (uri.Host.Contains("xhscdn.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("xiaohongshu.com", StringComparison.OrdinalIgnoreCase)))
        {
            return new Uri("https://www.xiaohongshu.com/");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out uri)
            && (uri.Host.Contains("sinaimg.cn", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("weibo.com", StringComparison.OrdinalIgnoreCase)))
        {
            return new Uri("https://weibo.com/");
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out uri)
            && (uri.Host.Contains("meipian", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("ivwen", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Contains("mpvolc", StringComparison.OrdinalIgnoreCase)))
        {
            return new Uri("https://www.meipian.cn/");
        }

        return new Uri("https://www.douyin.com/");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }

    private static string CreateCacheKey(string url)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(url));
        return Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        foreach (var task in _memoryCache.Values)
        {
            if (task.IsCompletedSuccessfully)
                task.Result?.Dispose();
        }
        _memoryCache.Clear();
        _httpClient.Dispose();
    }
}

public sealed record ImageCacheClearResult(
    string CacheDirectory,
    int DeletedFileCount,
    int FailedFileCount,
    long ReleasedBytes);
