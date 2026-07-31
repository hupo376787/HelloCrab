using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace HelloCrab.Core.Services.Settings;

public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // settings.json 是本地配置文件，允许直接写入全部 Unicode 字符，
        // 避免中文被保存为 \uXXXX 转义序列。
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public SettingsService()
    {
        // Windows 继续支持便携模式；macOS/Linux 的应用目录经常只读，
        // 因此自动回退到用户应用数据目录。
        var portablePath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        SettingsPath = OperatingSystem.IsWindows()
            ? portablePath
            : Path.Combine(GetApplicationDataDirectory(), "settings.json");
    }

    public string SettingsPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                var defaults = new AppSettings();
                EnsureRemoteToken(defaults);
                RemoteApiPortState.Set(defaults.RemoteApiPort);
                LongFileNameState.Set(defaults.EnableLongFileNames);
                return defaults;
            }

            var json = File.ReadAllText(SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                           ?? new AppSettings();

            // v3 将“文件名中添加作品 ID”的默认值改为 false。
            // 旧版 settings.json 通常保存了旧默认值 true，因此升级时执行一次迁移。
            if (settings.Version < 3)
            {
                settings.IncludeWorkId = false;
            }

            // v4 增加 PushPlusToken；v6 清理已经下线的平台配置字段；v7 增加微博平台；
            // v8 增加人像检测开关；v9 增加视频音轨检测开关；v10 增加 JSON 多语言；
            // v11 增加下载速度限制；v12 增加人像检测置信度；v13 增加长文件名开关。
            // 未知旧字段会在下次保存时自然移除。
            if (settings.Version < 13)
                settings.Version = 13;
            if (string.IsNullOrWhiteSpace(settings.LanguageCode))
                settings.LanguageCode = "zh-CN";

            settings.DownloadSpeedLimitMBps = Math.Clamp(
                settings.DownloadSpeedLimitMBps,
                0m,
                10000m);
            settings.PersonDetectionConfidence = Math.Clamp(
                settings.PersonDetectionConfidence,
                0.10,
                0.95);
            settings.DuplicateStopThreshold = Math.Clamp(
                settings.DuplicateStopThreshold,
                1,
                10000);
            settings.RemoteApiPort = RemoteApiPortState.Normalize(settings.RemoteApiPort);
            RemoteApiPortState.Set(settings.RemoteApiPort);
            LongFileNameState.Set(settings.EnableLongFileNames);
            EnsureRemoteToken(settings);

            return settings;
        }
        catch
        {
            // 设置文件损坏时不阻止程序启动，后续保存会用当前有效设置覆盖它。
            var defaults = new AppSettings();
            EnsureRemoteToken(defaults);
            RemoteApiPortState.Set(defaults.RemoteApiPort);
            LongFileNameState.Set(defaults.EnableLongFileNames);
            return defaults;
        }
    }

    public async Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _saveLock.WaitAsync(cancellationToken);
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            // MainWindowViewModel 的其他设置可能在端口或文件名策略修改后继续触发延迟保存。
            // 每次序列化前都使用当前进程状态，避免旧快照把新值覆盖回去。
            settings.RemoteApiPort = RemoteApiPortState.Current;
            settings.EnableLongFileNames = LongFileNameState.Enabled;

            var json = JsonSerializer.Serialize(settings, JsonOptions);
            var tempPath = SettingsPath + ".tmp";

            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, SettingsPath, true);
        }
        finally
        {
            _saveLock.Release();
        }
    }
    private static void EnsureRemoteToken(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.RemoteApiToken))
            return;

        settings.RemoteApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16))
            .ToLowerInvariant();
    }

    private static string GetApplicationDataDirectory()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local",
                "share");
        }

        var directory = Path.Combine(root, "HelloCrab");
        Directory.CreateDirectory(directory);
        return directory;
    }

}
