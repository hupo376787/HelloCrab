using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using HelloCrab.Core.Remote.ViewModels;
using ToolGood.Words.Pinyin;

namespace HelloCrab.Core.Remote.Views;

/// <summary>
/// Web / mobile 历史列表的增强逻辑：
/// 1. 搜索规则与桌面端保持一致，支持中文、UID、平台、全拼、姓名拼音和拼音首字母。
/// 2. Browser/WASM 无法可靠读取客户端系统 Emoji 字体，因此浏览器历史作者名中的 Emoji
///    使用 Twemoji PNG 作为内联图像显示，避免缺字方框。Android/iOS 继续使用系统字体。
///
/// 性能说明：ListBox 本身使用虚拟化列表。Emoji 处理只在历史作者 TextBlock 的
/// DataContext 被创建/切换时执行，不能挂在 LayoutUpdated 上，否则滚动期间每一帧布局
/// 都会扫描整棵可视树，抵消虚拟化收益并阻塞 WASM UI 线程。
/// </summary>
public partial class RemoteMainView
{
    private static readonly IDisposable RemoteHistoryEnhancementsDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<RemoteMainView>((view, _) =>
            Dispatcher.UIThread.Post(
                view.InitializeRemoteHistoryEnhancements,
                DispatcherPriority.Background));

    private static readonly IDisposable RemoteHistoryEmojiDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<TextBlock>((textBlock, _) =>
        {
            if (!OperatingSystem.IsBrowser()
                || !textBlock.Classes.Contains("emojiText")
                || textBlock.DataContext is not RemoteHistoryItemViewModel item)
            {
                return;
            }

            var userName = item.UserName ?? string.Empty;
            if (ContainsRemoteHistoryEmoji(userName))
                RenderRemoteHistoryEmojiName(textBlock, userName);
        });

