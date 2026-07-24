using HelloCrab.Core.Services.Images;

namespace HelloCrab.Core.Services.Crawling;

public sealed record CrawlSessionResult(
    string PlatformId,
    string CompletionMessage,
    string? AuthorId,
    string? AuthorName,
    string? AuthorFolder,
    int DownloadedWorkCount,
    bool PersonDetectionEnabled,
    PersonDetectionSessionTicket PersonDetection);
