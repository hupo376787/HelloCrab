using System.Text.Json;
using HelloCrab.Core.Models;

namespace HelloCrab.Core.Services.Downloading;

public sealed class JsonDownloadIndex
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, HashSet<string>> _cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] KnownPlatformIds =
    {
        "douyin",
        "tiktok",
        "kuaishou",
        "xiaohongshu",
        "weibo",
        "meipian",
        "instagram",
        "bilibili",
        "pinterest"
    };

    // 完成索引只认“平台 + 作者 ID + 作品 ID”。下载选项、文件命名方式、
    // 人像检测、音轨检测以及本地文件后来是否被删除，都不会让已完成作品重新下载。
    internal static string BuildKey(WorkItem work, CrawlerDownloadOptions options)
        => BuildIdentityKey(
            NormalizePlatformId(work.PlatformId),
            work.AuthorId,
            work.WorkId);

    public async Task<bool> IsCompletedAsync(
        string authorFolder,
        WorkItem work,
        CrawlerDownloadOptions options,
        CancellationToken cancellationToken)
    {
        var set = await GetIndexAsync(authorFolder, cancellationToken);
        return set.Contains(BuildKey(work, options));
    }

    public async Task<bool> IsCompletedAsync(
        string authorFolder,
        string key,
        CancellationToken cancellationToken)
    {
        var set = await GetIndexAsync(authorFolder, cancellationToken);
        return NormalizeStoredKey(key).Any(set.Contains);
    }

    public async Task MarkCompletedAsync(
        string authorFolder,
        string key,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var set = await GetIndexCoreAsync(authorFolder, cancellationToken);
            var normalizedKeys = NormalizeStoredKey(key);
            var normalizedKey = normalizedKeys.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(normalizedKey) || !set.Add(normalizedKey))
                return;

            await WriteIndexCoreAsync(authorFolder, set, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HashSet<string>> GetIndexAsync(
        string authorFolder,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return await GetIndexCoreAsync(authorFolder, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HashSet<string>> GetIndexCoreAsync(
        string authorFolder,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(authorFolder, out var cached))
            return cached;

        var set = new HashSet<string>(StringComparer.Ordinal);
        var filePath = Path.Combine(authorFolder, "crawler-index.json");
        var needsRewrite = false;
        if (File.Exists(filePath))
        {
            try
            {
                await using var stream = File.OpenRead(filePath);
                var items = await JsonSerializer.DeserializeAsync<HashSet<string>>(
                    stream,
                    cancellationToken: cancellationToken);
                if (items is not null)
                    set = NormalizeStoredKeys(items, out needsRewrite);
            }
            catch
            {
                // 单个作者索引损坏时不阻止采集，后续完成项会重建该索引。
            }
        }

        _cache[authorFolder] = set;
        if (needsRewrite)
        {
            try
            {
                // 旧版索引包含下载设置、处理开关和历史版本前缀。
                // 首次读取时统一收敛为“平台:作者ID:作品ID”，以后设置变化不再影响完成判断。
                await WriteIndexCoreAsync(authorFolder, set, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 迁移写回失败不影响本次内存中的索引使用；下次启动会再次尝试。
            }
        }

        return set;
    }

    internal static HashSet<string> NormalizeStoredKeys(
        IEnumerable<string> storedKeys,
        out bool changed)
    {
        var source = storedKeys as ICollection<string> ?? storedKeys.ToArray();
        changed = false;
        var normalized = new HashSet<string>(StringComparer.Ordinal);

        foreach (var storedKey in source)
        {
            var normalizedKeys = NormalizeStoredKey(storedKey);
            if (normalizedKeys.Count != 1
                || !string.Equals(normalizedKeys[0], storedKey, StringComparison.Ordinal))
            {
                changed = true;
            }

            normalized.UnionWith(normalizedKeys);
        }

        // 同一作品旧版本可能因为不同下载选项留下多条完成记录；迁移后只保留一条身份键。
        if (normalized.Count != source.Count)
            changed = true;

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeStoredKey(string? storedKey)
    {
        if (string.IsNullOrWhiteSpace(storedKey))
            return Array.Empty<string>();

        var body = storedKey.Trim();

        // 兼容曾经写入索引的处理版本前缀。
        const string legacyV4Prefix = "v4-person-filter:";
        if (body.StartsWith(legacyV4Prefix, StringComparison.Ordinal))
            body = body[legacyV4Prefix.Length..];

        const string legacyV3Prefix = "v3-audio-repair:";
        if (body.StartsWith(legacyV3Prefix, StringComparison.Ordinal))
            body = body[legacyV3Prefix.Length..];

        var parts = body.Split(
            ':',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return new[] { storedKey };

        var platformId = NormalizePlatformId(parts[0]);
        var authorId = parts[1];
        var workId = parts[2];
        if (string.IsNullOrWhiteSpace(platformId)
            || string.IsNullOrWhiteSpace(authorId)
            || string.IsNullOrWhiteSpace(workId))
        {
            return new[] { storedKey };
        }

        return new[] { BuildIdentityKey(platformId, authorId, workId) };
    }

    private static string NormalizePlatformId(string? platformId)
    {
        var value = platformId?.Trim() ?? string.Empty;

        // 快手主站与 Live 站只是入口和接口不同，作品完成索引继续互通。
        if (value.Equals("kuaishou-live", StringComparison.OrdinalIgnoreCase))
            return "kuaishou";

        foreach (var knownPlatformId in KnownPlatformIds)
        {
            if (value.Equals(knownPlatformId, StringComparison.OrdinalIgnoreCase))
                return knownPlatformId;

            // 兼容历史版本前缀：weibo-media-v2、pinterest-media-v4 等。
            if (value.StartsWith(knownPlatformId + "-", StringComparison.OrdinalIgnoreCase)
                && System.Text.RegularExpressions.Regex.IsMatch(
                    value,
                    @"-v\d+$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return knownPlatformId;
            }
        }

        return value;
    }

    private static string BuildIdentityKey(
        string platformId,
        string authorId,
        string workId)
        => $"{platformId}:{authorId}:{workId}";

    private static async Task WriteIndexCoreAsync(
        string authorFolder,
        HashSet<string> set,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(authorFolder);
        var filePath = Path.Combine(authorFolder, "crawler-index.json");
        var temp = filePath + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                set,
                JsonOptions,
                cancellationToken);
        }

        File.Move(temp, filePath, true);
    }
}
