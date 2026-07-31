namespace HelloCrab.Core.Services.Settings;

public sealed class AppSettings
{
    public int Version { get; set; } = 13;

    /// <summary>
    /// Light 或 Dark。
    /// </summary>
    public string Theme { get; set; } = "Light";
    public string LanguageCode { get; set; } = "zh-CN";

    public string SelectedPlatformId { get; set; } = "douyin";
    public bool HeadlessMode { get; set; }
    public string LastBrowserUrl { get; set; } = string.Empty;
    public string DownloadRoot { get; set; } = string.Empty;
    public bool IncludeWorkId { get; set; } = false;
    public bool DownloadCover { get; set; }
    public bool DownloadMusic { get; set; }

    /// <summary>开启后将作品基础文件名上限从 170 放宽到 220 个字符。</summary>
    public bool EnableLongFileNames { get; set; }

    /// <summary>作品媒体下载速度上限，单位 MB/s；0 表示不限速。</summary>
    public decimal DownloadSpeedLimitMBps { get; set; }
    public bool CheckVideoAudio { get; set; }
    public bool EnablePersonDetection { get; set; }

    /// <summary>人像检测置信度，范围 0.10～0.95；默认 0.60。</summary>
    public double PersonDetectionConfidence { get; set; } = 0.60;
    public bool StopOnDuplicateThreshold { get; set; } = true;
    public int DuplicateStopThreshold { get; set; } = 20;

    /// <summary>PushPlus 微信通知 Token；为空时不发送下载完成通知。</summary>
    public string PushPlusToken { get; set; } = string.Empty;

    // 远程控制服务供 Web/Android/iOS 客户端连接。
    public bool RemoteApiEnabled { get; set; }
    public int RemoteApiPort { get; set; } = 5088;
    public string RemoteApiToken { get; set; } = string.Empty;
}
