using HelloCrab.Core.Services.Localization;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using HelloCrab.Core.Models;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Core.Sites.Xiaohongshu;

/// <summary>
/// 小红书网页版作者主页适配器。
///
/// 第一页作品位于作者主页 HTML 中的 window.__INITIAL_STATE__；后续页来自
/// edith.xiaohongshu.com/api/sns/web/v1/user_posted。两者只提供 noteId 与 xsecToken，
/// 每条笔记需要再读取 /explore/{noteId} 详情 HTML 才能得到真实视频或图集地址。
/// </summary>
public sealed class XiaohongshuSiteAdapter : ISiteAdapter
{
    private const string InitialStateMarker = "window.__INITIAL_STATE__";

    public string Id => "xiaohongshu";
    public string DisplayName => RuntimeLocalization.Get("Platform.xiaohongshu", "小红书网页版");
    public string HomeUrl => "https://www.xiaohongshu.com/";

    public bool CanHandlePage(string pageUrl)
    {
        if (!Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri))
            return false;

        return IsXiaohongshuHost(uri.Host)
               && TryReadProfileUserId(uri) is not null;
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

        // 作者主页首屏是 HTML document，不是 XHR。
        if (IsProfileDocument(uri))
            return resourceType.Equals("document", StringComparison.OrdinalIgnoreCase);

