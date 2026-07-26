using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using HelloCrab.Core.Services.Browser;

namespace HelloCrab.Desktop.Chromium;

/// <summary>
/// 使用当前 Microsoft.Playwright 版本自带的安装入口，
/// 优先把匹配版本的 Chromium 安装到程序目录，并兼容查找 Playwright 的默认缓存目录。
/// </summary>
public sealed class PlaywrightChromiumInstaller
{
    private const string BrowsersPathEnvironmentVariable = "PLAYWRIGHT_BROWSERS_PATH";
    private static readonly SemaphoreSlim InstallGate = new(1, 1);

    /// <summary>
    /// 便携版 Chromium 的安装根目录。实际浏览器会位于该目录下的版本子目录中。
    /// </summary>
    public string PreferredInstallDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "chromium"));

    public async Task<int> InstallAsync(
        IProgress<ChromiumInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await InstallGate.WaitAsync(cancellationToken);
        try
        {
            EnsureInstallDirectoryWritable();
            progress?.Report(new ChromiumInstallProgress(
                null,
                "Chromium"));

            return await RunPlaywrightInstallProcessAsync(progress, cancellationToken);
        }
        finally
        {
            InstallGate.Release();
        }
    }

    /// <summary>
    /// 直接启动 Playwright 随包附带的 Node 驱动并重定向输出。
    /// Microsoft.Playwright.Program.Main 启动的是独立子进程，修改 Console.Out
    /// 无法可靠捕获其下载进度，因此这里直接管理该进程。
    /// </summary>
    private async Task<int> RunPlaywrightInstallProcessAsync(
        IProgress<ChromiumInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var (nodePath, cliPath) = FindPlaywrightDriver();
        var startInfo = new ProcessStartInfo
        {
            FileName = nodePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = AppContext.BaseDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        startInfo.ArgumentList.Add(cliPath);
        startInfo.ArgumentList.Add("install");
        startInfo.ArgumentList.Add("chromium");

        startInfo.Environment[BrowsersPathEnvironmentVariable] = PreferredInstallDirectory;
        startInfo.Environment["PW_LANG_NAME"] = "csharp";
        startInfo.Environment["PW_LANG_NAME_VERSION"] =
            $"{Environment.Version.Major}.{Environment.Version.Minor}";
        startInfo.Environment["PW_CLI_DISPLAY_VERSION"] =
            typeof(Microsoft.Playwright.Playwright).Assembly
                .GetName()
                .Version?
                .ToString(3) ?? string.Empty;

        using var process = new Process { StartInfo = startInfo };
        using var outputWriter = new PlaywrightInstallProgressWriter(
            Console.Out,
            progress);
        using var errorWriter = new PlaywrightInstallProgressWriter(
            Console.Error,
            progress);

        if (!process.Start())
            throw new InvalidOperationException("无法启动 Playwright Chromium 安装进程。");

        var outputTask = PumpOutputAsync(
            process.StandardOutput,
            outputWriter,
            cancellationToken);
        var errorTask = PumpOutputAsync(
            process.StandardError,
            errorWriter,
            cancellationToken);

        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(cancellationToken),
                outputTask,
                errorTask);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
        finally
        {
            outputWriter.FlushPending();
            errorWriter.FlushPending();
        }

        return process.ExitCode;
    }

    private static async Task PumpOutputAsync(
        StreamReader reader,
        PlaywrightInstallProgressWriter writer,
        CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            writer.Write(new string(buffer, 0, read));
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 取消安装时进程可能已经自行退出。
        }
    }

    private static (string NodePath, string CliPath) FindPlaywrightDriver()
    {
        var searchDirectories = new List<string>();
        var configuredSearchPath = Environment.GetEnvironmentVariable(
            "PLAYWRIGHT_DRIVER_SEARCH_PATH");
        if (!string.IsNullOrWhiteSpace(configuredSearchPath))
            searchDirectories.Add(configuredSearchPath);

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
            searchDirectories.Add(AppContext.BaseDirectory);

        var assemblyLocation = typeof(Microsoft.Playwright.Playwright)
            .Assembly
            .Location;
        if (!string.IsNullOrWhiteSpace(assemblyLocation))
        {
            var assemblyDirectory = Path.GetDirectoryName(assemblyLocation);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                searchDirectories.Add(assemblyDirectory);

                var nugetRoot = Directory.GetParent(assemblyDirectory)?.Parent;
                if (nugetRoot is not null)
                    searchDirectories.Add(nugetRoot.FullName);
            }
        }

        var customNodePath = Environment.GetEnvironmentVariable(
            "PLAYWRIGHT_NODEJS_PATH");

        foreach (var directory in searchDirectories
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var cliPath = Path.GetFullPath(Path.Combine(
                directory,
                ".playwright",
                "package",
                "cli.js"));
            var nodePath = !string.IsNullOrWhiteSpace(customNodePath)
                ? Path.GetFullPath(customNodePath)
                : Path.GetFullPath(Path.Combine(
                    directory,
                    ".playwright",
                    "node",
                    GetPlaywrightPlatformId(),
                    OperatingSystem.IsWindows() ? "node.exe" : "node"));

            if (File.Exists(cliPath) && File.Exists(nodePath))
                return (nodePath, cliPath);
        }

        throw new InvalidOperationException(
            "Microsoft.Playwright 程序集存在，但没有找到随包附带的 Node 驱动。" +
            "请重新发布或重新解压完整的桌面程序。");
    }

    private static string GetPlaywrightPlatformId()
    {
        if (OperatingSystem.IsWindows())
            return "win32_x64";

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "darwin-arm64"
                : "darwin-x64";
        }

        if (OperatingSystem.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64
                ? "linux-arm64"
                : "linux-x64";
        }

        throw new PlatformNotSupportedException("当前系统不受 Playwright Chromium 安装器支持。");
    }

    /// <summary>
    /// 按以下顺序查找浏览器：程序目录、Playwright 默认缓存目录、外部自定义目录。
    /// </summary>
    public async Task<string?> FindInstalledExecutablePathAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. 优先使用随程序一起移动的便携 Chromium。
        var executablePath = FindChromiumExecutable(PreferredInstallDirectory);
        if (!string.IsNullOrWhiteSpace(executablePath))
            return executablePath;

        // 2. 再兼容原来由 Playwright 安装到用户目录的浏览器。
        var defaultDirectory = GetDefaultPlaywrightBrowsersDirectory();
        executablePath = FindChromiumExecutable(defaultDirectory);
        if (!string.IsNullOrWhiteSpace(executablePath))
            return executablePath;

        // 3. 最后兼容用户在系统环境变量中指定的其他浏览器缓存目录。
        var customDirectory = Environment.GetEnvironmentVariable(
            BrowsersPathEnvironmentVariable,
            EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(customDirectory)
            && !PathsEqual(customDirectory, PreferredInstallDirectory)
            && !PathsEqual(customDirectory, defaultDirectory))
        {
            executablePath = FindChromiumExecutable(customDirectory);
            if (!string.IsNullOrWhiteSpace(executablePath))
                return executablePath;
        }

        // 兼容 Playwright 自身能够解析、但不在上述常规目录中的安装。
        try
        {
            using var playwright = await Microsoft.Playwright.Playwright
                .CreateAsync()
                .WaitAsync(cancellationToken);
            var reportedPath = playwright.Chromium.ExecutablePath;
            return !string.IsNullOrWhiteSpace(reportedPath) && File.Exists(reportedPath)
                ? Path.GetFullPath(reportedPath)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private void EnsureInstallDirectoryWritable()
    {
        try
        {
            Directory.CreateDirectory(PreferredInstallDirectory);

            var testFile = Path.Combine(
                PreferredInstallDirectory,
                $".write-test-{Guid.NewGuid():N}.tmp");
            using (File.Create(testFile))
            {
            }

            File.Delete(testFile);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"无法在程序目录安装 Chromium：{PreferredInstallDirectory}。"
                + "请把 HelloCrab 放到当前用户可写的目录，或以具有写入权限的方式运行。",
                ex);
        }
    }

    private static string? FindChromiumExecutable(string? rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            return null;

        try
        {
            var fullRoot = Path.GetFullPath(rootDirectory);
            if (!Directory.Exists(fullRoot))
                return null;

            var expectedFileNames = OperatingSystem.IsWindows()
                ? new[] { "chrome.exe" }
                : OperatingSystem.IsMacOS()
                    ? new[] { "Chromium", "Google Chrome for Testing" }
                    : new[] { "chrome", "chromium" };

            var candidates = expectedFileNames
                .SelectMany(fileName => Directory.EnumerateFiles(
                    fullRoot,
                    fileName,
                    SearchOption.AllDirectories))
                // 显示登录恢复需要完整浏览器，不能优先选择 headless shell。
                .Where(path => !path.Contains(
                    "headless_shell",
                    StringComparison.OrdinalIgnoreCase)
                    && !path.Contains(
                        "chrome-headless-shell",
                        StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .Where(file => file.Exists)
                .OrderByDescending(file => GetRevisionNumber(file.FullName))
                .ThenByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            return candidates.Count == 0
                ? null
                : Path.GetFullPath(candidates[0].FullName);
        }
        catch
        {
            return null;
        }
    }

    private static long GetRevisionNumber(string path)
    {
        var directory = new FileInfo(path).Directory;
        while (directory is not null)
        {
            var name = directory.Name;
            var separator = name.LastIndexOf('-');
            if (separator >= 0
                && long.TryParse(name[(separator + 1)..], out var revision))
            {
                return revision;
            }

            directory = directory.Parent;
        }

        return 0;
    }

    private static string? GetDefaultPlaywrightBrowsersDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);
            return string.IsNullOrWhiteSpace(localAppData)
                ? null
                : Path.Combine(localAppData, "ms-playwright");
        }

        var userProfile = Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile))
            return null;

        if (OperatingSystem.IsMacOS())
            return Path.Combine(userProfile, "Library", "Caches", "ms-playwright");

        return Path.Combine(userProfile, ".cache", "ms-playwright");
    }

    private static bool PathsEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
    /// <summary>
    /// 捕获 Playwright 安装器的控制台输出。安装器使用回车符刷新同一行，
    /// 因此同时把 CR 和 LF 都视为一条进度消息的结束符。
    /// </summary>
    private sealed class PlaywrightInstallProgressWriter : TextWriter
    {
        private static readonly Regex PercentRegex = new(
            @"(?<!\d)(?<percent>\d{1,3}(?:\.\d+)?)\s*%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex TotalSizeRegex = new(
            @"(?:of|/)\s*(?<size>\d+(?:\.\d+)?\s*[KMGT]?i?B)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex LeadingSizeRegex = new(
            @"^\s*(?<size>\d+(?:\.\d+)?\s*[KMGT]?i?B)\s*\[",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DownloadWithUrlRegex = new(
            @"Downloading\s+(?<stage>.+?)\s+from\s+(?<url>https?://\S+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly Regex DownloadStageRegex = new(
            @"Downloading\s+(?<stage>.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly TextWriter _inner;
        private readonly IProgress<ChromiumInstallProgress>? _progress;
        private readonly StringBuilder _line = new();
        private readonly object _sync = new();
        private string _stage = "Chromium";
        private string? _downloadUrl;
        private string? _lastReportKey;

        public PlaywrightInstallProgressWriter(
            TextWriter inner,
            IProgress<ChromiumInstallProgress>? progress)
        {
            _inner = inner;
            _progress = progress;
        }

        public override Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            lock (_sync)
            {
                _inner.Write(value);
                Append(value);
            }
        }

        public override void Write(string? value)
        {
            if (value is null)
                return;

            lock (_sync)
            {
                _inner.Write(value);
                foreach (var character in value)
                    Append(character);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_sync)
            {
                _inner.WriteLine(value);
                if (!string.IsNullOrEmpty(value))
                {
                    foreach (var character in value)
                        Append(character);
                }

                Append('\n');
            }
        }

        public override void Flush()
        {
            lock (_sync)
            {
                _inner.Flush();
            }
        }

        public void FlushPending()
        {
            lock (_sync)
            {
                ReportLine(_line.ToString());
                _line.Clear();
                _inner.Flush();
            }
        }

        private void Append(char value)
        {
            if (value is '\r' or '\n')
            {
                ReportLine(_line.ToString());
                _line.Clear();
                return;
            }

            // 防止异常输出无限增长。
            if (_line.Length >= 4096)
            {
                ReportLine(_line.ToString());
                _line.Clear();
            }

            _line.Append(value);
        }

        private void ReportLine(string rawLine)
        {
            if (_progress is null)
                return;

            var line = StripAnsi(rawLine).Trim();
            if (line.Length == 0)
                return;

            var stageWithUrlMatch = DownloadWithUrlRegex.Match(line);
            if (stageWithUrlMatch.Success)
            {
                _stage = NormalizeStage(stageWithUrlMatch.Groups["stage"].Value);
                _downloadUrl = stageWithUrlMatch.Groups["url"].Value.Trim();
                Report(null, _stage, null, _downloadUrl);
            }
            else
            {
                var stageMatch = DownloadStageRegex.Match(line);
                if (stageMatch.Success)
                {
                    _stage = NormalizeStage(stageMatch.Groups["stage"].Value);
                    _downloadUrl = null;
                    Report(null, _stage, null, null);
                }
            }

            var percentMatch = PercentRegex.Match(line);
            if (percentMatch.Success
                && double.TryParse(
                    percentMatch.Groups["percent"].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var percent))
            {
                percent = Math.Clamp(percent, 0d, 100d);
                var sizeMatch = TotalSizeRegex.Match(line);
                if (!sizeMatch.Success)
                    sizeMatch = LeadingSizeRegex.Match(line);

                var detail = sizeMatch.Success
                    ? $"size:{sizeMatch.Groups["size"].Value}"
                    : null;
                Report(percent, _stage, detail, _downloadUrl);
                return;
            }

            if (line.Contains("downloaded to", StringComparison.OrdinalIgnoreCase))
                Report(100d, _stage, "finalizing", _downloadUrl);
            else if (line.Contains("extract", StringComparison.OrdinalIgnoreCase))
                Report(null, _stage, "extracting", _downloadUrl);
        }

        private void Report(
            double? percent,
            string stage,
            string? detail,
            string? downloadUrl)
        {
            var key = $"{stage}|{percent:0.##}|{detail}|{downloadUrl}";
            if (string.Equals(key, _lastReportKey, StringComparison.Ordinal))
                return;

            _lastReportKey = key;
            _progress?.Report(new ChromiumInstallProgress(
                percent,
                stage,
                detail,
                downloadUrl));
        }

        private static string NormalizeStage(string value)
        {
            var stage = value.Trim();
            if (stage.Contains("headless", StringComparison.OrdinalIgnoreCase))
                return "Chromium Headless Shell";
            if (stage.Contains("chromium", StringComparison.OrdinalIgnoreCase))
                return "Chromium";
            if (stage.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase))
                return "Playwright FFmpeg";
            if (stage.Contains("winldd", StringComparison.OrdinalIgnoreCase))
                return "Playwright WinLDD";

            return stage.Length > 80 ? stage[..80] : stage;
        }

        private static string StripAnsi(string value)
            => Regex.Replace(value, @"\x1B\[[0-?]*[ -/]*[@-~]", string.Empty);
    }

}
