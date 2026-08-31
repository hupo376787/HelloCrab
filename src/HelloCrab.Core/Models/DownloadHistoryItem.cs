using System.Text.Json.Serialization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using HelloCrab.Core.Services.Localization;

namespace HelloCrab.Core.Models;

public sealed class DownloadHistoryItem : ObservableObject
{
    private int _id;
    private string _platform = string.Empty;
    private string _headUrl = string.Empty;
    private string _userId = string.Empty;
    private string _userName = string.Empty;
    private string _originalUrl = string.Empty;
    private DateTimeOffset _updatedAt;
    private bool _isChecked;
    private int _itemsCount;
    private long _itemsSize;
    private string _folderPath = string.Empty;
    private int _sortOrder;
    private IImage? _avatarImage;
    private bool _isDownloading;

    public int Id { get => _id; set => SetProperty(ref _id, value); }
    public string Platform
    {
        get => _platform;
        set
        {
            if (SetProperty(ref _platform, value))
                OnPropertyChanged(nameof(PlatformDisplayText));
        }
    }
    public string HeadUrl { get => _headUrl; set => SetProperty(ref _headUrl, value); }
    public string UserId { get => _userId; set { if (SetProperty(ref _userId, value)) OnPropertyChanged(nameof(UidText)); } }
    public string UserName { get => _userName; set => SetProperty(ref _userName, value); }
    public string OriginalUrl { get => _originalUrl; set => SetProperty(ref _originalUrl, value); }
    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (SetProperty(ref _updatedAt, value))
                OnPropertyChanged(nameof(UpdatedAtText));
        }
    }
    public bool IsChecked { get => _isChecked; set => SetProperty(ref _isChecked, value); }
    public int ItemsCount
    {
        get => _itemsCount;
        set
        {
            if (SetProperty(ref _itemsCount, value))
                OnPropertyChanged(nameof(ItemsSummary));
        }
    }
    public long ItemsSize
    {
        get => _itemsSize;
        set
        {
            if (SetProperty(ref _itemsSize, value))
            {
                OnPropertyChanged(nameof(ItemsSizeText));
                OnPropertyChanged(nameof(ItemsSummary));
            }
        }
    }
    public string FolderPath { get => _folderPath; set => SetProperty(ref _folderPath, value); }
    public int SortOrder { get => _sortOrder; set => SetProperty(ref _sortOrder, value); }

    [JsonIgnore]
    public IImage? AvatarImage { get => _avatarImage; set => SetProperty(ref _avatarImage, value); }

    [JsonIgnore]
    public bool IsDownloading
    {
        get => _isDownloading;
        set
        {
            if (SetProperty(ref _isDownloading, value))
                OnPropertyChanged(nameof(UpdatedAtText));
        }
    }

    [JsonIgnore]
    public string PlatformDisplayText
    {
        get
        {
            var localization = LocalizationService.Current;
            if (string.IsNullOrWhiteSpace(Platform))
                return localization?.Get("Common.UnknownPlatform", "未知平台") ?? "未知平台";

            var id = Platform.Trim().ToLowerInvariant() switch
            {
                "xhs" => "xiaohongshu",
                _ => Platform.Trim().ToLowerInvariant()
            };
            return localization?.Get($"Platform.{id}", Platform.Trim()) ?? Platform.Trim();
        }
    }

    [JsonIgnore]
    public string UidText => LocalizationService.Current?.Format("History.Uid", UserId)
                             ?? $"UID：{UserId}";

    [JsonIgnore]
    public string UpdatedAtText => IsDownloading
        ? LocalizationService.Current?.Get("Common.Downloading", "正在下载") ?? "正在下载"
        : UpdatedAt == default
            ? LocalizationService.Current?.Get("History.NotDownloaded", "尚未下载") ?? "尚未下载"
            : (LocalizationService.Current?.Format("History.LastDownloaded", UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm"))
               ?? $"最后下载：{UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm}");

    [JsonIgnore]
    public string ItemsSizeText => FormatBytes(ItemsSize);

    [JsonIgnore]
    public string ItemsSummary => LocalizationService.Current?.Format("History.ItemsSummary", ItemsCount, ItemsSizeText)
                                  ?? $"{ItemsCount} 个作品 · {ItemsSizeText}";

    public void RefreshLocalizedText()
    {
        OnPropertyChanged(nameof(PlatformDisplayText));
        OnPropertyChanged(nameof(UidText));
        OnPropertyChanged(nameof(UpdatedAtText));
        OnPropertyChanged(nameof(ItemsSummary));
    }

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