    private static readonly HttpClient RemoteHistoryEmojiHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(12)
    };

    private static readonly SemaphoreSlim RemoteHistoryEmojiDownloadGate = new(4, 4);
    private static readonly object RemoteHistoryEmojiCacheGate = new();
    private static readonly Dictionary<string, Task<Bitmap?>> RemoteHistoryEmojiCache =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, RemoteHistoryPinyinIndex> _remoteHistoryPinyinCache =
        new(StringComparer.Ordinal);
    private readonly HashSet<RemoteHistoryItemViewModel> _remoteHistoryEnhancementObservedItems = new();

    private RemoteMainViewModel? _remoteHistoryEnhancementViewModel;
    private bool _remoteHistoryEnhancementsInitialized;
    private bool _remoteHistoryEnhancedFilterQueued;
    private int _remoteHistoryEnhancementInstallAttempts;

    private readonly record struct RemoteHistoryPinyinIndex(
        string Full,
        string NameFull,
        string Initials);

    private void InitializeRemoteHistoryEnhancements()
    {
        // RemoteMainView.History.cs 会先创建 Web/手机历史 UI。这里用较低优先级安装增强逻辑，
        // 若设备较慢导致控件尚未创建，再异步重试几次。
        if (!_remoteHistoryUiInitialized
            || _remoteHistoryViewModel is not { } viewModel
            || (_browserHistorySearchBox is null && _mobileHistorySearchBox is null))
        {
            if (_remoteHistoryEnhancementInstallAttempts++ < 8)
            {
                Dispatcher.UIThread.Post(
                    InitializeRemoteHistoryEnhancements,
                    DispatcherPriority.Background);
            }
            return;
        }

        if (!_remoteHistoryEnhancementsInitialized)
        {
            _remoteHistoryEnhancementsInitialized = true;

            if (_browserHistorySearchBox is not null)
                _browserHistorySearchBox.TextChanged += RemoteHistoryEnhancedSearchTextChanged;
            if (_mobileHistorySearchBox is not null)
                _mobileHistorySearchBox.TextChanged += RemoteHistoryEnhancedSearchTextChanged;

            // 不再监听 LayoutUpdated。虚拟化列表滚动时会不断触发布局事件，
            // 在该事件里扫描可视树会让 Browser/WASM 很快出现明显卡顿甚至假死。
        }

        BindRemoteHistoryEnhancements(viewModel);
        QueueRemoteHistoryEnhancedFilter();
    }

    private void BindRemoteHistoryEnhancements(RemoteMainViewModel viewModel)
    {
        if (!ReferenceEquals(_remoteHistoryEnhancementViewModel, viewModel))
        {
            if (_remoteHistoryEnhancementViewModel is not null)
            {
                _remoteHistoryEnhancementViewModel.History.CollectionChanged -=
                    RemoteHistoryEnhancementHistoryChanged;
            }

            ClearRemoteHistoryEnhancementItemSubscriptions();
            _remoteHistoryEnhancementViewModel = viewModel;
            _remoteHistoryEnhancementViewModel.History.CollectionChanged +=
                RemoteHistoryEnhancementHistoryChanged;
        }

        RefreshRemoteHistoryEnhancementItemSubscriptions();
    }

    private void RemoteHistoryEnhancementHistoryChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        RefreshRemoteHistoryEnhancementItemSubscriptions();
        QueueRemoteHistoryEnhancedFilter();
    }

    private void RefreshRemoteHistoryEnhancementItemSubscriptions()
    {
        if (_remoteHistoryEnhancementViewModel is null)
            return;

        var current = _remoteHistoryEnhancementViewModel.History.ToHashSet();

        foreach (var item in _remoteHistoryEnhancementObservedItems
                     .Where(item => !current.Contains(item))
                     .ToArray())
        {
            item.PropertyChanged -= RemoteHistoryEnhancementItemChanged;
            _remoteHistoryEnhancementObservedItems.Remove(item);
        }

        foreach (var item in current)
        {
            if (_remoteHistoryEnhancementObservedItems.Add(item))
                item.PropertyChanged += RemoteHistoryEnhancementItemChanged;
        }
    }

    private void ClearRemoteHistoryEnhancementItemSubscriptions()
    {
        foreach (var item in _remoteHistoryEnhancementObservedItems)
            item.PropertyChanged -= RemoteHistoryEnhancementItemChanged;

        _remoteHistoryEnhancementObservedItems.Clear();
    }

    private void RemoteHistoryEnhancementItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RemoteHistoryItemViewModel.UserName)
            or nameof(RemoteHistoryItemViewModel.UserId)
            or nameof(RemoteHistoryItemViewModel.Platform))
        {
            QueueRemoteHistoryEnhancedFilter();
        }
    }

    private void RemoteHistoryEnhancedSearchTextChanged(object? sender, TextChangedEventArgs e)
        => QueueRemoteHistoryEnhancedFilter();

    private void QueueRemoteHistoryEnhancedFilter()
    {
        if (_remoteHistoryEnhancedFilterQueued)
            return;

        _remoteHistoryEnhancedFilterQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _remoteHistoryEnhancedFilterQueued = false;
                ApplyRemoteHistoryEnhancedFilter();
            },
            DispatcherPriority.Background);
    }

    private void ApplyRemoteHistoryEnhancedFilter()
    {
        if (_remoteHistoryEnhancementViewModel is not { } viewModel)
            return;

        var keywords = (_remoteHistorySearchText ?? string.Empty)
            .Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var desired = viewModel.History
            .Where(item => keywords.Length == 0
                           || keywords.All(keyword => RemoteHistoryItemMatches(item, keyword)))
            .ToArray();

        // 原有 RemoteMainView.History.cs 会先执行基础匹配。这里在事件队列末尾用
        // 与桌面一致的拼音结果覆盖它，避免全拼/首字母命中后又被基础筛选清掉。
        if (!_remoteFilteredHistory.SequenceEqual(desired))
        {
            _remoteFilteredHistory.Clear();
            foreach (var item in desired)
                _remoteFilteredHistory.Add(item);
        }

        var countText = keywords.Length == 0
            ? $"共 {viewModel.History.Count} 位作者"
            : $"找到 {desired.Length} 位作者 · 共 {viewModel.History.Count} 位";

        if (_browserHistoryCountText is not null)
            _browserHistoryCountText.Text = countText;
        if (_mobileHistoryCountText is not null)
            _mobileHistoryCountText.Text = countText;
    }

    private bool RemoteHistoryItemMatches(RemoteHistoryItemViewModel item, string keyword)
    {
        // 与桌面端一致：先匹配原始作者名、UID、平台。
        if (ContainsRemoteHistorySearchText(item.UserName, keyword)
            || ContainsRemoteHistorySearchText(item.UserId, keyword)
            || ContainsRemoteHistorySearchText(item.Platform, keyword))
        {
            return true;
        }

        // 拼音搜索只针对作者名，并只在查询包含英文字母时启用。
        if (string.IsNullOrWhiteSpace(item.UserName)
            || !keyword.Any(IsRemoteHistoryAsciiLetter))
        {
            return false;
        }

        var normalizedKeyword = NormalizeRemoteHistoryPinyin(keyword);
        if (normalizedKeyword.Length == 0)
            return false;

        var pinyin = GetRemoteHistoryPinyinIndex(item.UserName);
        return pinyin.Full.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
               || pinyin.NameFull.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase)
               || pinyin.Initials.Contains(normalizedKeyword, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsRemoteHistorySearchText(string? value, string keyword)
        => !string.IsNullOrWhiteSpace(value)
           && value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private RemoteHistoryPinyinIndex GetRemoteHistoryPinyinIndex(string userName)
    {
        if (_remoteHistoryPinyinCache.TryGetValue(userName, out var cached))
            return cached;

        // 三套索引与桌面 MainWindow.HistoryPinyinSearch.cs 完全一致：
        // 普通全拼、姓名模式全拼、拼音首字母。
        var full = TryGetRemoteHistoryPinyin(() => WordsHelper.GetPinyin(userName));
        var nameFull = TryGetRemoteHistoryPinyin(() => WordsHelper.GetPinyinForName(userName));
        var initials = TryGetRemoteHistoryPinyin(() => WordsHelper.GetFirstPinyin(userName));
        var result = new RemoteHistoryPinyinIndex(full, nameFull, initials);

        if (_remoteHistoryPinyinCache.Count >= 4096)
            _remoteHistoryPinyinCache.Clear();

        _remoteHistoryPinyinCache[userName] = result;
        return result;
    }

    private static string TryGetRemoteHistoryPinyin(Func<string> converter)
    {
        try
        {
            return NormalizeRemoteHistoryPinyin(converter());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeRemoteHistoryPinyin(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (IsRemoteHistoryAsciiLetter(ch) || ch is >= '0' and <= '9')
                builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    private static bool IsRemoteHistoryAsciiLetter(char ch)
        => (ch >= 'A' && ch <= 'Z') || (ch >= 'a' && ch <= 'z');

    private static bool ContainsRemoteHistoryEmoji(string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            if (IsRemoteHistoryEmojiElement(enumerator.GetTextElement()))
                return true;
        }

        return false;
    }

    private static void RenderRemoteHistoryEmojiName(TextBlock textBlock, string userName)
    {
        // Text 与 Inlines 不能同时作为内容来源；移除原 UserName Binding 后由本方法维护。
        textBlock.ClearValue(TextBlock.TextProperty);
        textBlock.Inlines?.Clear();

        var normalText = new StringBuilder();
        var enumerator = StringInfo.GetTextElementEnumerator(userName);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            if (!IsRemoteHistoryEmojiElement(element))
            {
                normalText.Append(element);
                continue;
            }

            FlushRemoteHistoryNormalText(textBlock, normalText);

            var twemojiCode = BuildTwemojiCode(element);
            if (string.IsNullOrWhiteSpace(twemojiCode))
            {
                normalText.Append(element);
                continue;
            }

            var size = Math.Max(16d, textBlock.FontSize + 2d);
            var image = new Image
            {
                Width = size,
                Height = size,
                Margin = new Thickness(1, 0),
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            ToolTip.SetTip(image, element);

            // InlineCollection 会自动把 Control 包装成 InlineUIContainer。
            textBlock.Inlines?.Add(image);
            _ = SetRemoteHistoryEmojiImageAsync(image, twemojiCode);
        }

        FlushRemoteHistoryNormalText(textBlock, normalText);
    }

    private static void FlushRemoteHistoryNormalText(TextBlock textBlock, StringBuilder text)
    {
        if (text.Length == 0)
            return;

        textBlock.Inlines?.Add(text.ToString());
        text.Clear();
    }

    private static bool IsRemoteHistoryEmojiElement(string element)
    {
        if (string.IsNullOrEmpty(element))
            return false;

        var hasEmojiPresentationSelector = false;
        foreach (var rune in element.EnumerateRunes())
        {
            var value = rune.Value;
            if (value == 0xFE0F)
                hasEmojiPresentationSelector = true;

            if (value is >= 0x1F000 and <= 0x1FAFF
                or >= 0x2600 and <= 0x27BF
                or >= 0x2300 and <= 0x23FF
                or >= 0x2B00 and <= 0x2BFF
                || value is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139
                or 0x3030 or 0x303D or 0x3297 or 0x3299 or 0x20E3)
            {
                return true;
            }
        }

        return hasEmojiPresentationSelector;
    }

    private static string BuildTwemojiCode(string element)
    {
        var parts = new List<string>();
        foreach (var rune in element.EnumerateRunes())
        {
            // Twemoji 文件名会省略 emoji presentation selector (FE0F)。
            if (rune.Value == 0xFE0F)
                continue;

            parts.Add(rune.Value.ToString("x", CultureInfo.InvariantCulture));
        }

        return string.Join("-", parts);
    }

    private static Task<Bitmap?> GetRemoteHistoryEmojiBitmapAsync(string twemojiCode)
    {
        lock (RemoteHistoryEmojiCacheGate)
        {
            if (RemoteHistoryEmojiCache.TryGetValue(twemojiCode, out var cached))
                return cached;

            var task = DownloadRemoteHistoryEmojiBitmapAsync(twemojiCode);
            RemoteHistoryEmojiCache[twemojiCode] = task;
            return task;
        }
    }

    private static async Task<Bitmap?> DownloadRemoteHistoryEmojiBitmapAsync(string twemojiCode)
    {
        await RemoteHistoryEmojiDownloadGate.WaitAsync();
        try
        {
            // Browser/WASM 的 Skia 画布不能依赖浏览器系统 Emoji 字体。
            // Twemoji 17.0.3 graphics: CC-BY 4.0, https://github.com/jdecked/twemoji
            var url =
                $"https://cdn.jsdelivr.net/gh/jdecked/twemoji@17.0.3/assets/72x72/{twemojiCode}.png";
            var bytes = await RemoteHistoryEmojiHttpClient.GetByteArrayAsync(url);
            if (bytes.Length == 0)
                return null;

            using var stream = new MemoryStream(bytes, writable: false);
            return new Bitmap(stream);
        }
        catch
        {
            // Emoji 属于显示增强。CDN 暂时不可用时保留当前位置，不影响历史操作和搜索。
            return null;
        }
        finally
        {
            RemoteHistoryEmojiDownloadGate.Release();
        }
    }

    private static async Task SetRemoteHistoryEmojiImageAsync(Image image, string twemojiCode)
    {
        var bitmap = await GetRemoteHistoryEmojiBitmapAsync(twemojiCode);
        if (bitmap is null)
            return;

        if (Dispatcher.UIThread.CheckAccess())
        {
            image.Source = bitmap;
        }
        else
        {
            Dispatcher.UIThread.Post(() => image.Source = bitmap);
        }
    }
}
