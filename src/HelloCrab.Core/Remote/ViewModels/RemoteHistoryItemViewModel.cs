using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HelloCrab.Core.Contracts;

namespace HelloCrab.Core.Remote.ViewModels;

/// <summary>
/// 远程控制端的历史作者显示模型。
///
/// API 只传输可序列化的 RemoteHistoryItemDto；头像通过桌面主机的受保护
/// 图片接口单独获取，避免 Browser/WASM 直接访问抖音 CDN 时受到 CORS 限制。
/// </summary>
public sealed class RemoteHistoryItemViewModel : ObservableObject, IDisposable
{
    private int _id;
    private string _platform = string.Empty;
    private string _userId = string.Empty;
    private string _userName = string.Empty;
    private string _originalUrl = string.Empty;
    private string _folderPath = string.Empty;
    private string _headUrl = string.Empty;
    private int _itemsCount;
    private long _itemsSize;
    private DateTimeOffset _updatedAt;
    private IImage? _avatarImage;

    public RemoteHistoryItemViewModel(RemoteHistoryItemDto source)
    {
        UpdateFrom(source);
    }

    public int Id { get => _id; private set => SetProperty(ref _id, value); }
    public string Platform { get => _platform; private set => SetProperty(ref _platform, value); }
    public string UserId
    {
        get => _userId;
        private set
        {
            if (SetProperty(ref _userId, value))
                OnPropertyChanged(nameof(UidText));
        }
    }

    public string UserName { get => _userName; private set => SetProperty(ref _userName, value); }
    public string OriginalUrl { get => _originalUrl; private set => SetProperty(ref _originalUrl, value); }
    public string FolderPath { get => _folderPath; private set => SetProperty(ref _folderPath, value); }
    public string HeadUrl { get => _headUrl; private set => SetProperty(ref _headUrl, value); }

    public int ItemsCount
    {
        get => _itemsCount;
        private set
        {
            if (SetProperty(ref _itemsCount, value))
                OnPropertyChanged(nameof(ItemsSummary));
        }
    }

    public long ItemsSize
    {
        get => _itemsSize;
        private set
        {
            if (SetProperty(ref _itemsSize, value))
                OnPropertyChanged(nameof(ItemsSummary));
        }
    }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        private set
        {
            if (SetProperty(ref _updatedAt, value))
                OnPropertyChanged(nameof(UpdatedAtText));
        }
    }

    public IImage? AvatarImage
    {
        get => _avatarImage;
        private set => SetProperty(ref _avatarImage, value);
    }

    public string AvatarKey => $"{Id}|{HeadUrl}";
    public string UidText => $"UID：{UserId}";
    public string ItemsSummary => $"{ItemsCount} 个作品 · {FormatBytes(ItemsSize)}";
    public string UpdatedAtText => UpdatedAt == default
        ? "尚未下载"
        : $"最后下载：{UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}";

    /// <summary>
    /// 返回头像 URL 是否发生变化。发生变化时调用方需要重新获取头像。
    /// </summary>
    public bool UpdateFrom(RemoteHistoryItemDto source)
    {
        var oldAvatarKey = AvatarKey;

        Id = source.Id;
        Platform = source.Platform ?? string.Empty;
        UserId = source.UserId ?? string.Empty;
        UserName = source.UserName ?? string.Empty;
        OriginalUrl = source.OriginalUrl ?? string.Empty;
        FolderPath = source.FolderPath ?? string.Empty;
        HeadUrl = source.HeadUrl ?? string.Empty;
        ItemsCount = source.ItemsCount;
        ItemsSize = source.ItemsSize;
        UpdatedAt = source.UpdatedAt;

        var avatarChanged = !string.Equals(oldAvatarKey, AvatarKey, StringComparison.Ordinal);
        if (avatarChanged)
            ClearAvatar();

        return avatarChanged;
    }

    public void SetAvatar(IImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var old = AvatarImage as IDisposable;
        AvatarImage = image;
        old?.Dispose();
    }

    public void ClearAvatar()
    {
        var old = AvatarImage as IDisposable;
        AvatarImage = null;
        old?.Dispose();
    }

    public void Dispose() => ClearAvatar();

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";

        var value = (double)bytes;
        var units = new[] { "KB", "MB", "GB", "TB" };
        var unitIndex = -1;
        do
        {
            value /= 1024;
            unitIndex++;
        } while (value >= 1024 && unitIndex < units.Length - 1);

        return $"{value:0.##} {units[unitIndex]}";
    }
}
