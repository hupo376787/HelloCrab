namespace HelloCrab.Core.Services.Images;

/// <summary>
/// Detects whether a downloaded image contains at least one person.
/// Implementations must fail safe: callers keep the source image whenever detection fails.
/// </summary>
public interface IPersonImageDetector : IAsyncDisposable
{
    PersonDetectionModelInfo GetModelInfo();

    Task<PersonImageDetectionResult> DetectAsync(
        string imagePath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default);
}

public sealed record PersonImageDetectionResult(
    bool DetectionSucceeded,
    bool ContainsPerson,
    string? ErrorMessage = null);

public sealed record PersonDetectionModelInfo(
    bool IsFound,
    string? ModelName,
    string? ModelPath);
