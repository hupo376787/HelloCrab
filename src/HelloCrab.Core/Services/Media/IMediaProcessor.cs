namespace HelloCrab.Core.Services.Media;

public interface IMediaProcessor
{
    Task<bool> HasAudioStreamAsync(string mediaPath, CancellationToken cancellationToken);

    Task MergeVideoAndAudioAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken);

    Task DownloadHlsAsync(
        string playlistUrl,
        string outputPath,
        string? userAgent,
        string? referer,
        string? cookieHeader,
        CancellationToken cancellationToken);
}
