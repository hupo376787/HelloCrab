using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Downloading;
using HelloCrab.Core.Services.Images;

namespace HelloCrab.Core.Services.History;

public sealed class DownloadHistoryService
{
    // System.Text.Json can still write supplementary-plane characters as UTF-16 surrogate-pair
    // escapes (for example \uD83D\uDC95) even when a relaxed encoder is used. History.json is a
    // local UTF-8 file, so after serialization we safely restore only unescaped Unicode emoji
    // sequences to their real characters. The negative look-behind prevents touching a literal
    // user string such as "\\uD83D\\uDC95".
    private static readonly Regex EscapedSurrogatePairRegex = new(
        @"(?<!\\)\\u(?<high>[dD][89aAbB][0-9a-fA-F]{2})\\u(?<low>[dD][c-fC-F][0-9a-fA-F]{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex EscapedEmojiBmpRegex = new(
        @"(?<!\\)\\u(?<code>200[dD]|20[eE]3|[fF][eE]0[eEfF]|2[67][0-9a-fA-F]{2})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly UTF8Encoding Utf8WithoutBom = new(false);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        // History.json 是本地 UTF-8 JSON 文件。使用宽松编码器后，中文和非 BMP Emoji
        // 都直接保存为可读字符，例如“💕玲姐💕”，不再写成 Unicode 代理对转义。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private readonly string _filePath;
    private readonly string[] _legacyFilePaths;
    private readonly List<DownloadHistoryItem> _items = new();
    private bool _loaded;

    public DownloadHistoryService()
    {
        // 按用户要求，下载历史与可执行文件放在同一目录。
        _filePath = Path.Combine(AppContext.BaseDirectory, "History.json");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(appData))
        {
            appData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        // 兼容旧版本：首次运行新版本时会读取旧文件，并迁移为 exe 根目录的 History.json。
        _legacyFilePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "download-history.json"),
            Path.Combine(appData, "HelloCrab", "download-history.json")
        ];
    }

    public string FilePath => _filePath;

    public event EventHandler<IReadOnlyList<DownloadHistoryItem>>? HistoryChanged;

    public async Task<IReadOnlyList<DownloadHistoryItem>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            return Snapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<DownloadHistoryItem?> FindAuthorAsync(
        string platformId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            var item = _items.FirstOrDefault(x =>
                PlatformMatches(x.Platform, platformId)
                && x.UserId.Equals(userId, StringComparison.Ordinal));
            return item is null ? null : Clone(item);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertDownloadedAuthorAsync(
        WorkItem work,
        string authorFolder,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DownloadHistoryItem> snapshot;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);

            var item = _items.FirstOrDefault(x =>
                x.Platform.Equals(work.PlatformId, StringComparison.OrdinalIgnoreCase)
                && x.UserId.Equals(work.AuthorId, StringComparison.Ordinal));

            if (item is null)
            {
                item = new DownloadHistoryItem
                {
                    Id = _items.Count == 0 ? 1 : _items.Max(x => x.Id) + 1,
                    SortOrder = 0,
                    Platform = NormalizePlatformName(work.PlatformId),
                    UserId = work.AuthorId
                };
                _items.Insert(0, item);
            }
            else
            {
                // 最近发生下载的作者始终移动到列表第一项。
                var oldIndex = _items.IndexOf(item);
                if (oldIndex > 0)
                {
                    _items.RemoveAt(oldIndex);
                    _items.Insert(0, item);
                }
            }

            item.Platform = NormalizePlatformName(work.PlatformId);
            item.HeadUrl = work.AuthorAvatarUrl ?? item.HeadUrl;
            item.UserId = work.AuthorId;
            item.UserName = work.AuthorName;
            item.OriginalUrl = work.AuthorPageUrl ?? work.SourceUrl;
            item.FolderPath = authorFolder;
            item.UpdatedAt = DateTimeOffset.Now;

            NormalizeSortOrder();
            await SaveCoreAsync(cancellationToken);
            snapshot = Snapshot();
        }
        finally
        {
            _gate.Release();
        }

        HistoryChanged?.Invoke(this, snapshot);
    }

    public async Task RefreshAuthorStatsAsync(
        string platformId,
        string userId,
        string authorFolder,
        CancellationToken cancellationToken = default)
    {
        var stats = await Task.Run(() => CalculateFolderStats(authorFolder), cancellationToken);
        IReadOnlyList<DownloadHistoryItem>? snapshot = null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            var item = _items.FirstOrDefault(x =>
                PlatformMatches(x.Platform, platformId)
                && x.UserId.Equals(userId, StringComparison.Ordinal));
            if (item is null)
                return;

            item.FolderPath = authorFolder;
            item.ItemsCount = stats.ItemsCount;
            item.ItemsSize = stats.ItemsSize;
            await SaveCoreAsync(cancellationToken);
            snapshot = Snapshot();
        }
        finally
        {
            _gate.Release();
        }

        if (snapshot is not null)
            HistoryChanged?.Invoke(this, snapshot);
    }

    public async Task MoveAsync(
        int itemId,
        int targetIndex,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DownloadHistoryItem>? snapshot = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            var oldIndex = _items.FindIndex(x => x.Id == itemId);
            if (oldIndex < 0)
                return;

            targetIndex = Math.Clamp(targetIndex, 0, _items.Count - 1);
            if (oldIndex == targetIndex)
                return;

            var item = _items[oldIndex];
            _items.RemoveAt(oldIndex);
            _items.Insert(targetIndex, item);
            NormalizeSortOrder();
            await SaveCoreAsync(cancellationToken);
            snapshot = Snapshot();
        }
        finally
        {
            _gate.Release();
        }

        if (snapshot is not null)
            HistoryChanged?.Invoke(this, snapshot);
    }

    public async Task SetOrderAsync(
        IReadOnlyList<int> orderedIds,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DownloadHistoryItem>? snapshot = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            var byId = _items.ToDictionary(x => x.Id);
            var reordered = new List<DownloadHistoryItem>(_items.Count);
            foreach (var id in orderedIds)
            {
                if (byId.Remove(id, out var item))
                    reordered.Add(item);
            }

            reordered.AddRange(byId.Values.OrderBy(x => x.SortOrder));
            _items.Clear();
            _items.AddRange(reordered);
            NormalizeSortOrder();
            await SaveCoreAsync(cancellationToken);
            snapshot = Snapshot();
        }
        finally
        {
            _gate.Release();
        }

        if (snapshot is not null)
            HistoryChanged?.Invoke(this, snapshot);
    }

    public async Task RemoveAsync(
        int itemId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<DownloadHistoryItem>? snapshot = null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureLoadedCoreAsync(cancellationToken);
            var item = _items.FirstOrDefault(x => x.Id == itemId);
            if (item is null)
                return;

            _items.Remove(item);
            NormalizeSortOrder();
            await SaveCoreAsync(cancellationToken);
            snapshot = Snapshot();
        }
        finally
        {
            _gate.Release();
        }

        if (snapshot is not null)
            HistoryChanged?.Invoke(this, snapshot);
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
            return;

        _loaded = true;

        var sourcePath = _filePath;
        var isLegacyFile = false;
        if (!File.Exists(sourcePath))
        {
            sourcePath = _legacyFilePaths.FirstOrDefault(File.Exists) ?? string.Empty;
            isLegacyFile = !string.IsNullOrWhiteSpace(sourcePath);
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
            return;

        try
        {
            // 先记录旧文件是否包含 \uXXXX（包括 Emoji 代理对）；关闭读取流后再覆盖写回，
            // 避免 Windows 下替换一个仍处于打开状态的 History.json。
            var rewriteReadableUnicode = !isLegacyFile && ContainsEscapedUnicode(sourcePath);
            List<DownloadHistoryItem>? items;
            await using (var stream = File.OpenRead(sourcePath))
            {
                items = await JsonSerializer.DeserializeAsync<List<DownloadHistoryItem>>(
                    stream,
                    _jsonOptions,
                    cancellationToken);
            }

            if (items is not null)
            {
                // 旧文件首次迁移时按最后下载时间倒序；新 History.json 则保留用户拖动后的顺序。
                _items.AddRange(isLegacyFile
                    ? items.OrderByDescending(x => x.UpdatedAt).ThenByDescending(x => x.Id)
                    : items.OrderBy(x => x.SortOrder).ThenBy(x => x.Id));
            }

            NormalizeSortOrder();
            if (isLegacyFile || rewriteReadableUnicode)
            {
                try
                {
                    // 新版本首次读取旧转义格式时立即重写一次，中文和 Emoji 会恢复成可读文本。
                    await SaveCoreAsync(cancellationToken);
                }
                catch (IOException)
                {
                    // 文件被外部编辑器临时占用时仍允许程序正常加载，后续保存会再次重写。
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (JsonException)
        {
            var brokenPath = sourcePath + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(sourcePath, brokenPath, true);
            _items.Clear();
        }
    }

    private static bool ContainsEscapedUnicode(string path)
    {
        try
        {
            return File.ReadAllText(path).Contains("\\u", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        var tempPath = _filePath + ".tmp";

        // Serialize to text first so supplementary-plane Emoji can be written as real UTF-8
        // characters. Writing directly with Utf8JsonWriter leaves some Emoji as surrogate-pair
        // escapes, which is valid JSON but does not meet the readable History.json requirement.
        var json = JsonSerializer.Serialize(_items, _jsonOptions);
        json = RestoreReadableEmoji(json);

        await File.WriteAllTextAsync(tempPath, json, Utf8WithoutBom, cancellationToken);
        File.Move(tempPath, _filePath, true);
    }

    private static string RestoreReadableEmoji(string json)
    {
        if (string.IsNullOrEmpty(json) || !json.Contains("\\u", StringComparison.OrdinalIgnoreCase))
            return json;

        json = EscapedSurrogatePairRegex.Replace(json, static match =>
        {
            var high = (char)Convert.ToInt32(match.Groups["high"].Value, 16);
            var low = (char)Convert.ToInt32(match.Groups["low"].Value, 16);
            return char.ConvertFromUtf32(char.ConvertToUtf32(high, low));
        });

        // Preserve variation selectors, zero-width joiners, keycaps and BMP Emoji symbols too.
        // None of these characters can break a JSON string, unlike quote or backslash escapes.
        return EscapedEmojiBmpRegex.Replace(json, static match =>
        {
            var codePoint = Convert.ToInt32(match.Groups["code"].Value, 16);
            return char.ConvertFromUtf32(codePoint);
        });
    }

    private static (int ItemsCount, long ItemsSize) CalculateFolderStats(string authorFolder)
    {
        if (!Directory.Exists(authorFolder))
            return (0, 0);

        // 新版本不再为每个作品生成元数据 JSON，完成作品以 crawler-index.json 为准。
        // 仍兼容读取旧版本已经生成的作品 JSON，用于迁移期间恢复历史统计。
        var workIds = ReadUniqueWorkIdsFromIndex(authorFolder);
        var metadataFallbackCount = 0;
        long totalSize = 0;

        foreach (var path in Directory.EnumerateFiles(authorFolder, "*", SearchOption.AllDirectories))
        {
            try
            {
                var fileName = Path.GetFileName(path);
                var isPendingOrPartial = fileName.EndsWith(
                                             PersonDetectionQueueService.PendingSuffix,
                                             StringComparison.OrdinalIgnoreCase)
                                         || fileName.EndsWith(".part", StringComparison.OrdinalIgnoreCase);
                if (!isPendingOrPartial
                    && !fileName.Equals("crawler-index.json", StringComparison.OrdinalIgnoreCase))
                {
                    totalSize += new FileInfo(path).Length;
                }

                if (isPendingOrPartial
                    || !Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                    || fileName.Equals("crawler-index.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var metadataWorkId = TryReadWorkIdFromMetadata(path);
                if (!string.IsNullOrWhiteSpace(metadataWorkId))
                    workIds.Add(metadataWorkId);
                else
                    metadataFallbackCount++;
            }
            catch (IOException)
            {
                // 文件正在写入或暂时不可访问时，本次先跳过；任务结束/下次刷新会重新统计。
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var itemsCount = workIds.Count > 0 ? workIds.Count : metadataFallbackCount;
        return (itemsCount, totalSize);
    }

    private static HashSet<string> ReadUniqueWorkIdsFromIndex(string authorFolder)
    {
        var workIds = new HashSet<string>(StringComparer.Ordinal);
        var indexPath = Path.Combine(authorFolder, "crawler-index.json");
        if (!File.Exists(indexPath))
            return workIds;

        try
        {
            var keys = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(indexPath));
            if (keys is null)
                return workIds;

            var normalizedKeys = JsonDownloadIndex.NormalizeStoredKeys(keys, out var changed);
            if (changed)
                TryRewriteNormalizedIndex(indexPath, normalizedKeys);

            foreach (var key in normalizedKeys)
            {
                var workId = TryExtractWorkId(key);
                if (!string.IsNullOrWhiteSpace(workId))
                    workIds.Add(workId);
            }
        }
        catch
        {
            // 索引损坏时仍可由作品元数据恢复统计。
        }

        return workIds;
    }

    private static void TryRewriteNormalizedIndex(
        string indexPath,
        HashSet<string> normalizedKeys)
    {
        try
        {
            var tempPath = indexPath + ".tmp";
            File.WriteAllText(
                tempPath,
                JsonSerializer.Serialize(normalizedKeys, new JsonSerializerOptions
                {
                    WriteIndented = true
                }),
                Utf8WithoutBom);
            File.Move(tempPath, indexPath, true);
        }
        catch (IOException)
        {
            // 文件被下载线程占用时由下次历史刷新或采集流程继续迁移。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string? TryReadWorkIdFromMetadata(string metadataPath)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(metadataPath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.Equals("WorkId", StringComparison.OrdinalIgnoreCase))
                    continue;

                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
            }
        }
        catch (JsonException)
        {
            // 正在写入、旧格式或非作品元数据时按无 WorkId 处理。
        }
        catch (IOException)
        {
        }

        return null;
    }

    private static string? TryExtractWorkId(string key)
    {
        var parts = key.Split(':');

        // 历史 v2/v3/v4 键仍可用于统计；JsonDownloadIndex 首次读取后会迁移并写回。
        if (key.StartsWith("v2:", StringComparison.Ordinal)
            || key.StartsWith("v3-audio-repair:", StringComparison.Ordinal)
            || key.StartsWith("v4-person-filter:", StringComparison.Ordinal))
        {
            return parts.Length >= 4 ? parts[3] : null;
        }

        // 当前稳定格式：platform:authorId:workId:workId=...:cover=...
        return parts.Length >= 4
               && parts[3].StartsWith("workId=", StringComparison.OrdinalIgnoreCase)
            ? parts[2]
            : null;
    }

    private IReadOnlyList<DownloadHistoryItem> Snapshot()
        => _items
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .Select(Clone)
            .ToArray();

    private static DownloadHistoryItem Clone(DownloadHistoryItem item)
        => new()
        {
            Id = item.Id,
            Platform = item.Platform,
            HeadUrl = item.HeadUrl,
            UserId = item.UserId,
            UserName = item.UserName,
            OriginalUrl = item.OriginalUrl,
            UpdatedAt = item.UpdatedAt,
            IsChecked = item.IsChecked,
            ItemsCount = item.ItemsCount,
            ItemsSize = item.ItemsSize,
            FolderPath = item.FolderPath,
            SortOrder = item.SortOrder
        };

    private void NormalizeSortOrder()
    {
        for (var index = 0; index < _items.Count; index++)
            _items[index].SortOrder = index;
    }

    private static string NormalizePlatformName(string platformId)
        => platformId.Trim().ToLowerInvariant() switch
        {
            "douyin" => "Douyin",
            "tiktok" => "TikTok",
            "instagram" => "Instagram",
            "kuaishou" => "Kuaishou",
            "weibo" => "Weibo",
            "meipian" => "Meipian",
            _ => platformId
        };

    private static bool PlatformMatches(string storedPlatform, string platformId)
    {
        if (storedPlatform.Equals(platformId, StringComparison.OrdinalIgnoreCase))
            return true;

        return (storedPlatform.Equals("Douyin", StringComparison.OrdinalIgnoreCase)
                && platformId.Equals("douyin", StringComparison.OrdinalIgnoreCase))
               || (storedPlatform.Equals("Instagram", StringComparison.OrdinalIgnoreCase)
                   && platformId.Equals("instagram", StringComparison.OrdinalIgnoreCase))
               || (storedPlatform.Equals("Kuaishou", StringComparison.OrdinalIgnoreCase)
                   && platformId.Equals("kuaishou", StringComparison.OrdinalIgnoreCase))
               || (storedPlatform.Equals("Weibo", StringComparison.OrdinalIgnoreCase)
                   && platformId.Equals("weibo", StringComparison.OrdinalIgnoreCase))
               || (storedPlatform.Equals("Meipian", StringComparison.OrdinalIgnoreCase)
                   && platformId.Equals("meipian", StringComparison.OrdinalIgnoreCase));
    }
}
