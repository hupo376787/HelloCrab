using HelloCrab.Core.Services.Localization;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.Pinterest;

/// <summary>
/// Pinterest 网页版作者主页和画板页适配器。
///
/// Pinterest 的瀑布流数据通常来自 /resource/*Resource/get/，返回结构以
/// resource_response.data.results（或 data 数组）承载 Pin，并用 bookmark 翻页。
/// 单个 Pin 的最高质量图片位于 images 的 orig/1200x/736x 等节点；视频位于
/// videos.video_list 或故事 Pin 的 pages/blocks 中。本适配器不自行构造签名请求，
/// 只消费浏览器已经成功收到的 Fetch/XHR 响应，因此可复用当前登录态和风控参数。
/// </summary>
public sealed partial class PinterestSiteAdapter : ISiteAdapter
{
    private static readonly HashSet<string> ReservedRootSegments = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "business", "categories", "developers", "explore", "help", "ideas",
        "login", "messages", "notifications", "oauth", "pin", "policy", "privacy",
        "resource", "search", "settings", "shopping", "source", "terms", "today",
        "topics", "videos", "_tools"
    };

    private static readonly HashSet<string> SupportedResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserPinsResource",
        "UserCreatedPinsResource",
        "UserPublishedPinsResource",
        "ProfilePinsResource",
        "ProfileCreatedPinsResource",
        "ProfileFeedResource",
        "UserActivityPinsResource",
        "BoardFeedResource",
        "BoardSectionPinsResource"
    };

    private static readonly HashSet<string> AuxiliaryResourceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "UserResource",
        "UserProfileResource",
        "BoardResource"
    };

    private readonly ConcurrentDictionary<string, PinterestProfile> _profiles =
        new(StringComparer.OrdinalIgnoreCase);

    public string Id => "pinterest";
    public string DisplayName => "Pinterest";
    public string HomeUrl => "https://www.pinterest.com/";

    public bool CanHandlePage(string pageUrl)
    {
        var context = ReadPageContext(pageUrl);
        return context is not null;
    }

    public bool IsTargetResponse(
        string responseUrl,
        string resourceType,
        int statusCode,
        string? requestBody)
    {
        if (statusCode is < 200 or >= 300
            || (!resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
                && !resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase))
            || !Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri)
            || !IsPinterestHost(uri.Host))
        {
            return false;
        }

        var resourceName = TryReadResourceName(uri.AbsolutePath);
        return resourceName is not null
               && (SupportedResourceNames.Contains(resourceName)
                   || AuxiliaryResourceNames.Contains(resourceName));
    }

    public bool TryHandleAuxiliaryResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody,
        out string? diagnostic)
    {
        diagnostic = null;
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri))
            return false;

        var resourceName = TryReadResourceName(uri.AbsolutePath);
        if (resourceName is null || !AuxiliaryResourceNames.Contains(resourceName))
            return false;

        try
        {
            using var document = JsonDocument.Parse(responseJson);
            var pageContext = ReadPageContext(pageUrl);
            if (!TryGetResourceData(document.RootElement, out var data))
            {
                diagnostic = RuntimeLocalization.Format("Pinterest.NoResourceData", "Pinterest {0} 中没有 resource_response.data。", resourceName);
                return true;
            }

            if (resourceName.Equals("BoardResource", StringComparison.OrdinalIgnoreCase))
            {
                var board = UnwrapSingleDataObject(data);
                if (TryGetObject(board, "owner", out var owner))
                {
                    CacheProfile(owner, pageContext?.Username);
                    diagnostic = RuntimeLocalization.Get("Pinterest.ProfileBoardLoaded", "已获取 Pinterest 画板作者资料。");
                }
                return true;
            }

            var user = UnwrapSingleDataObject(data);
            var profile = ParseProfile(user, pageContext?.Username);
            if (profile is null)
            {
                diagnostic = RuntimeLocalization.Get("Pinterest.ProfileUnrecognized", "Pinterest 作者资料响应中没有可识别的用户信息。");
                return true;
            }

            if (pageContext is not null
                && !SameUsername(profile.Username, pageContext.Username))
            {
                // UserResource 可能是当前登录账号，而不是正在采集的主页作者。
                // 忽略它，避免历史头像短暂被登录用户覆盖。
                return true;
            }

            CacheProfile(profile);
            diagnostic = string.IsNullOrWhiteSpace(profile.AvatarUrl)
                ? RuntimeLocalization.Format("Pinterest.ProfileNoAvatar", "已获取 Pinterest 作者资料：{0}，但没有头像。", profile.DisplayName)
                : RuntimeLocalization.Format("Pinterest.ProfileAvatarLoaded", "已获取 Pinterest 作者头像：{0}。", profile.DisplayName);
            return true;
        }
        catch (JsonException ex)
        {
            diagnostic = RuntimeLocalization.Format("Pinterest.ProfileParseFailed", "解析 Pinterest 作者资料失败：{0}", ex.Message);
            return true;
        }
    }

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        var pageContext = ReadPageContext(pageUrl);
        if (pageContext is null)
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                false,
                null,
                RuntimeLocalization.Get("Pinterest.Error.AuthorUnknown", "无法从当前 Pinterest 页面识别作者用户名。请打开作者主页、Created 页面或具体画板页。"));
        }

        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        if (!TryGetResourceData(root, out var data))
        {
            return new ParsedWorkBatch(
                Array.Empty<WorkItem>(),
                null,
                null,
                RuntimeLocalization.Get("Pinterest.Error.NoResponseData", "Pinterest 接口响应中没有 resource_response.data。可能是登录页、风控响应或非作品接口。"));
        }

        var works = new List<WorkItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rejected = 0;
        foreach (var pin in EnumeratePinObjects(data))
        {
            var work = ParsePin(pin, pageContext, out var wasRejected);
            if (wasRejected)
                rejected++;
            if (work is not null && seen.Add(work.WorkId))
                works.Add(work);
        }

        var bookmarkState = ReadBookmarkState(root);
        var diagnostic = RuntimeLocalization.Format("Pinterest.PageSummary", "Pinterest 本页解析到 {0} 个 Pin。", works.Count);
        if (rejected > 0)
            diagnostic += " " + RuntimeLocalization.Format("Pinterest.Filtered", "已过滤 {0} 个与当前作者或画板不一致的条目。", rejected);
        if (bookmarkState.HasMore == true)
            diagnostic += " " + RuntimeLocalization.Get("Pinterest.MoreBookmark", "页面仍有后续 bookmark，将继续向下滚动。");

        return new ParsedWorkBatch(
            works,
            bookmarkState.HasMore,
            bookmarkState.Bookmark,
            diagnostic,
            rejected)
        {
            TotalWorkCount = HelloCrab.Core.Utilities.AuthorWorkCountReader.TryRead(Id, root)
        };
    }

    public bool TryCreateCursorRequest(
        BrowserRequestSnapshot previousRequest,
        string cursor,
        out BrowserPageRequest nextRequest)
        => CursorRequestRewriter.TryRewrite(
            previousRequest,
            cursor,
            new[] { "bookmark", "bookmarks", "next_bookmark", "nextBookmark" },
            out nextRequest);

    public Task<WorkItem> EnrichWorkMetadataAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var pageContext = ReadPageContext(work.AuthorPageUrl ?? work.SourceUrl);
        PinterestProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(work.AuthorId))
            _profiles.TryGetValue(work.AuthorId, out profile);
        if (profile is null && !string.IsNullOrWhiteSpace(pageContext?.Username))
            _profiles.TryGetValue(pageContext.Username, out profile);
        if (profile is null)
            return Task.FromResult(work);

        return Task.FromResult(work with
        {
            // Pinterest 的用户数字 ID 在不同资源中并非总是同时返回。采集会话使用
            // 页面 username 作为稳定 UID，避免作者资料稍晚到达时发生 UID 切换。
            AuthorName = string.IsNullOrWhiteSpace(profile.Name) ? work.AuthorName : profile.Name,
            AuthorAvatarUrl = string.IsNullOrWhiteSpace(profile.AvatarUrl)
                ? work.AuthorAvatarUrl
                : profile.AvatarUrl
        });
    }

    public async Task<WorkItem?> ResolveWorkAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 作者瀑布流只用于收集 Pin ID。每个 Pin 都读取公开 /pin/{id}/ 文档，
        // 解析 __PWS_INITIAL_PROPS__ 中的完整图片、故事页和视频清晰度数据。
        if (!work.RequiresDetailResolution)
            return work;

        var response = await browser.EvaluatePageAsync(
            """
            async pinId => {
                const id = String(pinId || '').trim();
                if (!id)
                    throw new Error('Pinterest Pin ID 为空。');

                const sourceUrl = `/pin/${encodeURIComponent(id)}/`;
                try {
                    const result = await fetch(sourceUrl, {
                        method: 'GET',
                        credentials: 'include',
                        redirect: 'follow',
                        cache: 'no-store',
                        headers: {
                            'Accept': 'text/html,application/xhtml+xml,application/json;q=0.9,*/*;q=0.8'
                        }
                    });
                    const html = await result.text();
                    if (!result.ok) {
                        return {
                            kind: 'none',
                            error: `Pin 页面 HTTP ${result.status}: ${html.slice(0, 160)}`
                        };
                    }

                    const doc = new DOMParser().parseFromString(html, 'text/html');
                    const script = doc.querySelector('script#__PWS_INITIAL_PROPS__');
                    if (!script) {
                        return {
                            kind: 'none',
                            error: 'Pin 页面中没有 __PWS_INITIAL_PROPS__。'
                        };
                    }

                    let initialProps;
                    try {
                        initialProps = JSON.parse(script.textContent || '{}');
                    } catch (error) {
                        return {
                            kind: 'none',
                            error: `解析 __PWS_INITIAL_PROPS__ 失败：${error?.message || error}`
                        };
                    }

                    const readId = node => String(
                        node?.id ?? node?.pin_id ?? node?.pinId ?? '');
                    let pin = initialProps?.initialReduxState?.pins?.[id] ?? null;

                    // 部分页面只把 Pin 放在 resources.PinResource 的 data 中。
                    if (!pin) {
                        const resources =
                            initialProps?.initialReduxState?.resources?.PinResource;
                        if (resources && typeof resources === 'object') {
                            for (const entry of Object.values(resources)) {
                                const candidate =
                                    entry?.data ?? entry?.resource_response?.data ?? entry;
                                if (readId(candidate) === id) {
                                    pin = candidate;
                                    break;
                                }
                            }
                        }
                    }

                    // 兼容 Pinterest 后续调整 Redux 键名，但仍坚持只解析
                    // __PWS_INITIAL_PROPS__，不再伪造 PinResource 请求。
                    if (!pin) {
                        let visited = 0;
                        const findPin = (node, depth = 0) => {
                            if (!node || depth > 24 || visited++ > 150000)
                                return null;
                            if (Array.isArray(node)) {
                                for (const item of node) {
                                    const found = findPin(item, depth + 1);
                                    if (found) return found;
                                }
                                return null;
                            }
                            if (typeof node !== 'object')
                                return null;
                            if (readId(node) === id)
                                return node;
                            for (const value of Object.values(node)) {
                                const found = findPin(value, depth + 1);
                                if (found) return found;
                            }
                            return null;
                        };
                        pin = findPin(initialProps);
                    }

                    if (!pin) {
                        return {
                            kind: 'none',
                            error: `__PWS_INITIAL_PROPS__ 中没有找到 Pin ${id}。`
                        };
                    }

                    // 显式收集 blocks[].video.video_list、page.video.video_list
                    // 以及普通 Pin 的 videos.video_list。把结果放入一个附加字段，
                    // C# 端仍会按宽高、码率选择最佳版本。
                    const resolvedVideoList = {};
                    const seenUrls = new Set();
                    let videoIndex = 0;
                    let visitedMedia = 0;
                    const collectVideoLists = (node, depth = 0) => {
                        if (!node || depth > 24 || visitedMedia++ > 150000)
                            return;
                        if (Array.isArray(node)) {
                            node.forEach(item => collectVideoLists(item, depth + 1));
                            return;
                        }
                        if (typeof node !== 'object')
                            return;

                        const list = node.video_list;
                        if (list && typeof list === 'object') {
                            for (const [name, rendition] of Object.entries(list)) {
                                if (!rendition || typeof rendition !== 'object')
                                    continue;
                                const url = String(
                                    rendition.url ??
                                    rendition.src ??
                                    rendition.play_url ??
                                    rendition.playUrl ??
                                    '');
                                if (!/^https?:\/\//i.test(url) || seenUrls.has(url))
                                    continue;
                                if (!/\.m3u8(?:$|\?)/i.test(url)
                                    && !/\.(?:mp4|mov)(?:$|\?)/i.test(url)) {
                                    continue;
                                }

                                seenUrls.add(url);
                                resolvedVideoList[
                                    `${name || 'video'}_${videoIndex++}`
                                ] = {
                                    url,
                                    width: Number(rendition.width ?? rendition.Width ?? 0) || 0,
                                    height: Number(rendition.height ?? rendition.Height ?? 0) || 0,
                                    bitrate: Number(
                                        rendition.bitrate ??
                                        rendition.bit_rate ??
                                        rendition.Bitrate ??
                                        0) || 0,
                                    file_size: Number(
                                        rendition.file_size ??
                                        rendition.filesize ??
                                        rendition.size ??
                                        0) || 0,
                                    duration: Number(rendition.duration ?? 0) || 0,
                                    codec: String(
                                        rendition.codec ??
                                        rendition.codec_name ??
                                        rendition.format ??
                                        '')
                                };
                            }
                        }

                        for (const value of Object.values(node))
                            collectVideoLists(value, depth + 1);
                    };
                    collectVideoLists(pin);

                    if (Object.keys(resolvedVideoList).length > 0) {
                        pin = {
                            ...pin,
                            hello_crab_resolved_video: {
                                video_list: resolvedVideoList
                            }
                        };
                    }

                    return {
                        kind: 'pin',
                        payload: pin,
                        resolvedVideoCount: Object.keys(resolvedVideoList).length
                    };
                } catch (error) {
                    return {
                        kind: 'none',
                        error: `读取 Pin 文档失败：${error?.message || error}`
                    };
                }
            }
            """,
            work.WorkId,
            cancellationToken);

        if (!TryGetProperty(response, "payload", out var pin))
        {
            // 文档或 __PWS_INITIAL_PROPS__ 解析失败时不写完成索引，
            // 下次采集仍会重新请求该 Pin。
            var error = ReadString(response, "error");
            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException(error);
            return null;
        }

        var detailId = ReadFlexibleString(pin, "id", "pin_id", "pinId");
        if (!string.IsNullOrWhiteSpace(detailId)
            && !detailId.Equals(work.WorkId, StringComparison.Ordinal))
        {
            return null;
        }

        var assets = ParsePrimaryAssets(pin);
        if (!assets.Any(asset =>
                asset.Type is MediaAssetType.Video or MediaAssetType.Image))
        {
            return null;
        }

        var cover = ParseBestImage(pin, 0, MediaAssetType.Cover);
        if (assets.Any(asset => asset.Type == MediaAssetType.Video)
            && cover is not null
            && !assets.Any(asset => asset.Type == MediaAssetType.Cover))
        {
            assets.Add(cover);
        }

        var authorName = work.AuthorName;
        var authorAvatar = work.AuthorAvatarUrl;
        var pageContext = ReadPageContext(work.AuthorPageUrl ?? work.SourceUrl);
        var creator = TryGetPinCreator(pin);
        if (creator.ValueKind == JsonValueKind.Object)
        {
            var detailUsername = ReadFlexibleString(creator, "username", "user_name");
            if (string.IsNullOrWhiteSpace(detailUsername)
                || pageContext is null
                || SameUsername(detailUsername, pageContext.Username))
            {
                authorName = ReadFlexibleString(
                                 creator,
                                 "full_name",
                                 "fullName",
                                 "business_name",
                                 "name",
                                 "username")
                             ?? authorName;
                authorAvatar = ParseAvatar(creator) ?? authorAvatar;
            }
        }

        var description = ReadFlexibleString(
                              pin,
                              "title",
                              "grid_title",
                              "description",
                              "closeup_description",
                              "alt_text",
                              "seo_description")
                          ?? work.Description;
        description = StripHtml(description).Trim();
        if (string.IsNullOrWhiteSpace(description))
            description = work.Description;

        var createTime = ReadCreateTime(pin);
        if (createTime <= 0)
            createTime = work.CreateTime;

        return work with
        {
            AuthorName = authorName,
            AuthorAvatarUrl = authorAvatar,
            Description = description,
            CreateTime = createTime,
            Assets = assets,
            MediaRefererUrl = work.SourceUrl,
            RequiresDetailResolution = false
        };
    }

    public async Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const pinSelector = [
                    'a[href*="/pin/"]',
                    '[data-test-id="pin"]',
                    '[data-grid-item]',
                    '[data-test-id*="pin" i]'
                ].join(',');
                const root = document.scrollingElement || document.documentElement;
                const candidates = [{ el: root, isRoot: true }];
                for (const el of document.querySelectorAll('body *')) {
                    const style = getComputedStyle(el);
                    if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 100) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 260 || rect.height < 240) continue;
                    if (style.display === 'none' || style.visibility === 'hidden') continue;
                    candidates.push({ el, isRoot: false });
                }

                let selected = candidates[0];
                let bestScore = -1;
                for (const candidate of candidates) {
                    const el = candidate.el;
                    const range = Math.max(0, el.scrollHeight - el.clientHeight);
                    if (range < 80) continue;
                    const count = candidate.isRoot
                        ? document.querySelectorAll(pinSelector).length
                        : el.querySelectorAll(pinSelector).length;
                    const score = count * 1000000 + range + el.clientHeight;
                    if (score > bestScore) {
                        selected = candidate;
                        bestScore = score;
                    }
                }

                const el = selected.el;
                const pins = [...document.querySelectorAll(pinSelector)].filter(node => {
                    const rect = node.getBoundingClientRect();
                    return rect.width > 20 && rect.height > 20;
                });
                pins.at(-1)?.scrollIntoView({ block: 'end', inline: 'nearest', behavior: 'auto' });

                const before = selected.isRoot ? window.scrollY : el.scrollTop;
                const viewport = selected.isRoot ? window.innerHeight : el.clientHeight;
                const max = Math.max(0, el.scrollHeight - viewport);
                let target = Math.min(max, before + Math.max(800, viewport * 0.9));
                if (max - target < Math.max(180, viewport * 0.3)) target = max;

                if (selected.isRoot) {
                    window.scrollTo({ top: target, behavior: 'auto' });
                    el.scrollTop = target;
                    window.dispatchEvent(new Event('scroll'));
                    document.dispatchEvent(new Event('scroll', { bubbles: true }));
                } else {
                    if (!el.hasAttribute('tabindex')) el.setAttribute('tabindex', '-1');
                    try { el.focus({ preventScroll: true }); } catch { }
                    el.scrollTo({ top: target, behavior: 'auto' });
                    el.scrollTop = target;
                    el.dispatchEvent(new Event('scroll', { bubbles: true }));
                }

                const rect = selected.isRoot
                    ? { left: 0, top: 0, width: window.innerWidth, height: window.innerHeight }
                    : el.getBoundingClientRect();
                const classes = !selected.isRoot && typeof el.className === 'string'
                    ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2).map(x => `.${x}`).join('')
                    : '';
                return {
                    x: Math.max(1, Math.min(window.innerWidth - 2, rect.left + rect.width / 2)),
                    y: Math.max(1, Math.min(window.innerHeight - 2, rect.top + rect.height / 2)),
                    wheelDelta: Math.max(650, Math.round(viewport * 0.8)),
                    container: selected.isRoot ? 'document' : `${el.tagName.toLowerCase()}${classes}`
                };
            }
            """, cancellationToken);

        if (result.ValueKind == JsonValueKind.Object)
        {
            var x = ReadDouble(result, "x", 1);
            var y = ReadDouble(result, "y", 1);
            var delta = ReadDouble(result, "wheelDelta", 900);
            await SetAutomationInputPassThroughAsync(browser, true);
            try
            {
                await browser.MoveMouseAsync(x, y, cancellationToken);
                await browser.WheelAsync(0, delta, cancellationToken);
                await Task.Delay(250, cancellationToken);
                await browser.PressKeyAsync("End", cancellationToken);
            }
            finally
            {
                await SetAutomationInputPassThroughAsync(browser, false);
            }
        }

        await Task.Delay(Random.Shared.Next(900, 1_401), cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await browser.EvaluatePageAsync("""
            () => {
                const selector = [
                    'a[href*="/pin/"]',
                    '[data-test-id="pin"]',
                    '[data-grid-item]',
                    '[data-test-id*="pin" i]'
                ].join(',');
                const root = document.scrollingElement || document.documentElement;
                const candidates = [{ el: root, isRoot: true }];
                for (const el of document.querySelectorAll('body *')) {
                    const style = getComputedStyle(el);
                    if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 100) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 260 || rect.height < 240) continue;
                    candidates.push({ el, isRoot: false });
                }

                let selected = candidates[0];
                let bestScore = -1;
                for (const candidate of candidates) {
                    const el = candidate.el;
                    const range = Math.max(0, el.scrollHeight - el.clientHeight);
                    const count = candidate.isRoot
                        ? document.querySelectorAll(selector).length
                        : el.querySelectorAll(selector).length;
                    const score = count * 1000000 + range + el.clientHeight;
                    if (range >= 80 && score > bestScore) {
                        selected = candidate;
                        bestScore = score;
                    }
                }

                const el = selected.el;
                const classes = !selected.isRoot && typeof el.className === 'string'
                    ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2).map(x => `.${x}`).join('')
                    : '';
                return {
                    scrollY: selected.isRoot ? window.scrollY : el.scrollTop,
                    viewportHeight: selected.isRoot ? window.innerHeight : el.clientHeight,
                    documentHeight: el.scrollHeight,
                    containerName: selected.isRoot ? 'document' : `${el.tagName.toLowerCase()}${classes}`,
                    workItemCount: selected.isRoot
                        ? document.querySelectorAll(selector).length
                        : el.querySelectorAll(selector).length
                };
            }
            """, cancellationToken);

        return new PageScrollState(
            ReadDouble(result, "scrollY"),
            ReadDouble(result, "viewportHeight"),
            ReadDouble(result, "documentHeight"),
            ReadString(result, "containerName") ?? "document",
            (int)Math.Clamp(ReadInt64(result, "workItemCount"), 0, int.MaxValue));
    }

    private WorkItem? ParsePin(
        JsonElement pin,
        PinterestPageContext pageContext,
        out bool rejected)
    {
        rejected = false;
        var workId = ReadFlexibleString(pin, "id", "pin_id", "pinId");
        if (string.IsNullOrWhiteSpace(workId))
            return null;

        var board = TryGetObject(pin, "board", out var boardElement)
            ? boardElement
            : default;
        var boardOwner = board.ValueKind == JsonValueKind.Object
                         && TryGetObject(board, "owner", out var ownerElement)
            ? ownerElement
            : default;
        var pinner = TryGetObject(pin, "pinner", out var pinnerElement)
            ? pinnerElement
            : TryGetObject(pin, "owner", out var pinOwner)
                ? pinOwner
                : TryGetObject(pin, "creator", out var creator)
                    ? creator
                    : default;

        var contextAuthor = pageContext.IsBoardPage && boardOwner.ValueKind == JsonValueKind.Object
            ? boardOwner
            : pinner;
        var pinnerUsername = ReadFlexibleString(pinner, "username", "user_name");
        var boardOwnerUsername = ReadFlexibleString(boardOwner, "username", "user_name");
        var responseUsername = pageContext.IsBoardPage ? boardOwnerUsername : pinnerUsername;

        // 作者主页只接收该作者创建的 Pin；画板页按 board.owner 校验，允许画板中保存
        // 其他创作者的 Pin。部分画板响应只给 owner.id、不重复给 username，此时
        // 不能错误地拿原始 pinner.username 与画板所有者比较。
        if (!pageContext.IsBoardPage
            && !string.IsNullOrWhiteSpace(responseUsername)
            && !SameUsername(responseUsername, pageContext.Username))
        {
            rejected = true;
            return null;
        }
        if (pageContext.IsBoardPage
            && boardOwner.ValueKind == JsonValueKind.Object
            && !string.IsNullOrWhiteSpace(responseUsername)
            && !SameUsername(responseUsername, pageContext.Username))
        {
            rejected = true;
            return null;
        }

        var cachedProfile = FindCachedProfile(pageContext.Username, ReadFlexibleString(contextAuthor, "id"));
        // 始终使用页面 username 作为 Pinterest 历史 UID。它同时也是作者主页 URL
        // 的稳定标识，并可避免不同接口有时返回数字 ID、有时不返回导致会话分裂。
        var authorId = pageContext.Username;
        var authorName = ReadFlexibleString(
                             contextAuthor,
                             "full_name",
                             "fullName",
                             "business_name",
                             "name",
                             "username")
                         ?? cachedProfile?.Name
                         ?? pageContext.Username;
        var authorAvatar = ParseAvatar(contextAuthor)
                           ?? cachedProfile?.AvatarUrl;
        CacheProfile(new PinterestProfile(authorId, pageContext.Username, authorName, authorAvatar));

        var assets = ParsePrimaryAssets(pin);
        if (!assets.Any(asset => asset.Type is MediaAssetType.Video or MediaAssetType.Image))
            return null;

        var description = ReadFlexibleString(
                              pin,
                              "title",
                              "grid_title",
                              "description",
                              "closeup_description",
                              "alt_text",
                              "seo_description")
                          ?? $"Pinterest Pin {workId}";
        description = StripHtml(description).Trim();
        if (string.IsNullOrWhiteSpace(description))
            description = $"Pinterest Pin {workId}";

        var sourceUrl = $"https://www.pinterest.com/pin/{Uri.EscapeDataString(workId)}/";
        var cover = ParseBestImage(pin, 0, MediaAssetType.Cover);
        if (assets.Any(asset => asset.Type == MediaAssetType.Video)
            && cover is not null
            && !assets.Any(asset => asset.Type == MediaAssetType.Cover))
        {
            assets.Add(cover);
        }

        return new WorkItem(
            Id,
            workId,
            authorId,
            authorName,
            authorAvatar,
            description,
            ReadCreateTime(pin),
            assets,
            sourceUrl)
        {
            AuthorPageUrl = pageContext.AuthorPageUrl,
            MediaRefererUrl = sourceUrl,
            // 作者瀑布流只负责提供 Pin ID。每条 Pin 都读取公开详情文档中的
            // __PWS_INITIAL_PROPS__，避免列表只给封面时漏掉视频。
            RequiresDetailResolution = true
        };
    }

    private static JsonElement TryGetPinCreator(JsonElement pin)
    {
        if (TryGetObject(pin, "pinner", out var pinner))
            return pinner;
        if (TryGetObject(pin, "owner", out var owner))
            return owner;
        if (TryGetObject(pin, "creator", out var creator))
            return creator;
        return default;
    }

    private static bool LooksLikeVideoPin(JsonElement element, int depth = 0)
    {
        if (depth > 10)
            return false;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = property.Name;
                    if ((name.Equals("is_video", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("isVideo", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.True)
                    {
                        return true;
                    }

                    if (name.Equals("videos", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("video", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("video_list", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("native_creator_video", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("video_status", StringComparison.OrdinalIgnoreCase))
                    {
                        if (property.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                            return true;
                    }

                    if ((name.Equals("media_type", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("content_type", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("creative_type", StringComparison.OrdinalIgnoreCase)
                         || name.Equals("pin_type", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value)
                            && value.Contains("video", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }

                    if (LooksLikeVideoPin(property.Value, depth + 1))
                        return true;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (LooksLikeVideoPin(item, depth + 1))
                        return true;
                }
                break;
        }

        return false;
    }

    private static List<MediaAsset> ParsePrimaryAssets(JsonElement pin)
    {
        var assets = new List<MediaAsset>();
        var index = 1;

        if (TryGetObject(pin, "story_pin_data", out var story)
            && TryGetArray(story, "pages", out var pages))
        {
            foreach (var page in pages.EnumerateArray())
            {
                var media = ParseBestVideo(page, index)
                            ?? ParseBestImage(page, index, MediaAssetType.Image);
                if (media is not null)
                {
                    assets.Add(media);
                    index++;
                }
            }
        }

        if (assets.Count == 0
            && TryGetObject(pin, "carousel_data", out var carousel)
            && TryGetArray(carousel, "items", out var carouselItems))
        {
            foreach (var item in carouselItems.EnumerateArray())
            {
                var media = ParseBestVideo(item, index)
                            ?? ParseBestImage(item, index, MediaAssetType.Image);
                if (media is not null)
                {
                    assets.Add(media);
                    index++;
                }
            }
        }

        if (assets.Count == 0)
        {
            var video = ParseBestVideo(pin, index);
            if (video is not null)
                assets.Add(video);
            else if (ParseBestImage(pin, index, MediaAssetType.Image) is { } image)
                assets.Add(image);
        }

        return assets;
    }

    private static MediaAsset? ParseBestVideo(JsonElement container, int index)
    {
        var candidates = new List<VideoCandidate>();
        CollectVideoCandidates(container, candidates, 0);
        var ordered = candidates
            .Where(candidate => candidate.Urls.Count > 0)
            .OrderByDescending(candidate => (long)candidate.Width * candidate.Height)
            .ThenByDescending(candidate => Math.Max(candidate.Width, candidate.Height))
            .ThenByDescending(candidate => candidate.Urls.Any(
                static url => !url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)))
            .ThenByDescending(candidate => candidate.Bitrate)
            .ThenByDescending(candidate => candidate.FileSize)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        var best = ordered[0];
        var urls = NormalizeUrls(ordered.SelectMany(candidate => candidate.Urls));
        return new MediaAsset(
            MediaAssetType.Video,
            index,
            urls,
            best.Bitrate,
            best.Width,
            best.Height,
            best.Codec);
    }

    private static MediaAsset? ParseBestImage(
        JsonElement container,
        int index,
        MediaAssetType type)
    {
        var candidates = new List<ImageCandidate>();
        CollectImageCandidates(container, candidates, 0);
        var ordered = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Url))
            .OrderByDescending(candidate => (long)candidate.Width * candidate.Height)
            .ThenByDescending(candidate => Math.Max(candidate.Width, candidate.Height))
            .ThenByDescending(candidate => candidate.SourcePriority)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        var best = ordered[0];
        var urls = NormalizeUrls(ordered.Select(candidate => candidate.Url));
        return urls.Count == 0
            ? null
            : new MediaAsset(type, index, urls, Width: best.Width, Height: best.Height);
    }

    private static void CollectVideoCandidates(
        JsonElement element,
        ICollection<VideoCandidate> target,
        int depth)
    {
        if (depth > 14)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var directUrls = ReadDirectUrls(element)
                    .Where(IsVideoUrl)
                    .ToArray();
                if (directUrls.Length > 0)
                {
                    target.Add(new VideoCandidate(
                        ReadInt32(element, "width", "Width"),
                        ReadInt32(element, "height", "Height"),
                        ReadInt64(element, "bitrate", "Bitrate", "bit_rate"),
                        ReadInt64(element, "file_size", "filesize", "size", "FileSize"),
                        ReadFlexibleString(element, "codec", "codec_name", "mime_type", "format"),
                        directUrls));
                }

                foreach (var property in element.EnumerateObject())
                {
                    // 图片集合中有大量 url 字段，不继续把它们当视频遍历。
                    if (property.Name.Equals("images", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("thumbnail", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    CollectVideoCandidates(property.Value, target, depth + 1);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectVideoCandidates(item, target, depth + 1);
                break;
        }
    }

    private static void CollectImageCandidates(
        JsonElement element,
        ICollection<ImageCandidate> target,
        int depth,
        int sourcePriority = 0)
    {
        if (depth > 14)
            return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var localPriority = sourcePriority;
                foreach (var url in ReadDirectUrls(element).Where(IsImageUrl))
                {
                    target.Add(new ImageCandidate(
                        ReadInt32(element, "width", "Width"),
                        ReadInt32(element, "height", "Height"),
                        localPriority,
                        url));
                }

                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Equals("videos", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("video_list", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("pinner", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("owner", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("creator", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("user", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Equals("board", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("avatar", StringComparison.OrdinalIgnoreCase)
                        || property.Name.Contains("profile", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var childPriority = localPriority;
                    if (property.Name.Equals("orig", StringComparison.OrdinalIgnoreCase))
                        childPriority = Math.Max(childPriority, 1000);
                    else if (TryReadSizeLabel(property.Name, out var labelledSize))
                        childPriority = Math.Max(childPriority, labelledSize);
                    CollectImageCandidates(property.Value, target, depth + 1, childPriority);
                }
                break;
            }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectImageCandidates(item, target, depth + 1, sourcePriority);
                break;
            case JsonValueKind.String:
            {
                var url = element.GetString();
                if (IsImageUrl(url))
                    target.Add(new ImageCandidate(0, 0, sourcePriority, url!));
                break;
            }
        }
    }

    private static IEnumerable<JsonElement> EnumeratePinObjects(JsonElement element, int depth = 0)
    {
        if (depth > 12)
            yield break;

        if (element.ValueKind == JsonValueKind.Object)
        {
            if (LooksLikePin(element))
            {
                yield return element;
                yield break;
            }

            foreach (var property in element.EnumerateObject())
            {
                foreach (var pin in EnumeratePinObjects(property.Value, depth + 1))
                    yield return pin;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var pin in EnumeratePinObjects(item, depth + 1))
                    yield return pin;
            }
        }
    }

    private static bool LooksLikePin(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object
            || string.IsNullOrWhiteSpace(ReadFlexibleString(element, "id", "pin_id", "pinId")))
        {
            return false;
        }

        return HasProperty(element, "images")
               || HasProperty(element, "videos")
               || HasProperty(element, "story_pin_data")
               || HasProperty(element, "carousel_data")
               || string.Equals(ReadFlexibleString(element, "type"), "pin", StringComparison.OrdinalIgnoreCase)
               || string.Equals(ReadFlexibleString(element, "__typename"), "Pin", StringComparison.OrdinalIgnoreCase);
    }

    private static BookmarkState ReadBookmarkState(JsonElement root)
    {
        if (!TryGetObject(root, "resource_response", out var response))
            return new BookmarkState(null, null);

        if (TryReadBookmark(response, out var bookmark))
            return BuildBookmarkState(bookmark);
        if (TryGetObject(response, "data", out var data) && TryReadBookmark(data, out bookmark))
            return BuildBookmarkState(bookmark);
        return new BookmarkState(null, null);
    }

    private static bool TryReadBookmark(JsonElement element, out string? bookmark)
    {
        bookmark = null;
        foreach (var name in new[] { "bookmark", "bookmarks", "next_bookmark", "nextBookmark" })
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String)
            {
                bookmark = value.GetString();
                return true;
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                bookmark = value.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
                return true;
            }
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return true;
        }
        return false;
    }

    private static BookmarkState BuildBookmarkState(string? bookmark)
    {
        var value = bookmark?.Trim();
        if (string.IsNullOrWhiteSpace(value)
            || value.Equals("-end-", StringComparison.OrdinalIgnoreCase)
            || value.Equals("end", StringComparison.OrdinalIgnoreCase))
        {
            return new BookmarkState(false, null);
        }
        return new BookmarkState(true, value);
    }

    private static bool TryGetResourceData(JsonElement root, out JsonElement data)
    {
        if (TryGetObject(root, "resource_response", out var resourceResponse)
            && TryGetProperty(resourceResponse, "data", out data))
        {
            return true;
        }
        data = default;
        return false;
    }

    private static JsonElement UnwrapSingleDataObject(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0)
            return data[0];
        if (data.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "pin", "user", "owner", "board", "result" })
            {
                if (TryGetObject(data, name, out var nested))
                    return nested;
            }
        }
        return data;
    }

    private PinterestProfile? ParseProfile(JsonElement user, string? fallbackUsername)
    {
        if (user.ValueKind != JsonValueKind.Object)
            return null;
        var username = ReadFlexibleString(user, "username", "user_name") ?? fallbackUsername;
        var id = ReadFlexibleString(user, "id", "user_id") ?? username;
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(username))
            return null;
        var name = ReadFlexibleString(user, "full_name", "fullName", "business_name", "name", "username")
                   ?? username
                   ?? id!;
        return new PinterestProfile(id ?? username!, username ?? id!, name, ParseAvatar(user));
    }

    private void CacheProfile(JsonElement user, string? fallbackUsername)
    {
        var profile = ParseProfile(user, fallbackUsername);
        if (profile is not null)
            CacheProfile(profile);
    }

    private void CacheProfile(PinterestProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.Id))
            _profiles[profile.Id] = profile;
        if (!string.IsNullOrWhiteSpace(profile.Username))
            _profiles[profile.Username] = profile;
    }

    private PinterestProfile? FindCachedProfile(string? username, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && _profiles.TryGetValue(id, out var profile))
            return profile;
        return !string.IsNullOrWhiteSpace(username) && _profiles.TryGetValue(username, out profile)
            ? profile
            : null;
    }

    private static string? ParseAvatar(JsonElement user)
    {
        var direct = ReadFlexibleString(
            user,
            "image_xlarge_url",
            "image_large_url",
            "image_medium_url",
            "image_small_url",
            "profile_image",
            "avatar_url",
            "avatar");
        if (!string.IsNullOrWhiteSpace(direct))
            return NormalizeUrls(new[] { direct }).FirstOrDefault();

        if (TryGetObject(user, "images", out var images))
            return ParseBestImage(images, 0, MediaAssetType.Cover)?.CandidateUrls.FirstOrDefault();
        return null;
    }

    private static PinterestPageContext? ReadPageContext(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri) || !IsPinterestHost(uri.Host))
            return null;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Uri.UnescapeDataString)
            .ToArray();
        if (segments.Length == 0 || ReservedRootSegments.Contains(segments[0]))
            return null;

        var username = segments[0].Trim();
        if (string.IsNullOrWhiteSpace(username) || username.StartsWith('_'))
            return null;

        var second = segments.Length > 1 ? segments[1].Trim() : string.Empty;
        var isSpecialTab = second.Equals("_created", StringComparison.OrdinalIgnoreCase)
                           || second.Equals("_saved", StringComparison.OrdinalIgnoreCase)
                           || second.Equals("_pins", StringComparison.OrdinalIgnoreCase);
        var isBoardPage = segments.Length > 1 && !string.IsNullOrWhiteSpace(second) && !isSpecialTab;
        var authorPageUrl = $"https://www.pinterest.com/{Uri.EscapeDataString(username)}/";
        return new PinterestPageContext(username, isBoardPage, isBoardPage ? second : null, authorPageUrl);
    }

    private static string? TryReadResourceName(string path)
    {
        var match = ResourceNameRegex().Match(path);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static bool IsPinterestHost(string host)
    {
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized.Equals("pinterest.com", StringComparison.Ordinal)
               || normalized.EndsWith(".pinterest.com", StringComparison.Ordinal)
               || normalized.StartsWith("pinterest.", StringComparison.Ordinal)
               || normalized.Contains(".pinterest.", StringComparison.Ordinal);
    }

    private static bool SameUsername(string left, string right)
        => Uri.UnescapeDataString(left).Trim().TrimStart('@')
            .Equals(Uri.UnescapeDataString(right).Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase);

    private static long ReadCreateTime(JsonElement pin)
    {
        foreach (var name in new[] { "created_at", "created_at_date", "created_time", "timestamp", "time" })
        {
            if (!TryGetProperty(pin, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            {
                if (number > 10_000_000_000)
                    number /= 1000;
                return number;
            }
            if (value.ValueKind != JsonValueKind.String)
                continue;
            var text = value.GetString();
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                if (number > 10_000_000_000)
                    number /= 1000;
                return number;
            }
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
                return date.ToUnixTimeSeconds();
        }
        return 0;
    }

    private static IReadOnlyList<string> ReadDirectUrls(JsonElement element)
    {
        var urls = new List<string>();
        if (element.ValueKind != JsonValueKind.Object)
            return urls;
        foreach (var name in new[]
                 {
                     "url", "src", "play_url", "playUrl", "video_url", "videoUrl",
                     "download_url", "downloadUrl", "hls_url", "hlsUrl", "playlist_url",
                     "playlistUrl", "stream_url", "streamUrl", "image_url",
                     "image_large_url", "image_xlarge_url", "image_medium_url", "image_small_url"
                 })
        {
            if (TryGetProperty(element, name, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } url)
            {
                urls.Add(url);
            }
        }

        foreach (var name in new[] { "url_list", "urlList", "urls" })
        {
            if (!TryGetProperty(element, name, out var value) || value.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } url)
                    urls.Add(url);
            }
        }
        return urls;
    }

    private static bool IsVideoUrl(string? value)
    {
        if (!TryNormalizeUrl(value, out var url))
            return false;
        return url.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".mov", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || url.Contains("v.pinimg.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains("/videos/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImageUrl(string? value)
    {
        if (!TryNormalizeUrl(value, out var url))
            return false;
        if (IsVideoUrl(url))
            return false;
        return url.Contains("i.pinimg.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".jpg", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".jpeg", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".png", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".webp", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".gif", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".avif", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryNormalizeUrl(string? raw, out string url)
    {
        url = WebUtility.HtmlDecode(raw ?? string.Empty).Trim();
        if (url.StartsWith("//", StringComparison.Ordinal))
            url = "https:" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out _);
    }

    private static IReadOnlyList<string> NormalizeUrls(IEnumerable<string> urls)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in urls)
        {
            if (!TryNormalizeUrl(raw, out var value) || !seen.Add(value))
                continue;
            result.Add(value);
        }
        return result;
    }

    private static bool TryReadSizeLabel(string name, out int size)
    {
        size = 0;
        var digits = new string(name.TakeWhile(char.IsDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out size);
    }

    private static string StripHtml(string text)
        => Regex.Replace(WebUtility.HtmlDecode(text), "<[^>]+>", " ");

    private static async Task SetAutomationInputPassThroughAsync(
        IBrowserAutomationService browser,
        bool enabled)
    {
        try
        {
            await browser.EvaluatePageAsync(
                """
                enabled => {
                    window.__smcAllowAutomationInput = enabled;
                    const overlay = document.getElementById('__social_media_crawler_capture_lock__');
                    if (overlay) overlay.style.pointerEvents = enabled ? 'none' : 'auto';
                }
                """,
                enabled);
        }
        catch
        {
            // 未开启页面锁定时没有遮罩，不影响滚动逻辑。
        }
    }

    private static bool HasProperty(JsonElement element, string name)
        => TryGetProperty(element, name, out _);

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out value))
                return true;
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
        => TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
        => TryGetProperty(element, name, out value) && value.ValueKind == JsonValueKind.Array;

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? ReadFlexibleString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                return text;
            if (value.ValueKind == JsonValueKind.Number)
                return value.GetRawText();
        }
        return null;
    }

    private static long ReadInt64(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            {
                return number;
            }
        }
        return 0;
    }

    private static int ReadInt32(JsonElement element, params string[] names)
        => (int)Math.Clamp(ReadInt64(element, names), 0, int.MaxValue);

    private static double ReadDouble(JsonElement element, string name, double fallback = 0)
        => TryGetProperty(element, name, out var value) && value.TryGetDouble(out var number)
            ? number
            : fallback;

    [GeneratedRegex(@"/resource/([^/]+)/", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceNameRegex();

    private sealed record PinterestPageContext(
        string Username,
        bool IsBoardPage,
        string? BoardSlug,
        string AuthorPageUrl);

    private sealed record PinterestProfile(
        string Id,
        string Username,
        string Name,
        string? AvatarUrl)
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Username : Name;
    }

    private sealed record VideoCandidate(
        int Width,
        int Height,
        long Bitrate,
        long FileSize,
        string? Codec,
        IReadOnlyList<string> Urls);

    private sealed record ImageCandidate(
        int Width,
        int Height,
        int SourcePriority,
        string Url);

    private sealed record BookmarkState(bool? HasMore, string? Bookmark);
}
