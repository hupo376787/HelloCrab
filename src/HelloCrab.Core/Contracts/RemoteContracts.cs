namespace HelloCrab.Core.Contracts;

public sealed class RemoteHealthDto
{
    public string Service { get; set; } = "HelloCrab";
    public string Version { get; set; } = "1.0";
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.Now;
}

public sealed class RemoteCrawlerSnapshot
{
    public DateTimeOffset ServerTime { get; set; } = DateTimeOffset.Now;
    public bool IsBusy { get; set; }
    public bool IsCapturing { get; set; }
    public bool IsBrowserStarted { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public string CurrentUrl { get; set; } = string.Empty;
    public string CurrentWork { get; set; } = string.Empty;
    public bool IsDownloading { get; set; }
    public bool IsDownloadIndeterminate { get; set; }
    public double DownloadProgressPercent { get; set; }
    public string DownloadProgressText { get; set; } = string.Empty;
    public string CurrentCoverUrl { get; set; } = string.Empty;
    public string? CurrentAuthorName { get; set; }
    public string? CurrentAuthorId { get; set; }
    public string? CurrentAuthorDirectory { get; set; }
    public int ResponseCount { get; set; }
    public int? TotalWorkCount { get; set; }
    public int DiscoveredCount { get; set; }
    public int DownloadedCount { get; set; }
    public int SkippedCount { get; set; }
    public int FailedCount { get; set; }
    public RemoteSettingsDto Settings { get; set; } = new();
    public List<string> Logs { get; set; } = new();
    public List<RemoteHistoryItemDto> History { get; set; } = new();
}

public sealed class RemoteSettingsDto
{
    public string Theme { get; set; } = "Light";
    public string SelectedPlatformId { get; set; } = "douyin";
    public bool HeadlessMode { get; set; }
    public string BrowserUrl { get; set; } = string.Empty;
    public string DownloadRoot { get; set; } = string.Empty;
    public bool DownloadVideo { get; set; } = true;
    public bool DownloadImage { get; set; } = true;
    public bool IncludeWorkId { get; set; } = false;
    public bool DownloadCover { get; set; }
    public bool DownloadMusic { get; set; }
    public bool DownloadLivePhoto { get; set; }
    public bool UpdateAuthorNickname { get; set; }
    public bool CheckVideoAudio { get; set; }
    public bool EnablePersonDetection { get; set; }
    public double PersonDetectionConfidence { get; set; } = 0.60;
    public bool StopOnDuplicateThreshold { get; set; } = true;
    public int DuplicateStopThreshold { get; set; } = 20;
    public int PageDelaySeconds { get; set; } = 10;
}

public sealed class RemoteHistoryItemDto
{
    public int Id { get; set; }
    public string Platform { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string OriginalUrl { get; set; } = string.Empty;
    public string FolderPath { get; set; } = string.Empty;
    public string HeadUrl { get; set; } = string.Empty;
    public int ItemsCount { get; set; }
    public long ItemsSize { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool IsDownloading { get; set; }
}

public sealed class RemoteCommandResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;

    public static RemoteCommandResult Ok(string message) => new() { Success = true, Message = message };
    public static RemoteCommandResult Fail(string message) => new() { Success = false, Message = message };
}
