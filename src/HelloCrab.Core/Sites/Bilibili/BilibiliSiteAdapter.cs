using HelloCrab.Core.Services.Localization;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.Bilibili;

/// <summary>
/// 哔哩哔哩网页版作者视频页适配器。
///
/// 作者作品列表来自 api.bilibili.com/x/space/wbi/arc/search；该接口只提供
/// BV 号、标题、封面等列表信息。真实最高画质 DASH 视频流与独立音频流位于
/// 每条视频详情页中的 window.__playinfo__.data.dash，下载后由下载服务调用
/// FFmpeg 无损封装合并。
/// </summary>
public sealed class BilibiliSiteAdapter : ISiteAdapter
{
    private const string PlayInfoMarker = "window.__playinfo__";
    private const string WorkListApiPath = "/x/space/wbi/arc/search";
    private const string ProfileApiPath = "/x/space/wbi/acc/info";
    private static readonly TimeSpan ProfileWaitTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, BilibiliProfile> _profiles =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BilibiliProfile>> _profileWaiters =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _profileWaitTimedOut =
        new(StringComparer.Ordinal);

    private static readonly HttpClient DetailHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromMinutes(2)
    };

    public string Id => "bilibili";
    public string DisplayName => RuntimeLocalization.Get("Platform.bilibili", "哔哩哔哩网页版");
    public string HomeUrl => "https://www.bilibili.com/";

    public bool CanHandlePage(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3
            || !long.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            return false;
        }

        return segments[1].Equals("upload", StringComparison.OrdinalIgnoreCase)
               && segments[2].Equals("video", StringComparison.OrdinalIgnoreCase);
    }

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
    {
        if (statusCode is < 200 or >= 300
            || !Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Host.Equals("api.bilibili.com", StringComparison.OrdinalIgnoreCase)
               && (uri.AbsolutePath.Equals(WorkListApiPath, StringComparison.OrdinalIgnoreCase)
                   || uri.AbsolutePath.Equals(ProfileApiPath, StringComparison.OrdinalIgnoreCase))
               && (resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase)
                   || resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase));
    }

    public bool TryHandleAuxiliaryResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody,
        out string? diagnostic)
    {
        diagnostic = null;
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("api.bilibili.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.Equals(ProfileApiPath, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var targetMid = TryReadProfileMid(pageUrl);
        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var root = document.RootElement;
            var code = ReadFirstInt64(root, "code");
            if (code != 0)
            {
                var message = ReadFirstString(root, "message") ?? RuntimeLocalization.Get("Common.UnknownError", "未知错误");
                diagnostic = RuntimeLocalization.Format("Bilibili.ProfileApiFailed", "哔哩哔哩作者资料接口返回失败：code={0}，{1}。", code, message);
                return true;
            }

            if (!TryGetObject(root, "data", out var data))
            {
                diagnostic = RuntimeLocalization.Get("Bilibili.ProfileNoData", "哔哩哔哩作者资料接口中没有 data 节点。");
                return true;
            }

            var responseMid = ReadFlexibleString(data, "mid")
                              ?? TryReadQueryParameter(responseUrl, "mid")
                              ?? targetMid;
            if (string.IsNullOrWhiteSpace(responseMid))
            {
                diagnostic = RuntimeLocalization.Get("Bilibili.ProfileNoUid", "哔哩哔哩作者资料接口中没有 UID。");
                return true;
            }

            if (!string.IsNullOrWhiteSpace(targetMid) && !SameId(responseMid, targetMid))
            {
                diagnostic = RuntimeLocalization.Format(
                    "Bilibili.ProfileOtherIgnored",
                    "已忽略其他哔哩哔哩作者资料：UID {0}，当前目标 UID {1}。",
                    responseMid, targetMid);
                return true;
            }

            var normalizedMid = NormalizeNumericId(responseMid);
            var profile = new BilibiliProfile(
                normalizedMid,
                ReadFirstString(data, "name")?.Trim(),
                NormalizeUrl(ReadFirstString(data, "face")));

            _profiles[normalizedMid] = profile;
            _profileWaitTimedOut.TryRemove(normalizedMid, out _);
            if (_profileWaiters.TryGetValue(normalizedMid, out var waiter))
                waiter.TrySetResult(profile);

            diagnostic = string.IsNullOrWhiteSpace(profile.FaceUrl)
                ? RuntimeLocalization.Format("Bilibili.ProfileNoAvatar", "已获取哔哩哔哩作者资料：{0}（UID {1}），但接口未返回头像。", profile.DisplayName, normalizedMid)
                : RuntimeLocalization.Format("Bilibili.ProfileAvatarLoaded", "已获取哔哩哔哩作者头像：{0}（UID {1}）。", profile.DisplayName, normalizedMid);
            return true;
        }
        catch (JsonException ex)
        {
            diagnostic = RuntimeLocalization.Format("Bilibili.ProfileParseFailed", "解析哔哩哔哩作者资料失败：{0}", ex.Message);
            return true;
        }
    }

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var responseUri)
            || !responseUri.AbsolutePath.Equals(WorkListApiPath, StringComparison.OrdinalIgnoreCase))
        {
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);
        }

        var targetMid = TryReadProfileMid(pageUrl);
        if (string.IsNullOrWhiteSpace(targetMid))
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                false,
                null,
                RuntimeLocalization.Get("Bilibili.Error.CannotReadUid", "无法从哔哩哔哩作者主页读取 UID。"));
        }

        targetMid = NormalizeNumericId(targetMid);
        _profiles.TryGetValue(targetMid, out var cachedProfile);

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        var code = ReadFirstInt64(root, "code");
        if (code != 0)
        {
            var message = ReadFirstString(root, "message") ?? RuntimeLocalization.Get("Common.UnknownError", "未知错误");
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                null,
                null,
                RuntimeLocalization.Format("Bilibili.WorkApiFailed", "哔哩哔哩作品接口返回失败：code={0}，{1}。", code, message));
        }

        if (!TryGetObject(root, "data", out var data)
            || !TryGetObject(data, "list", out var list)
            || !TryGetArray(list, "vlist", out var videoList))
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                null,
                null,
                RuntimeLocalization.Get("Bilibili.WorkNoList", "哔哩哔哩作品接口中没有找到 data.list.vlist。"));
        }

        var works = new List<WorkItem>();
        var rejectedCount = 0;
        foreach (var item in videoList.EnumerateArray())
        {
            var authorId = ReadFlexibleString(item, "mid");
            if (string.IsNullOrWhiteSpace(authorId))
                authorId = targetMid;

            if (!SameId(authorId, targetMid))
            {
                rejectedCount++;
                continue;
            }

            var bvid = ReadFirstString(item, "bvid")?.Trim();
            if (string.IsNullOrWhiteSpace(bvid))
                continue;

            var title = ReadFirstString(item, "title")?.Trim();
            var description = string.IsNullOrWhiteSpace(title)
                ? ReadFirstString(item, "description")?.Trim()
                : title;
            if (string.IsNullOrWhiteSpace(description))
                description = bvid;

            var authorName = cachedProfile?.Name;
            if (string.IsNullOrWhiteSpace(authorName))
                authorName = ReadFirstString(item, "author")?.Trim();
            if (string.IsNullOrWhiteSpace(authorName))
                authorName = RuntimeLocalization.Format("Bilibili.UserFallback", "哔哩哔哩用户 {0}", targetMid);

            var authorAvatarUrl = cachedProfile?.FaceUrl;
            var coverUrl = NormalizeUrl(ReadFirstString(item, "pic"));
            var assets = string.IsNullOrWhiteSpace(coverUrl)
                ? Array.Empty<MediaAsset>()
                : new[]
                {
                    new MediaAsset(
                        MediaAssetType.Cover,
                        0,
                        new[] { coverUrl })
                };

            var sourceUrl = $"https://www.bilibili.com/video/{Uri.EscapeDataString(bvid)}/";
            works.Add(new WorkItem(
                Id,
                bvid,
                targetMid,
                authorName,
                authorAvatarUrl,
                description,
                ReadFirstInt64(item, "created"),
                assets,
                sourceUrl)
            {
                AuthorPageUrl = BuildAuthorPageUrl(targetMid),
                MediaRefererUrl = sourceUrl
            });
        }

        var pageNumber = 1L;
        var pageSize = 40L;
        var totalCount = (long)works.Count;
        if (TryGetObject(data, "page", out var page))
        {
            pageNumber = Math.Max(1, ReadFirstInt64(page, "pn"));
            pageSize = Math.Max(1, ReadFirstInt64(page, "ps"));
            totalCount = Math.Max(0, ReadFirstInt64(page, "count"));
        }

        var hasMore = pageNumber * pageSize < totalCount;
        var nextPage = hasMore
            ? (pageNumber + 1).ToString(CultureInfo.InvariantCulture)
            : null;
        var diagnostic = RuntimeLocalization.Format("Bilibili.PageSummary", "哔哩哔哩第 {0} 页发现 {1} 个视频，总数 {2}；将逐个读取视频页 DASH 最高画质。", pageNumber, works.Count, totalCount);
        if (rejectedCount > 0)
            diagnostic += " " + RuntimeLocalization.Format("Bilibili.Filtered", "已过滤 {0} 个非目标作者条目。", rejectedCount);

        return new ParsedWorkBatch(
            works,
            hasMore,
            nextPage,
            diagnostic,
            rejectedCount)
        {
            TotalWorkCount = totalCount <= int.MaxValue ? (int)totalCount : int.MaxValue
        };
    }

    public bool TryCreateCursorRequest(
        BrowserRequestSnapshot previousRequest,
        string cursor,
        out BrowserPageRequest nextRequest)
        => CursorRequestRewriter.TryRewrite(
            previousRequest,
            cursor,
            new[] { "pn" },
            out nextRequest);

    public Task<WorkItem> EnrichWorkMetadataAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
        => EnrichAuthorProfileAsync(work, cancellationToken);

    public async Task<WorkItem?> ResolveWorkAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        work = await EnrichAuthorProfileAsync(work, cancellationToken);

        var html = await FetchVideoPageAsync(work, browser, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException(RuntimeLocalization.Get("Bilibili.Error.EmptyDetail", "哔哩哔哩视频详情返回了空文档。"));

        using var playInfo = ParsePlayInfo(html);
        if (!TryGetObject(playInfo.RootElement, "data", out var data))
            throw new InvalidOperationException(RuntimeLocalization.Get("Bilibili.Error.PlayInfoNoData", "window.__playinfo__ 中没有 data 节点。"));

        var resolvedAssets = new List<MediaAsset>();
        if (TryGetObject(data, "dash", out var dash))
        {
            var video = ParseBestDashVideo(dash);
            if (video is null)
                throw new InvalidOperationException(RuntimeLocalization.Get("Bilibili.Error.NoVideoDash", "DASH 数据中没有可用的视频流。"));

            resolvedAssets.Add(video);
            var audio = ParseBestDashAudio(dash)
                        ?? throw new InvalidOperationException(RuntimeLocalization.Get("Bilibili.Error.NoAudioDash", "DASH 数据中没有可用的音频流。"));
            resolvedAssets.Add(audio);
        }
        else
        {
            // 极少数旧视频或受限页面可能只返回 durl（已包含音频的视频文件）。
            var progressive = ParseProgressiveVideo(data);
            if (progressive is null)
                throw new InvalidOperationException(RuntimeLocalization.Get("Bilibili.Error.NoPlayableVideo", "视频页中既没有 DASH，也没有可用的 durl 视频流。"));
            resolvedAssets.Add(progressive);
        }

        var cover = work.Assets.FirstOrDefault(asset => asset.Type == MediaAssetType.Cover);
        if (cover is not null)
            resolvedAssets.Add(cover);

        return work with
        {
            Assets = resolvedAssets,
            MediaRefererUrl = work.SourceUrl
        };
    }

    public async Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        await browser.EvaluatePageAsync("""
            async () => {
                const root = document.scrollingElement || document.documentElement;
                const overlay = document.getElementById('__social_media_crawler_capture_lock__');

                const isVisible = element => {
                    if (!element) return false;
                    const style = getComputedStyle(element);
                    const rect = element.getBoundingClientRect();
                    return style.display !== 'none'
                        && style.visibility !== 'hidden'
                        && rect.width > 1
                        && rect.height > 1;
                };

                const isDisabled = element => {
                    const className = typeof element.className === 'string'
                        ? element.className.toLowerCase()
                        : '';
                    return element.disabled === true
                        || element.getAttribute('aria-disabled') === 'true'
                        || className.includes('disabled');
                };

                const findNextButton = () => {
                    const candidates = [...document.querySelectorAll('button, a, [role="button"]')]
                        .filter(isVisible)
                        .filter(element => !isDisabled(element));

                    return candidates.find(element => {
                        const text = (element.innerText || element.textContent || '').replace(/\s+/g, '');
                        const aria = (element.getAttribute('aria-label') || '').replace(/\s+/g, '');
                        const title = (element.getAttribute('title') || '').replace(/\s+/g, '');
                        return text === '下一页'
                            || text.includes('下一页')
                            || aria.includes('下一页')
                            || title.includes('下一页');
                    }) || candidates.find(element => {
                        const className = typeof element.className === 'string'
                            ? element.className.toLowerCase()
                            : '';
                        return className.includes('pager-next')
                            || className.includes('pagination-next')
                            || className.includes('pagenation--btn-side')
                               && (element.parentElement?.lastElementChild === element);
                    });
                };

                // 先把分页控件带入可视区，等待 B 站虚拟列表/底部组件完成渲染。
                window.scrollTo({ top: Math.max(0, root.scrollHeight), behavior: 'auto' });
                root.scrollTop = Math.max(0, root.scrollHeight);
                window.dispatchEvent(new Event('scroll'));
                await new Promise(resolve => setTimeout(resolve, 350));

                const next = findNextButton();
                if (!next) return { clicked: false, reason: 'next-not-found' };

                next.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'auto' });
                window.__smcAllowAutomationInput = true;
                if (overlay) overlay.style.pointerEvents = 'none';
                try {
                    next.click();
                } finally {
                    setTimeout(() => {
                        window.__smcAllowAutomationInput = false;
                        const currentOverlay = document.getElementById('__social_media_crawler_capture_lock__');
                        if (currentOverlay) currentOverlay.style.pointerEvents = 'auto';
                    }, 450);
                }

                return { clicked: true };
            }
            """, cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const root = document.scrollingElement || document.documentElement;
                return {
                    scrollY: Math.max(window.scrollY || 0, root.scrollTop || 0),
                    viewportHeight: window.innerHeight || root.clientHeight || 0,
                    documentHeight: Math.max(
                        root.scrollHeight || 0,
                        document.documentElement?.scrollHeight || 0,
                        document.body?.scrollHeight || 0),
                    containerName: 'document',
                    workItemCount: document.querySelectorAll(
                        'a[href*="/video/BV"], [class*="video-card" i], [class*="small-item" i]'
                    ).length
                };
            }
            """, cancellationToken);

        return new PageScrollState(
            ReadDouble(result, "scrollY"),
            ReadDouble(result, "viewportHeight"),
            ReadDouble(result, "documentHeight"),
            ReadFirstString(result, "containerName") ?? "document",
            (int)Math.Clamp(ReadFirstInt64(result, "workItemCount"), 0, int.MaxValue));
    }

    private async Task<WorkItem> EnrichAuthorProfileAsync(
        WorkItem work,
        CancellationToken cancellationToken)
    {
        var authorId = NormalizeNumericId(work.AuthorId);
        if (_profiles.TryGetValue(authorId, out var cached))
            return ApplyProfile(work, cached);

        // acc/info 与 arc/search 常常几乎同时返回。等待时间只发生在同一作者首个
        // 尚未拿到资料的作品上；超时后做标记，后续作品不会重复等待。
        if (_profileWaitTimedOut.ContainsKey(authorId))
            return work;

        var waiter = _profileWaiters.GetOrAdd(
            authorId,
            static _ => new TaskCompletionSource<BilibiliProfile>(
                TaskCreationOptions.RunContinuationsAsynchronously));

        // 避免“检查缓存”和“建立等待器”之间恰好收到资料造成漏唤醒。
        if (_profiles.TryGetValue(authorId, out cached))
        {
            waiter.TrySetResult(cached);
            return ApplyProfile(work, cached);
        }

        try
        {
            var profile = await waiter.Task.WaitAsync(ProfileWaitTimeout, cancellationToken);
            return ApplyProfile(work, profile);
        }
        catch (TimeoutException)
        {
            _profileWaitTimedOut.TryAdd(authorId, 0);
            return work;
        }
    }

    private static WorkItem ApplyProfile(WorkItem work, BilibiliProfile profile)
        => work with
        {
            AuthorName = string.IsNullOrWhiteSpace(profile.Name)
                ? work.AuthorName
                : profile.Name,
            AuthorAvatarUrl = string.IsNullOrWhiteSpace(profile.FaceUrl)
                ? work.AuthorAvatarUrl
                : profile.FaceUrl
        };

    private static async Task<string> FetchVideoPageAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        var context = await browser.GetDownloadContextAsync(cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Get, work.SourceUrl);
        request.Headers.UserAgent.ParseAdd(context.UserAgent);
        request.Headers.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        request.Headers.AcceptLanguage.ParseAdd("zh-CN,zh;q=0.9,en;q=0.7");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        request.Headers.Referrer = Uri.TryCreate(
            work.AuthorPageUrl,
            UriKind.Absolute,
            out var referer)
            ? referer
            : null;

        var cookies = await context.GetCookiesAsync(work.SourceUrl, cancellationToken);
        if (cookies.Count > 0)
        {
            request.Headers.TryAddWithoutValidation(
                "Cookie",
                string.Join("; ", cookies.Select(cookie => $"{cookie.Name}={cookie.Value}")));
        }

        using var response = await DetailHttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(RuntimeLocalization.Format(
                "Bilibili.Error.VideoHttp",
                "读取视频页失败：HTTP {0} {1}，{2}",
                (int)response.StatusCode, response.ReasonPhrase, TrimForMessage(html)));
        }

        return html;
    }

    private static JsonDocument ParsePlayInfo(string html)
    {
        var markerIndex = html.IndexOf(PlayInfoMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException(
                RuntimeLocalization.Get("Bilibili.Error.PlayInfoMissing", "视频页中没有找到 window.__playinfo__。请确认页面可正常播放且登录状态有效。"));
        }

        var equalsIndex = html.IndexOf('=', markerIndex + PlayInfoMarker.Length);
        if (equalsIndex < 0)
            throw new InvalidOperationException(RuntimeLocalization.Get("Bilibili.Error.PlayInfoIncomplete", "window.__playinfo__ 格式不完整。"));

        var jsonStart = html.IndexOf('{', equalsIndex + 1);
        if (jsonStart < 0)
            throw new InvalidOperationException(RuntimeLocalization.Get("Bilibili.Error.PlayInfoNoJson", "window.__playinfo__ 中没有 JSON 对象。"));

        var json = ExtractBalancedJsonObject(html, jsonStart);
        return JsonDocument.Parse(json);
    }

    private static string ExtractBalancedJsonObject(string text, int startIndex)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var index = startIndex; index < text.Length; index++)
        {
            var current = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                    inString = false;
                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current != '}')
                continue;

            depth--;
            if (depth == 0)
                return text[startIndex..(index + 1)];
        }

        throw new InvalidOperationException(RuntimeLocalization.Get("Bilibili.Error.PlayInfoUnclosed", "window.__playinfo__ JSON 对象没有正确结束。"));
    }

    private static MediaAsset? ParseBestDashVideo(JsonElement dash)
    {
        if (!TryGetArray(dash, "video", out var videos))
            return null;

        JsonElement? best = null;
        foreach (var candidate in videos.EnumerateArray())
        {
            if (ReadCandidateUrls(candidate).Count == 0)
                continue;

            if (!best.HasValue || CompareVideoQuality(candidate, best.Value) > 0)
                best = candidate;
        }

        if (!best.HasValue)
            return null;

        var selected = best.Value;
        return new MediaAsset(
            MediaAssetType.Video,
            0,
            ReadCandidateUrls(selected),
            ReadFirstInt64(selected, "bandwidth"),
            (int)Math.Clamp(ReadFirstInt64(selected, "width"), 0, int.MaxValue),
            (int)Math.Clamp(ReadFirstInt64(selected, "height"), 0, int.MaxValue),
            ReadFirstString(selected, "codecs", "codec"));
    }

    private static MediaAsset? ParseBestDashAudio(JsonElement dash)
    {
        if (!TryGetArray(dash, "audio", out var audios))
            return null;

        JsonElement? best = null;
        foreach (var candidate in audios.EnumerateArray())
        {
            if (ReadCandidateUrls(candidate).Count == 0)
                continue;

            if (!best.HasValue
                || ReadFirstInt64(candidate, "bandwidth")
                   > ReadFirstInt64(best.Value, "bandwidth"))
            {
                best = candidate;
            }
        }

        if (!best.HasValue)
            return null;

        var selected = best.Value;
        return new MediaAsset(
            MediaAssetType.Music,
            0,
            ReadCandidateUrls(selected),
            ReadFirstInt64(selected, "bandwidth"),
            Codec: ReadFirstString(selected, "codecs", "codec"));
    }

    private static MediaAsset? ParseProgressiveVideo(JsonElement data)
    {
        if (!TryGetArray(data, "durl", out var durl) || durl.GetArrayLength() != 1)
            return null;

        var urls = new List<string>();
        foreach (var segment in durl.EnumerateArray())
        {
            var url = NormalizeUrl(ReadFirstString(segment, "url"));
            if (!string.IsNullOrWhiteSpace(url))
                urls.Add(url);

            if (!TryGetArray(segment, "backup_url", out var backups))
                continue;
            foreach (var backup in backups.EnumerateArray())
            {
                if (backup.ValueKind != JsonValueKind.String)
                    continue;
                var normalized = NormalizeUrl(backup.GetString());
                if (!string.IsNullOrWhiteSpace(normalized))
                    urls.Add(normalized);
            }
        }

        var candidates = urls.Distinct(StringComparer.Ordinal).ToArray();
        return candidates.Length == 0
            ? null
            : new MediaAsset(MediaAssetType.Video, 0, candidates);
    }

    private static int CompareVideoQuality(JsonElement left, JsonElement right)
    {
        var leftPixels = ReadFirstInt64(left, "width") * ReadFirstInt64(left, "height");
        var rightPixels = ReadFirstInt64(right, "width") * ReadFirstInt64(right, "height");
        var comparison = leftPixels.CompareTo(rightPixels);
        if (comparison != 0)
            return comparison;

        comparison = ReadFrameRate(left).CompareTo(ReadFrameRate(right));
        if (comparison != 0)
            return comparison;

        comparison = ReadFirstInt64(left, "id", "quality")
            .CompareTo(ReadFirstInt64(right, "id", "quality"));
        if (comparison != 0)
            return comparison;

        return ReadFirstInt64(left, "bandwidth")
            .CompareTo(ReadFirstInt64(right, "bandwidth"));
    }

    private static double ReadFrameRate(JsonElement element)
    {
        var value = ReadFirstString(element, "frameRate", "frame_rate");
        if (string.IsNullOrWhiteSpace(value))
            return 0;

        var parts = value.Split('/', 2, StringSplitOptions.TrimEntries);
        if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator))
            return 0;
        if (parts.Length == 1)
            return numerator;
        if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            || denominator == 0)
        {
            return numerator;
        }

        return numerator / denominator;
    }

    private static IReadOnlyList<string> ReadCandidateUrls(JsonElement element)
    {
        var urls = new List<string>();
        AddUrl(urls, ReadFirstString(element, "baseUrl", "base_url"));

        if (TryGetArray(element, "backupUrl", out var camelBackups)
            || TryGetArray(element, "backup_url", out camelBackups))
        {
            foreach (var item in camelBackups.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    AddUrl(urls, item.GetString());
            }
        }

        return urls.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static void AddUrl(ICollection<string> urls, string? rawUrl)
    {
        var normalized = NormalizeUrl(rawUrl);
        if (!string.IsNullOrWhiteSpace(normalized))
            urls.Add(normalized);
    }

    private static string NormalizeNumericId(string value)
        => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : value.Trim();

    private static string? TryReadQueryParameter(string url, string name)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;

        var query = uri.Query;
        if (query.StartsWith("?", StringComparison.Ordinal))
            query = query[1..];

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var rawName = separator >= 0 ? pair[..separator] : pair;
            if (!Uri.UnescapeDataString(rawName).Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var rawValue = separator >= 0 ? pair[(separator + 1)..] : string.Empty;
            return Uri.UnescapeDataString(rawValue.Replace('+', ' '));
        }

        return null;
    }

    private static string? TryReadProfileMid(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            || !uri.Host.Equals("space.bilibili.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var first = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return long.TryParse(first, NumberStyles.None, CultureInfo.InvariantCulture, out var mid)
            ? mid.ToString(CultureInfo.InvariantCulture)
            : null;
    }

    private static string BuildAuthorPageUrl(string mid)
        => $"https://space.bilibili.com/{Uri.EscapeDataString(mid)}/upload/video";

    private static bool SameId(string? left, string? right)
    {
        if (long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber)
            && long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber))
        {
            return leftNumber == rightNumber;
        }

        return string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = WebUtility.HtmlDecode(value.Trim())
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("\\/", "/", StringComparison.Ordinal);
        if (normalized.StartsWith("//", StringComparison.Ordinal))
            normalized = "https:" + normalized;
        return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : null;
    }

    private static string? ReadFlexibleString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetObject(JsonElement element, string propertyName, out JsonElement value)
        => TryGetProperty(element, propertyName, out value)
           && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(JsonElement element, string propertyName, out JsonElement value)
        => TryGetProperty(element, propertyName, out value)
           && value.ValueKind == JsonValueKind.Array;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        value = default;
        if (element.ValueKind != JsonValueKind.Object)
            return false;

        if (element.TryGetProperty(propertyName, out value))
            return true;

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        return false;
    }

    private static string? ReadFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
                return value.GetString();
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }

        return null;
    }

    private static long ReadFirstInt64(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(
                    value.GetString(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out number))
            {
                return number;
            }
        }

        return 0;
    }

    private static double ReadDouble(JsonElement element, string propertyName, double fallback = 0)
    {
        if (!TryGetProperty(element, propertyName, out var value))
            return fallback;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;
        return value.ValueKind == JsonValueKind.String
               && double.TryParse(
                   value.GetString(),
                   NumberStyles.Float,
                   CultureInfo.InvariantCulture,
                   out number)
            ? number
            : fallback;
    }

    private static string TrimForMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return RuntimeLocalization.Get("Common.NoBody", "未返回正文");

        var normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return normalized.Length <= 180 ? normalized : normalized[..180];
    }

    private sealed record BilibiliProfile(
        string Mid,
        string? Name,
        string? FaceUrl)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Name)
            ? RuntimeLocalization.Format("Bilibili.UserFallback", "哔哩哔哩用户 {0}", Mid)
            : Name!;
    }
}
