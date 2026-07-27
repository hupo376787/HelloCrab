using System.Text.Json;

namespace HelloCrab.Core.Services.History;

internal sealed class HistoryFavoritesStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    public HistoryFavoritesStore()
    {
        FilePath = Path.Combine(AppContext.BaseDirectory, "HistoryFavorites.json");
    }

    public string FilePath { get; }

    public async Task<HashSet<string>> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(FilePath))
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            await using var stream = File.OpenRead(FilePath);
            var values = await JsonSerializer.DeserializeAsync<string[]>(
                stream,
                _jsonOptions,
                cancellationToken);

            return new HashSet<string>(
                values ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            var brokenPath = FilePath + $".broken-{DateTime.Now:yyyyMMddHHmmss}";
            File.Move(FilePath, brokenPath, true);
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        IEnumerable<string> favoriteKeys,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var values = favoriteKeys
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var tempPath = FilePath + ".tmp";
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    values,
                    _jsonOptions,
                    cancellationToken);
            }

            File.Move(tempPath, FilePath, true);
        }
        finally
        {
            _gate.Release();
        }
    }
}
