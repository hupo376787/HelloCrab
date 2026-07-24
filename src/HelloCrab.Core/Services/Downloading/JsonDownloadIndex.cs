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

    internal static string BuildKey(WorkItem work, CrawlerDownloadOptions options)
        => BuildCanonicalKey(
            NormalizePlatformId(work.PlatformId),
            work.AuthorId,
            work.WorkId,
            options.IncludeWorkId,
            options.DownloadCover,
            options.DownloadMusic,
            options.CheckVideoAudio,
            options.EnablePersonDetection);

    public async Task<bool> IsCompletedAsync(string authorFolder, string key, CancellationToken cancellationToken)
    {
        var set = await GetIndexAsync(authorFolder, cancellationToken);
        return set.Contains(key);
    }

    public async Task MarkCompletedAsync(string authorFolder, string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var set = await GetIndexCoreAsync(authorFolder, cancellationToken);
            if (!set.Add(key))
                return;

            await WriteIndexCoreAsync(authorFolder, set, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<HashSet<string>> GetIndexAsync(string authorFolder, CancellationToken cancellationToken)
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
                {
                    set = NormalizeStoredKeys(items, out needsRewrite);
                }
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
                // 把历史处理版本和平台解析版本前缀迁移为稳定的平台键，
                // 避免以后再因解析器版本变化导致 crawler-index.json 失配。
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
        changed = false;
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var storedKey in storedKeys)
        {
            var normalizedKeys = NormalizeStoredKey(storedKey);
            if (normalizedKeys.Count != 1
                || !string.Equals(normalizedKeys[0], storedKey, StringComparison.Ordinal))
            {
                changed = true;
            }

            normalized.UnionWith(normalizedKeys);
        }

        if (normalized.Count != storedKeys.Count())
            changed = true;

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeStoredKey(string? storedKey)
    {
        if (string.IsNullOrWhiteSpace(storedKey))
            return Array.Empty<string>();

        const string legacyV4Prefix = "v4-person-filter:";
        if (storedKey.StartsWith(legacyV4Prefix, StringComparison.Ordinal))
        {
            var body = storedKey[legacyV4Prefix.Length..];
            return TryBuildCanonicalKey(body, defaultAudio: false, defaultPerson: false, out var key)
                ? new[] { key }
                : new[] { storedKey };
        }

        const string legacyV3Prefix = "v3-audio-repair:";
        if (storedKey.StartsWith(legacyV3Prefix, StringComparison.Ordinal))
        {
            var body = storedKey[legacyV3Prefix.Length..];
            if (!TryBuildCanonicalKey(body, defaultAudio: true, defaultPerson: false, out var audioKey))
                return new[] { storedKey };

            // v3 当时会执行音轨检测/修复。它既满足当前“开启音轨检测”的记录，
            // 也满足当前“关闭音轨检测”的较低要求，因此迁移为两个稳定键。
            var noAudioKey = ReplaceFlag(audioKey, "audio", false);
            return string.Equals(audioKey, noAudioKey, StringComparison.Ordinal)
                ? new[] { audioKey }
                : new[] { audioKey, noAudioKey };
        }

        // 早期 Pinterest、微博解析升级时曾把版本写进平台前缀，例如
        // pinterest-media-v4 / weibo-media-v2。索引只应该记录稳定的平台 ID，
        // 因此读取旧文件时统一迁移为 pinterest / weibo。
        if (TryBuildCanonicalKey(
                storedKey,
                defaultAudio: false,
                defaultPerson: false,
                out var canonicalKey))
        {
            return new[] { canonicalKey };
        }

        return new[] { storedKey };
    }

    private static bool TryBuildCanonicalKey(
        string body,
        bool defaultAudio,
        bool defaultPerson,
        out string key)
    {
        key = string.Empty;
        var parts = body.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
            return false;

        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["workId"] = false,
            ["cover"] = false,
            ["music"] = false,
            ["audio"] = defaultAudio,
            ["person"] = defaultPerson
        };

        for (var index = 3; index < parts.Length; index++)
        {
            var separator = parts[index].IndexOf('=');
            if (separator <= 0)
                continue;

            var name = parts[index][..separator];
            var value = parts[index][(separator + 1)..];
            if (flags.ContainsKey(name))
                flags[name] = value == "1" || bool.TryParse(value, out var parsed) && parsed;
        }

        key = BuildCanonicalKey(
            NormalizePlatformId(parts[0]),
            parts[1],
            parts[2],
            flags["workId"],
            flags["cover"],
            flags["music"],
            flags["audio"],
            flags["person"]);
        return true;
    }

    private static string NormalizePlatformId(string? platformId)
    {
        var value = platformId?.Trim() ?? string.Empty;
        foreach (var knownPlatformId in KnownPlatformIds)
        {
            if (value.Equals(knownPlatformId, StringComparison.OrdinalIgnoreCase))
                return knownPlatformId;

            // 兼容历史版本前缀：weibo-media-v2、pinterest-media-v4，
            // 以及以后可能出现的同类 "平台-功能-v数字" 写法。
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

    private static string ReplaceFlag(string key, string flagName, bool enabled)
    {
        var oldValue = $":{flagName}={(enabled ? 0 : 1)}";
        var newValue = $":{flagName}={(enabled ? 1 : 0)}";
        return key.Replace(oldValue, newValue, StringComparison.Ordinal);
    }

    private static string BuildCanonicalKey(
        string platformId,
        string authorId,
        string workId,
        bool includeWorkId,
        bool downloadCover,
        bool downloadMusic,
        bool checkVideoAudio,
        bool enablePersonDetection)
        => $"{platformId}:{authorId}:{workId}:" +
           $"workId={(includeWorkId ? 1 : 0)}:" +
           $"cover={(downloadCover ? 1 : 0)}:" +
           $"music={(downloadMusic ? 1 : 0)}:" +
           $"audio={(checkVideoAudio ? 1 : 0)}:" +
           $"person={(enablePersonDetection ? 1 : 0)}";

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
            await JsonSerializer.SerializeAsync(stream, set, JsonOptions, cancellationToken);
        }

        File.Move(temp, filePath, true);
    }
}