        // 第二页开始由 user_posted 接口返回 JSON。
        return IsUserPostedApi(uri)
               && (resourceType.Equals("xhr", StringComparison.OrdinalIgnoreCase)
                   || resourceType.Equals("fetch", StringComparison.OrdinalIgnoreCase));
    }

    public ParsedWorkBatch ParseResponse(
        string responseUrl,
        string responseJson,
        string pageUrl,
        string? requestBody)
    {
        if (!Uri.TryCreate(responseUrl, UriKind.Absolute, out var uri))
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);

        return IsUserPostedApi(uri)
            ? ParseUserPostedResponse(responseJson, pageUrl)
            : ParseProfileDocument(responseJson, pageUrl);
    }

    public bool TryCreateCursorRequest(
        BrowserRequestSnapshot previousRequest,
        string cursor,
        out BrowserPageRequest nextRequest)
    {
        if (Uri.TryCreate(previousRequest.Url, UriKind.Absolute, out var uri)
            && IsUserPostedApi(uri))
        {
            return CursorRequestRewriter.TryRewrite(
                previousRequest,
                cursor,
                new[] { "cursor" },
                out nextRequest);
        }

        nextRequest = null!;
        return false;
    }

    public async Task<WorkItem?> ResolveWorkAsync(
        WorkItem work,
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(work.SourceUrl, UriKind.Absolute, out var sourceUri)
            || !IsExploreDocument(sourceUri))
        {
            return work;
        }

        var html = await browser.FetchTextAsync(work.SourceUrl, cancellationToken);
        if (string.IsNullOrWhiteSpace(html))
            throw new InvalidOperationException(RuntimeLocalization.Get("Xhs.Error.EmptyDetail", "小红书作品详情返回了空文档。"));

        using var state = ParseInitialState(html);
        if (!TryFindDetailNote(state.RootElement, work.WorkId, out var note))
        {
            throw new InvalidOperationException(
                RuntimeLocalization.Get("Xhs.Error.NoteMissing", "详情文档中没有找到对应笔记数据，xsec_token 可能已失效或页面要求重新登录。"));
        }

        var author = TryGetObject(note, "user", out var authorElement)
            ? authorElement
            : default;
        var authorId = ReadFirstString(author, "userId", "user_id") ?? work.AuthorId;
        if (!SameId(authorId, work.AuthorId))
            return null;

        var authorName = ReadFirstString(author, "nickname", "nickName", "nick_name")
                         ?? work.AuthorName;
        var authorAvatar = NormalizeUrl(ReadFirstString(author, "avatar", "images", "imageb"))
                           ?? work.AuthorAvatarUrl;
        var title = ReadFirstString(note, "title", "displayTitle", "display_title");
        var description = !string.IsNullOrWhiteSpace(title)
            ? title
            : ReadFirstString(note, "desc", "description") ?? work.Description;
        var createTime = NormalizeTimestamp(ReadFirstInt64(note, "time", "timestamp", "createTime"));
        if (createTime == 0)
            createTime = work.CreateTime;

        var noteType = ReadFirstString(note, "type") ?? string.Empty;
        var assets = new List<MediaAsset>();
        if (noteType.Equals("video", StringComparison.OrdinalIgnoreCase))
        {
            var video = ParseVideo(note);
            if (video is not null)
                assets.Add(video);
        }
        else
        {
            assets.AddRange(ParseImages(note));
        }

        // 某些灰度版本 type 字段缺失或值改变时，按资源结构再兜底判断一次。
        if (!assets.Any(asset => asset.Type is MediaAssetType.Video or MediaAssetType.Image))
        {
            var video = ParseVideo(note);
            if (video is not null)
                assets.Add(video);
            else
                assets.AddRange(ParseImages(note));
        }

        var cover = ParseCover(note, work);
        if (cover is not null)
            assets.Add(cover);

        if (!assets.Any(asset => asset.Type is MediaAssetType.Video or MediaAssetType.Image))
            return null;

        return work with
        {
            AuthorId = authorId,
            AuthorName = authorName,
            AuthorAvatarUrl = authorAvatar,
            Description = description,
            CreateTime = createTime,
            Assets = assets,
            SourceUrl = sourceUri.ToString(),
            MediaRefererUrl = sourceUri.ToString()
        };
    }

    public async Task ScrollNextAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = await browser.EvaluatePageAsync("""
            () => {
                const workSelector = [
                    'a[href*="/explore/"]',
                    '[class*="note-item" i]',
                    '[class*="feeds-page" i] section',
                    '[data-note-id]'
                ].join(',');

                function describe(el, isRoot) {
                    if (isRoot) return 'document';
                    const id = el.id ? `#${el.id}` : '';
                    const classes = typeof el.className === 'string'
                        ? el.className.trim().split(/\s+/).filter(Boolean).slice(0, 2).map(x => `.${x}`).join('')
                        : '';
                    return `${el.tagName.toLowerCase()}${id}${classes}`;
                }

                const root = document.scrollingElement || document.documentElement;
                const candidates = [{ el: root, isRoot: true }];
                for (const el of document.querySelectorAll('body *')) {
                    const style = getComputedStyle(el);
                    if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 100) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 220 || rect.height < 220) continue;
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
                        ? document.querySelectorAll(workSelector).length
                        : el.querySelectorAll(workSelector).length;
                    const score = count * 1000000 + range + el.clientHeight;
                    if (score > bestScore) {
                        selected = candidate;
                        bestScore = score;
                    }
                }

                const el = selected.el;
                const works = [...document.querySelectorAll(workSelector)].filter(item => {
                    const rect = item.getBoundingClientRect();
                    return rect.width > 20 && rect.height > 20;
                });
                works.at(-1)?.scrollIntoView({ block: 'end', inline: 'nearest', behavior: 'auto' });

                const before = selected.isRoot ? window.scrollY : el.scrollTop;
                const viewport = selected.isRoot ? window.innerHeight : el.clientHeight;
                const height = el.scrollHeight;
                const max = Math.max(0, height - viewport);
                const step = Math.max(700, viewport * 0.84);
                let target = Math.min(max, before + step);
                if (max - target < Math.max(180, viewport * 0.28)) target = max;

                if (selected.isRoot) {
                    window.scrollTo({ top: target, behavior: 'auto' });
                    el.scrollTop = target;
                    window.dispatchEvent(new Event('scroll'));
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
                return {
                    x: Math.max(1, Math.min(window.innerWidth - 2, rect.left + rect.width / 2)),
                    y: Math.max(1, Math.min(window.innerHeight - 2, rect.top + rect.height / 2)),
                    wheelDelta: Math.max(500, Math.round(viewport * 0.75)),
                    container: describe(el, selected.isRoot)
                };
            }
            """, cancellationToken);

        if (result.ValueKind == JsonValueKind.Object)
        {
            var x = result.TryGetProperty("x", out var xElement) ? xElement.GetDouble() : 1;
            var y = result.TryGetProperty("y", out var yElement) ? yElement.GetDouble() : 1;
            var delta = result.TryGetProperty("wheelDelta", out var deltaElement)
                ? deltaElement.GetDouble()
                : 800;

            await SetAutomationInputPassThroughAsync(browser, true, cancellationToken);
            try
            {
                await browser.MoveMouseAsync(x, y, cancellationToken);
                await browser.WheelAsync(0, delta, cancellationToken);
                await Task.Delay(300, cancellationToken);
                await browser.PressKeyAsync("End", cancellationToken);
            }
            finally
            {
                await SetAutomationInputPassThroughAsync(browser, false, CancellationToken.None);
            }
        }

        await Task.Delay(1_300, cancellationToken);
    }

    public async Task<PageScrollState> GetScrollStateAsync(
        IBrowserAutomationService browser,
        CancellationToken cancellationToken)
    {
        var result = await browser.EvaluatePageAsync("""
            () => {
                const selector = [
                    'a[href*="/explore/"]',
                    '[class*="note-item" i]',
                    '[class*="feeds-page" i] section',
                    '[data-note-id]'
                ].join(',');
                const root = document.scrollingElement || document.documentElement;
                const candidates = [{ el: root, isRoot: true }];
                for (const el of document.querySelectorAll('body *')) {
                    const style = getComputedStyle(el);
                    if (!/(auto|scroll|overlay)/.test(style.overflowY)) continue;
                    if (el.scrollHeight <= el.clientHeight + 100) continue;
                    const rect = el.getBoundingClientRect();
                    if (rect.width < 220 || rect.height < 220) continue;
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
                const top = selected.isRoot ? window.scrollY : el.scrollTop;
                const viewport = selected.isRoot ? window.innerHeight : el.clientHeight;
                const id = selected.isRoot ? '' : (el.id ? `#${el.id}` : '');
                return {
                    scrollY: top,
                    viewportHeight: viewport,
                    documentHeight: el.scrollHeight,
                    containerName: selected.isRoot ? 'document' : `${el.tagName.toLowerCase()}${id}`,
                    workItemCount: document.querySelectorAll(selector).length
                };
            }
            """, cancellationToken);

        return new PageScrollState(
            ReadDouble(result, "scrollY"),
            ReadDouble(result, "viewportHeight"),
            ReadDouble(result, "documentHeight"),
            ReadFirstString(result, "containerName") ?? "document",
            (int)ReadInt64(result, "workItemCount"));
    }

    private ParsedWorkBatch ParseProfileDocument(string html, string pageUrl)
    {
        using var state = ParseInitialState(html);
        var root = state.RootElement;
        if (!TryGetObject(root, "user", out var user))
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);

        var activeIndex = 0;
        if (TryGetObject(user, "activeTab", out var activeTab))
            activeIndex = (int)ReadFirstInt64(activeTab, "index", "key");

        if (!TryGetArray(user, "notes", out var noteGroups))
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);

        JsonElement notes = default;
        if (activeIndex >= 0 && activeIndex < noteGroups.GetArrayLength())
        {
            var candidate = noteGroups[activeIndex];
            if (candidate.ValueKind == JsonValueKind.Array)
                notes = candidate;
        }

        if (notes.ValueKind != JsonValueKind.Array || notes.GetArrayLength() == 0)
        {
            foreach (var group in noteGroups.EnumerateArray())
            {
                if (group.ValueKind == JsonValueKind.Array && group.GetArrayLength() > 0)
                {
                    notes = group;
                    break;
                }
            }
        }

        var profileUserId = TryReadProfileUserId(pageUrl);
        var profileName = ReadProfileName(user);
        var profileAvatar = ReadProfileAvatar(user);
        var works = ParseListingNotes(
            notes,
            profileUserId,
            profileName,
            profileAvatar,
            pageUrl,
            out var rejected);

        bool? hasMore = null;
        string? cursor = null;
        if (TryGetArray(user, "noteQueries", out var queries)
            && queries.GetArrayLength() > 0)
        {
            var queryIndex = Math.Clamp(activeIndex, 0, queries.GetArrayLength() - 1);
            var query = queries[queryIndex];
            if (query.ValueKind == JsonValueKind.Object)
            {
                hasMore = ReadNullableBool(query, "hasMore", "has_more");
                cursor = ReadFirstString(query, "cursor");
            }
        }

        var diagnostic = rejected > 0
            ? RuntimeLocalization.Format("Xhs.FirstFiltered", "小红书首屏已过滤 {0} 条非目标作者笔记。", rejected)
            : RuntimeLocalization.Format("Xhs.FirstSummary", "小红书首屏发现 {0} 条笔记，将逐条读取详情地址。", works.Count);
        return new ParsedWorkBatch(works, hasMore, cursor, diagnostic, rejected)
        {
            TotalWorkCount = HelloCrab.Core.Utilities.AuthorWorkCountReader.TryRead(Id, root)
        };
    }

    private ParsedWorkBatch ParseUserPostedResponse(string json, string pageUrl)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!TryGetObject(root, "data", out var data)
            || !TryGetArray(data, "notes", out var notes))
        {
            return new ParsedWorkBatch(Array.Empty<WorkItem>(), null, null);
        }

        var profileUserId = TryReadProfileUserId(pageUrl);
        var works = ParseListingNotes(
            notes,
            profileUserId,
            null,
            null,
            pageUrl,
            out var rejected);
        var hasMore = ReadNullableBool(data, "has_more", "hasMore");
        var cursor = ReadFirstString(data, "cursor");
        var diagnostic = rejected > 0
            ? RuntimeLocalization.Format("Xhs.PageFiltered", "小红书分页已过滤 {0} 条非目标作者笔记。", rejected)
            : null;
        return new ParsedWorkBatch(works, hasMore, cursor, diagnostic, rejected)
        {
            TotalWorkCount = HelloCrab.Core.Utilities.AuthorWorkCountReader.TryRead(Id, root)
        };
    }

    private static List<WorkItem> ParseListingNotes(
        JsonElement notes,
        string? profileUserId,
        string? profileName,
        string? profileAvatar,
        string pageUrl,
        out int rejected)
    {
        var works = new List<WorkItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        rejected = 0;
        if (notes.ValueKind != JsonValueKind.Array)
            return works;

        foreach (var wrapper in notes.EnumerateArray())
        {
            var note = TryGetObject(wrapper, "noteCard", out var noteCard)
                ? noteCard
                : wrapper;
            var noteId = ReadFirstString(note, "noteId", "note_id", "id")
                         ?? ReadFirstString(wrapper, "id", "noteId", "note_id");
            var token = ReadFirstString(note, "xsecToken", "xsec_token")
                        ?? ReadFirstString(wrapper, "xsecToken", "xsec_token");
            if (string.IsNullOrWhiteSpace(noteId)
                || string.IsNullOrWhiteSpace(token)
                || !seen.Add(noteId))
            {
                continue;
            }

            var author = TryGetObject(note, "user", out var authorElement)
                ? authorElement
                : default;
            var authorId = ReadFirstString(author, "userId", "user_id") ?? profileUserId;
            if (string.IsNullOrWhiteSpace(authorId))
                continue;
            if (!string.IsNullOrWhiteSpace(profileUserId) && !SameId(authorId, profileUserId))
            {
                rejected++;
                continue;
            }

            var authorName = ReadFirstString(author, "nickname", "nickName", "nick_name")
                             ?? profileName
                             ?? RuntimeLocalization.Get("Common.UnknownAuthor", "未知作者");
            var authorAvatar = NormalizeUrl(ReadFirstString(author, "avatar"))
                               ?? profileAvatar;
            var title = ReadFirstString(note, "displayTitle", "display_title", "title")
                        ?? RuntimeLocalization.Get("Common.UnknownTitle", "无标题");
            var cover = TryGetObject(note, "cover", out var coverElement)
                ? ParseCoverAsset(coverElement)
                : null;
            var assets = cover is null
                ? Array.Empty<MediaAsset>()
                : new[] { cover };
            var sourceUrl = BuildExploreUrl(noteId, token);
            var createTime = TryReadTimeFromNoteId(noteId);

            works.Add(new WorkItem(
                "xiaohongshu",
                noteId,
                authorId,
                authorName,
                authorAvatar,
                title,
                createTime,
                assets,
                sourceUrl)
            {
                AuthorPageUrl = pageUrl,
                MediaRefererUrl = sourceUrl
            });
        }

        return works;
    }

    private static MediaAsset? ParseVideo(JsonElement note)
    {
        var candidates = new List<VideoCandidate>();
        if (TryGetObject(note, "video", out var video))
            CollectVideoCandidates(video, "video", 0, 0, 0, candidates);
        else
            CollectVideoCandidates(note, "note", 0, 0, 0, candidates);

        var ordered = candidates
            .Where(item => !string.IsNullOrWhiteSpace(item.Url))
            .GroupBy(item => item.Url, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(item => CodecRank(item.Codec))
                .ThenByDescending(item => item.Bitrate)
                .ThenByDescending(item => (long)item.Width * item.Height)
                .First())
            .OrderBy(item => CodecRank(item.Codec))
            .ThenByDescending(item => item.Bitrate)
            .ThenByDescending(item => (long)item.Width * item.Height)
            .ToArray();
        if (ordered.Length == 0)
            return null;

        var first = ordered[0];
        return new MediaAsset(
            MediaAssetType.Video,
            0,
            ordered.Select(item => item.Url).ToArray(),
            first.Bitrate,
            first.Width,
            first.Height,
            first.Codec);
    }

    private static IReadOnlyList<MediaAsset> ParseImages(JsonElement note)
    {
        if (!TryGetArray(note, "imageList", out var imageList)
            && !TryGetArray(note, "image_list", out imageList)
            && !TryGetArray(note, "images", out imageList))
        {
            return Array.Empty<MediaAsset>();
        }

        var assets = new List<MediaAsset>();
        var index = 0;
        foreach (var image in imageList.EnumerateArray())
        {
            var urls = ReadImageUrls(image);
            if (urls.Count == 0)
                continue;

            assets.Add(new MediaAsset(
                MediaAssetType.Image,
                index++,
                urls,
                Width: (int)ReadFirstInt64(image, "width", "w"),
                Height: (int)ReadFirstInt64(image, "height", "h")));
        }

        return assets;
    }

    private static MediaAsset? ParseCover(JsonElement note, WorkItem listingWork)
    {
        if (TryGetArray(note, "imageList", out var imageList)
            && imageList.GetArrayLength() > 0)
        {
            var urls = ReadImageUrls(imageList[0]);
            if (urls.Count > 0)
            {
                return new MediaAsset(
                    MediaAssetType.Cover,
                    0,
                    urls,
                    Width: (int)ReadFirstInt64(imageList[0], "width", "w"),
                    Height: (int)ReadFirstInt64(imageList[0], "height", "h"));
            }
        }

        if (TryGetObject(note, "cover", out var cover))
        {
            var parsed = ParseCoverAsset(cover);
            if (parsed is not null)
                return parsed;
        }

        return listingWork.Assets.FirstOrDefault(asset => asset.Type == MediaAssetType.Cover);
    }

    private static MediaAsset? ParseCoverAsset(JsonElement cover)
    {
        var urls = ReadImageUrls(cover);
        if (urls.Count == 0)
            return null;

        return new MediaAsset(
            MediaAssetType.Cover,
            0,
            urls,
            Width: (int)ReadFirstInt64(cover, "width", "w"),
            Height: (int)ReadFirstInt64(cover, "height", "h"));
    }

    private static IReadOnlyList<string> ReadImageUrls(JsonElement image)
    {
        var urls = new List<string>();
        AddUrl(urls, ReadFirstString(image, "urlDefault", "url_default"));

        if (TryGetArray(image, "infoList", out var infoList)
            || TryGetArray(image, "info_list", out infoList))
        {
            foreach (var info in infoList.EnumerateArray()
                         .OrderBy(item => ImageSceneRank(ReadFirstString(item, "imageScene", "image_scene"))))
            {
                AddUrl(urls, ReadFirstString(info, "url"));
            }
        }

        AddUrl(urls, ReadFirstString(image, "url", "urlPre", "url_pre"));
        AddUrl(urls, ReadFirstString(image, "urlPre", "url_pre"));
        return urls;
    }

    private static void CollectVideoCandidates(
        JsonElement element,
        string path,
        int inheritedWidth,
        int inheritedHeight,
        long inheritedBitrate,
        ICollection<VideoCandidate> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                {
                    var ownWidth = (int)ReadFirstInt64(element, "width", "w");
                    var ownHeight = (int)ReadFirstInt64(element, "height", "h");
                    var width = ownWidth > 0 ? ownWidth : inheritedWidth;
                    var height = ownHeight > 0 ? ownHeight : inheritedHeight;
                    var bitrate = ReadFirstInt64(
                        element,
                        "bitrate",
                        "avgBitrate",
                        "avg_bitrate",
                        "videoBitrate");
                    if (bitrate <= 0)
                        bitrate = inheritedBitrate;

                    foreach (var property in element.EnumerateObject())
                    {
                        var childPath = $"{path}.{property.Name}";
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            var value = NormalizeUrl(property.Value.GetString());
                            if (IsVideoUrl(value))
                            {
                                result.Add(new VideoCandidate(
                                    value!,
                                    ReadCodec(childPath),
                                    width,
                                    height,
                                    bitrate));
                            }
                        }
                        else
                        {
                            CollectVideoCandidates(
                                property.Value,
                                childPath,
                                width,
                                height,
                                bitrate,
                                result);
                        }
                    }

                    break;
                }
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectVideoCandidates(
                        item,
                        path,
                        inheritedWidth,
                        inheritedHeight,
                        inheritedBitrate,
                        result);
                }

                break;
            case JsonValueKind.String:
                {
                    var value = NormalizeUrl(element.GetString());
                    if (IsVideoUrl(value))
                    {
                        result.Add(new VideoCandidate(
                            value!,
                            ReadCodec(path),
                            inheritedWidth,
                            inheritedHeight,
                            inheritedBitrate));
                    }

                    break;
                }
        }
    }

    private static bool TryFindDetailNote(JsonElement root, string workId, out JsonElement note)
    {
        note = default;
        if (TryGetObject(root, "note", out var noteStore)
            && TryGetObject(noteStore, "noteDetailMap", out var map))
        {
            if (map.TryGetProperty(workId, out var direct)
                && TryGetObject(direct, "note", out note))
            {
                return true;
            }

            foreach (var property in map.EnumerateObject())
            {
                if (!TryGetObject(property.Value, "note", out var candidate))
                    continue;
                var candidateId = ReadFirstString(candidate, "noteId", "note_id", "id");
                if (SameId(candidateId, workId))
                {
                    note = candidate;
                    return true;
                }
            }
        }

        // 新版详情 SSR 同时把当前笔记放在 feed.undertakeNote.items[].noteCard。
        // note.noteDetailMap 若在后续灰度版本中被移除，仍可从这份结构读取 imageList。
        if (TryGetObject(root, "feed", out var feed)
            && TryGetObject(feed, "undertakeNote", out var undertakeNote)
            && TryGetArray(undertakeNote, "items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!TryGetObject(item, "noteCard", out var candidate))
                    continue;
                var candidateId = ReadFirstString(candidate, "noteId", "note_id", "id");
                if (SameId(candidateId, workId))
                {
                    note = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static JsonDocument ParseInitialState(string html)
    {
        var json = ExtractInitialStateJson(html);
        var normalized = NormalizeJavaScriptJson(json);
        try
        {
            return JsonDocument.Parse(normalized);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                RuntimeLocalization.Get("Xhs.Error.InitialStateParse", "无法解析页面中的 window.__INITIAL_STATE__ 数据，网页结构可能已经变化。"),
                ex);
        }
    }

    private static string ExtractInitialStateJson(string html)
    {
        var markerIndex = html.IndexOf(InitialStateMarker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidOperationException(
                RuntimeLocalization.Get("Xhs.Error.InitialStateMissing", "页面中没有找到 window.__INITIAL_STATE__，请确认网页已登录且未进入验证页面。"));
        }

        var assignmentIndex = html.IndexOf('=', markerIndex + InitialStateMarker.Length);
        var startIndex = assignmentIndex < 0 ? -1 : html.IndexOf('{', assignmentIndex + 1);
        if (startIndex < 0)
            throw new InvalidOperationException(RuntimeLocalization.Get("Xhs.Error.JsonStart", "页面初始数据没有有效的 JSON 对象起点。"));

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = startIndex; index < html.Length; index++)
        {
            var character = html[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character == '{')
            {
                depth++;
                continue;
            }

            if (character != '}')
                continue;

            depth--;
            if (depth == 0)
                return html[startIndex..(index + 1)];
        }

        throw new InvalidOperationException(RuntimeLocalization.Get("Xhs.Error.JsonEnd", "页面初始数据没有完整结束，HTML 可能被截断。"));
    }

    private static string NormalizeJavaScriptJson(string source)
    {
        var builder = new StringBuilder(source.Length);
        var inString = false;
        var escaped = false;
        for (var index = 0; index < source.Length;)
        {
            var character = source[index];
            if (inString)
            {
                builder.Append(character);
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inString = false;
                index++;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                builder.Append(character);
                index++;
                continue;
            }

            if (TryReplaceJavaScriptLiteral(source, index, "undefined", builder, out var consumed)
                || TryReplaceJavaScriptLiteral(source, index, "NaN", builder, out consumed)
                || TryReplaceJavaScriptLiteral(source, index, "Infinity", builder, out consumed))
            {
                index += consumed;
                continue;
            }

            // 2026 年新版详情页在 INITIAL_STATE 中加入了
            // AiNoteDetailStore.noteDetailMap: new Map([])。这是一段可执行的
            // JavaScript 状态，而不是严格 JSON。空 Map/Set 不包含笔记数据，按其
            // JSON 等价空容器归一化，避免整份状态因一个辅助 Store 解析失败。
            if (TryReplaceEmptyJavaScriptCollection(
                    source,
                    index,
                    "Map",
                    "{}",
                    builder,
                    out consumed)
                || TryReplaceEmptyJavaScriptCollection(
                    source,
                    index,
                    "Set",
                    "[]",
                    builder,
                    out consumed))
            {
                index += consumed;
                continue;
            }

            if (character == '-'
                && TryReplaceJavaScriptLiteral(source, index + 1, "Infinity", builder, out consumed))
            {
                index += consumed + 1;
                continue;
            }

            builder.Append(character);
            index++;
        }

        return builder.ToString();
    }

    private static bool TryReplaceEmptyJavaScriptCollection(
        string source,
        int index,
        string constructorName,
        string replacement,
        StringBuilder builder,
        out int consumed)
    {
        consumed = 0;
        const string newKeyword = "new";
        if (index < 0
            || index + newKeyword.Length > source.Length
            || !source.AsSpan(index, newKeyword.Length).Equals(
                newKeyword,
                StringComparison.Ordinal)
            || index > 0 && IsIdentifierCharacter(source[index - 1]))
        {
            return false;
        }

        var cursor = index + newKeyword.Length;
        if (cursor >= source.Length || !char.IsWhiteSpace(source[cursor]))
            return false;
        SkipWhitespace(source, ref cursor);

        if (cursor + constructorName.Length > source.Length
            || !source.AsSpan(cursor, constructorName.Length).Equals(
                constructorName,
                StringComparison.Ordinal))
        {
            return false;
        }

        cursor += constructorName.Length;
        if (cursor < source.Length && IsIdentifierCharacter(source[cursor]))
            return false;
        SkipWhitespace(source, ref cursor);
        if (cursor >= source.Length || source[cursor] != '(')
            return false;

        cursor++;
        SkipWhitespace(source, ref cursor);
        if (cursor < source.Length && source[cursor] == '[')
        {
            cursor++;
            SkipWhitespace(source, ref cursor);
            if (cursor >= source.Length || source[cursor] != ']')
                return false;
            cursor++;
            SkipWhitespace(source, ref cursor);
        }

        if (cursor >= source.Length || source[cursor] != ')')
            return false;

        cursor++;
        builder.Append(replacement);
        consumed = cursor - index;
        return true;
    }

    private static void SkipWhitespace(string source, ref int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
            index++;
    }

    private static bool TryReplaceJavaScriptLiteral(
        string source,
        int index,
        string literal,
        StringBuilder builder,
        out int consumed)
    {
        consumed = 0;
        if (index < 0 || index + literal.Length > source.Length
            || !source.AsSpan(index, literal.Length).Equals(literal, StringComparison.Ordinal))
        {
            return false;
        }

        var previousIsIdentifier = index > 0 && IsIdentifierCharacter(source[index - 1]);
        var nextIndex = index + literal.Length;
        var nextIsIdentifier = nextIndex < source.Length && IsIdentifierCharacter(source[nextIndex]);
        if (previousIsIdentifier || nextIsIdentifier)
            return false;

        builder.Append("null");
        consumed = literal.Length;
        return true;
    }

    private static bool IsIdentifierCharacter(char value)
        => char.IsLetterOrDigit(value) || value is '_' or '$';

    private static string? ReadProfileName(JsonElement user)
    {
        if (!TryGetObject(user, "userPageData", out var pageData)
            || !TryGetObject(pageData, "basicInfo", out var basic))
        {
            return null;
        }

        return ReadFirstString(basic, "nickname", "nickName", "nick_name");
    }

    private static string? ReadProfileAvatar(JsonElement user)
    {
        if (!TryGetObject(user, "userPageData", out var pageData)
            || !TryGetObject(pageData, "basicInfo", out var basic))
        {
            return null;
        }

        return NormalizeUrl(ReadFirstString(basic, "imageb", "images", "avatar"));
    }

    private static string BuildExploreUrl(string noteId, string xsecToken)
        => $"https://www.xiaohongshu.com/explore/{Uri.EscapeDataString(noteId)}" +
           $"?xsec_token={Uri.EscapeDataString(xsecToken)}&xsec_source=pc_feed";

    private static string? TryReadProfileUserId(string pageUrl)
        => Uri.TryCreate(pageUrl, UriKind.Absolute, out var uri)
            ? TryReadProfileUserId(uri)
            : null;

    private static string? TryReadProfileUserId(Uri uri)
    {
        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 3
            || !segments[0].Equals("user", StringComparison.OrdinalIgnoreCase)
            || !segments[1].Equals("profile", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return WebUtility.UrlDecode(segments[2])?.Trim();
    }

    private static bool IsProfileDocument(Uri uri)
        => IsXiaohongshuHost(uri.Host)
           && TryReadProfileUserId(uri) is not null;

    private static bool IsExploreDocument(Uri uri)
        => IsXiaohongshuHost(uri.Host)
           && uri.AbsolutePath.StartsWith("/explore/", StringComparison.OrdinalIgnoreCase);

    private static bool IsUserPostedApi(Uri uri)
        => uri.Host.Equals("edith.xiaohongshu.com", StringComparison.OrdinalIgnoreCase)
           && uri.AbsolutePath.Equals(
               "/api/sns/web/v1/user_posted",
               StringComparison.OrdinalIgnoreCase);

    private static bool IsXiaohongshuHost(string host)
        => host.Equals("xiaohongshu.com", StringComparison.OrdinalIgnoreCase)
           || host.EndsWith(".xiaohongshu.com", StringComparison.OrdinalIgnoreCase);

    private static long TryReadTimeFromNoteId(string noteId)
    {
        if (noteId.Length < 8
            || !long.TryParse(
                noteId.AsSpan(0, 8),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out var seconds))
        {
            return 0;
        }

        return seconds is > 946684800 and < 4102444800 ? seconds : 0;
    }

    private static long NormalizeTimestamp(long value)
    {
        if (value <= 0)
            return 0;
        while (value > 9_999_999_999)
            value /= 1000;
        return value;
    }

    private static string? NormalizeUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = WebUtility.HtmlDecode(value.Trim());
        if (normalized.StartsWith("//", StringComparison.Ordinal))
            return "https:" + normalized;
        if (normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            return "https://" + normalized[7..];
        return Uri.TryCreate(normalized, UriKind.Absolute, out _) ? normalized : null;
    }

    private static bool IsVideoUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.Contains(".mp4", StringComparison.OrdinalIgnoreCase)
               || value.Contains(".m3u8", StringComparison.OrdinalIgnoreCase)
               || value.Contains("sns-video", StringComparison.OrdinalIgnoreCase)
               || value.Contains("/stream/", StringComparison.OrdinalIgnoreCase));

    private static string? ReadCodec(string path)
    {
        if (path.Contains("h264", StringComparison.OrdinalIgnoreCase)
            || path.Contains("avc", StringComparison.OrdinalIgnoreCase))
            return "h264";
        if (path.Contains("h265", StringComparison.OrdinalIgnoreCase)
            || path.Contains("hevc", StringComparison.OrdinalIgnoreCase))
            return "h265";
        if (path.Contains("av1", StringComparison.OrdinalIgnoreCase))
            return "av1";
        if (path.Contains("h266", StringComparison.OrdinalIgnoreCase)
            || path.Contains("vvc", StringComparison.OrdinalIgnoreCase))
            return "h266";
        return null;
    }

    private static int CodecRank(string? codec)
        => codec?.ToLowerInvariant() switch
        {
            "h264" => 0,
            "h265" => 1,
            "av1" => 2,
            "h266" => 3,
            _ => 4
        };

    private static int ImageSceneRank(string? scene)
        => scene?.ToUpperInvariant() switch
        {
            "WB_DFT" => 0,
            "CRD_DFT" => 1,
            "WB_PRV" => 2,
            "CRD_PRV" => 3,
            _ => 4
        };

    private static void AddUrl(ICollection<string> values, string? rawUrl)
    {
        var url = NormalizeUrl(rawUrl);
        if (url is null || IsVideoUrl(url) || values.Contains(url, StringComparer.Ordinal))
            return;
        values.Add(url);
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out value)
               && value.ValueKind == JsonValueKind.Object;
    }

    private static bool TryGetArray(JsonElement element, string name, out JsonElement value)
    {
        value = default;
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(name, out value)
               && value.ValueKind == JsonValueKind.Array;
    }

    private static string? ReadFirstString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString()?.Trim();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
            else if (value.ValueKind == JsonValueKind.Number)
            {
                return value.GetRawText();
            }
        }

        return null;
    }

    private static long ReadFirstInt64(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return 0;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                return number;
            if (value.ValueKind == JsonValueKind.String
                && long.TryParse(value.GetString(), out number))
            {
                return number;
            }
        }

        return 0;
    }

    private static bool? ReadNullableBool(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;
            if (value.ValueKind == JsonValueKind.True)
                return true;
            if (value.ValueKind == JsonValueKind.False)
                return false;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
                return number != 0;
        }

        return null;
    }

    private static double ReadDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.TryGetDouble(out var result)
            ? result
            : 0;

    private static long ReadInt64(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.TryGetInt64(out var result)
            ? result
            : 0;

    private static bool SameId(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(left.Trim(), right.Trim(), StringComparison.Ordinal);

    private static async Task SetAutomationInputPassThroughAsync(
        IBrowserAutomationService browser,
        bool enabled,
        CancellationToken cancellationToken)
    {
        try
        {
            await browser.EvaluatePageAsync(
                enabled
                    ? "() => { window.__smcAllowAutomationInput = true; }"
                    : "() => { window.__smcAllowAutomationInput = false; }",
                cancellationToken);
        }
        catch when (!enabled)
        {
            // 停止或页面关闭时恢复标记失败，不覆盖原始异常。
        }
    }

    private sealed record VideoCandidate(
        string Url,
        string? Codec,
        int Width,
        int Height,
        long Bitrate);
}
