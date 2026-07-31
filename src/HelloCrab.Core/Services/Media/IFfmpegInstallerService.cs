namespace HelloCrab.Core.Services.Media;

public sealed record FfmpegInstallProgress(
    string Message,
    long BytesReceived = 0,
    long? TotalBytes = null,
    double BytesPerSecond = 0)
{
    public int? Percentage => TotalBytes is > 0
        ? (int)Math.Clamp(BytesReceived * 100L / TotalBytes.Value, 0, 100)
        : null;
}

public sealed record FfmpegInstallResult(
    string InstallDirectory,
    string FfmpegPath,
    string FfprobePath,
    string SourcePageUrl,
    string PackageUrl);

public sealed record FfmpegToolInfo(
    bool IsFound,
    string? FfmpegPath,
    string? FfprobePath);

/// <summary>
/// 下载并安装桌面端 FFmpeg 工具。接口放在 Core 中，具体平台实现由桌面项目提供。
/// </summary>
public interface IFfmpegInstallerService
{
    bool IsSupported { get; }

    bool IsInstalled { get; }

    string InstallDirectory { get; }

    string GetStatusText();

    FfmpegToolInfo GetToolInfo();

    Task<FfmpegInstallResult> InstallAsync(
        IProgress<FfmpegInstallProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
