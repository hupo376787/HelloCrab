using System.Text;

namespace HelloCrab.Core.Remote.Services;

/// <summary>
/// Small native-platform store used by Android and iOS. The file is placed in the app's private data directory.
/// </summary>
public sealed class FileRemoteClientPreferencesStore : IRemoteClientPreferencesStore
{
    private readonly string _filePath;

    public FileRemoteClientPreferencesStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? BuildDefaultPath()
            : filePath;
    }

    public RemoteClientPreferences Load()
    {
        if (!File.Exists(_filePath))
            return new RemoteClientPreferences();

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(_filePath))
        {
            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
                continue;

            values[line[..separatorIndex].Trim()] = line[(separatorIndex + 1)..].Trim();
        }

        return new RemoteClientPreferences
        {
            ServerAddress = Decode(values.GetValueOrDefault("server")),
            AccessToken = Decode(values.GetValueOrDefault("token")),
            IsDarkTheme = !string.Equals(values.GetValueOrDefault("theme"), "Light", StringComparison.OrdinalIgnoreCase)
        };
    }

    public void Save(RemoteClientPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = _filePath + ".tmp";
        File.WriteAllLines(
            temporaryPath,
            new[]
            {
                "version=1",
                $"server={Encode(preferences.ServerAddress)}",
                $"token={Encode(preferences.AccessToken)}",
                $"theme={(preferences.IsDarkTheme ? "Dark" : "Light")}" 
            },
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private static string BuildDefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        if (string.IsNullOrWhiteSpace(root))
            root = Path.GetTempPath();

        return Path.Combine(root, "HelloCrabRemote", "client.preferences");
    }

    private static string Encode(string? value)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));

    private static string Decode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }
}
