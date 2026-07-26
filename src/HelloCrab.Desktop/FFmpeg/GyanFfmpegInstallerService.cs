using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using HelloCrab.Core.Services.Media;

namespace HelloCrab.Desktop.FFmpeg;

/// <summary>
/// 从 FFmpeg 官网列出的 gyan.dev Windows 构建页中解析最新 release essentials ZIP，
/// 后台下载并把 ffmpeg.exe / ffprobe.exe 安装到程序目录的 ffmpeg/bin 文件夹。
/// </summary>
public sealed class GyanFfmpegInstallerService : IFfmpegInstallerService, IDisposable
{
    private const string BuildsPageUrl = "https://www.gyan.dev/ffmpeg/builds/";
    private const string FallbackPackageUrl =
        "https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";
    private const long MaximumPackageBytes = 512L * 1024 * 1024;

    private static readonly Regex PackageLinkRegex = new(
        "href\\s*=\\s*[\\\"'](?<href>[^\\\"']*ffmpeg-release-essentials\\.zip(?:\\?[^\\\"']*)?)[\\\"']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GyanFfmpegInstallerService(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                "HelloCrab/1.0 (+https://www.gyan.dev/ffmpeg/builds/)");
        }
    }

    public bool IsSupported => OperatingSystem.IsWindows() && Environment.Is64BitOperatingSystem;

    public string InstallDirectory => Path.Combine(AppContext.BaseDirectory, "ffmpeg");

    public bool IsInstalled => FindExistingPair() is not null;

    public FfmpegToolInfo GetToolInfo()
    {
        var existing = FindExistingPair();
        return existing is null
            ? new FfmpegToolInfo(false, null, null)
            : new FfmpegToolInfo(
                true,
                Path.GetFullPath(existing.Value.FfmpegPath),
                Path.GetFullPath(existing.Value.FfprobePath));
    }

    public string GetStatusText()
    {
        if (!OperatingSystem.IsWindows())
            return "自动下载仅支持 Windows；其他系统请通过系统包管理器安装 FFmpeg。";

        if (!Environment.Is64BitOperatingSystem)
            return "gyan.dev 当前提供 64 位 Windows 构建，此系统不支持自动安装。";

        var existing = FindExistingPair();
        if (existing is not null)
            return $"FFmpeg 已可用：{Path.GetDirectoryName(existing.Value.FfmpegPath)}";

        return "尚未检测到 FFmpeg。需要检测/修复视频音轨时可点击下载。";
    }

    public async Task<FfmpegInstallResult> InstallAsync(
        IProgress<FfmpegInstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("FFmpeg 自动下载仅支持 Windows。");

        if (!Environment.Is64BitOperatingSystem)
        {
            throw new PlatformNotSupportedException(
                "gyan.dev 的 Windows 构建为 64 位，当前系统无法使用自动安装。");
        }

        var architecture = RuntimeInformation.OSArchitecture.ToString();
        progress?.Report(new FfmpegInstallProgress(
            $"正在访问 gyan.dev FFmpeg Windows 构建页（系统架构：{architecture}）…"));

        var packageUri = await ResolvePackageUriAsync(cancellationToken);
        progress?.Report(new FfmpegInstallProgress(
            $"已找到 release essentials 压缩包：{packageUri.AbsolutePath.Split('/').Last()}",
            DownloadUrl: packageUri.AbsoluteUri));

        var temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "HelloCrab",
            "ffmpeg-install-" + Guid.NewGuid().ToString("N"));
        var archivePath = Path.Combine(temporaryRoot, "ffmpeg-release-essentials.zip");
        var extractPath = Path.Combine(temporaryRoot, "extract");

        Directory.CreateDirectory(temporaryRoot);
        try
        {
            await DownloadPackageAsync(packageUri, archivePath, progress, cancellationToken);
            await VerifyChecksumIfAvailableAsync(
                packageUri,
                archivePath,
                progress,
                cancellationToken);
            progress?.Report(new FfmpegInstallProgress("下载完成，正在校验 ZIP 结构并解压 FFmpeg…"));

            Directory.CreateDirectory(extractPath);
            await ExtractArchiveAsync(archivePath, extractPath, cancellationToken);

            var sourceBinDirectory = FindSourceBinDirectory(extractPath);
            var sourceFfmpeg = Path.Combine(sourceBinDirectory, "ffmpeg.exe");
            var sourceFfprobe = Path.Combine(sourceBinDirectory, "ffprobe.exe");
            if (!IsUsableFile(sourceFfmpeg) || !IsUsableFile(sourceFfprobe))
                throw new InvalidDataException("压缩包中没有找到有效的 ffmpeg.exe 和 ffprobe.exe。");

            var destinationBinDirectory = Path.Combine(InstallDirectory, "bin");
            Directory.CreateDirectory(destinationBinDirectory);

            progress?.Report(new FfmpegInstallProgress(
                $"正在安装到程序目录：{destinationBinDirectory}"));

            var destinationFfmpeg = Path.Combine(destinationBinDirectory, "ffmpeg.exe");
            var destinationFfprobe = Path.Combine(destinationBinDirectory, "ffprobe.exe");
            CopyFileAtomically(sourceFfmpeg, destinationFfmpeg);
            CopyFileAtomically(sourceFfprobe, destinationFfprobe);

            var sourceFfplay = Path.Combine(sourceBinDirectory, "ffplay.exe");
            if (IsUsableFile(sourceFfplay))
                CopyFileAtomically(sourceFfplay, Path.Combine(destinationBinDirectory, "ffplay.exe"));

            CopyOptionalDocumentation(extractPath, InstallDirectory);
            await WriteInstallSourceAsync(
                packageUri,
                architecture,
                cancellationToken);

            if (!IsInstalled)
                throw new IOException("FFmpeg 文件复制完成，但安装结果校验失败。");

            progress?.Report(new FfmpegInstallProgress("FFmpeg 安装完成。"));
            return new FfmpegInstallResult(
                InstallDirectory,
                destinationFfmpeg,
                destinationFfprobe,
                BuildsPageUrl,
                packageUri.AbsoluteUri);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new UnauthorizedAccessException(
                $"无法写入程序目录“{InstallDirectory}”。请把程序放到可写目录，或以管理员身份运行后重试。",
                ex);
        }
        finally
        {
            TryDeleteDirectory(temporaryRoot);
        }
    }

    private async Task<Uri> ResolvePackageUriAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(BuildsPageUrl, cancellationToken);
            response.EnsureSuccessStatusCode();
            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = PackageLinkRegex.Match(html);
            if (match.Success)
            {
                var href = WebUtility.HtmlDecode(match.Groups["href"].Value.Trim());
                var resolved = Uri.TryCreate(href, UriKind.Absolute, out var absolute)
                    ? absolute
                    : new Uri(new Uri(BuildsPageUrl), href);
                ValidatePackageUri(resolved);
                return resolved;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 页面结构变化或暂时访问失败时，使用 gyan.dev 提供的稳定 latest ZIP 地址。
        }

        var fallback = new Uri(FallbackPackageUrl);
        ValidatePackageUri(fallback);
        return fallback;
    }

    private async Task DownloadPackageAsync(
        Uri packageUri,
        string archivePath,
        IProgress<FfmpegInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var probe = await ProbePackageAsync(packageUri, cancellationToken);
        progress?.Report(new FfmpegInstallProgress(
            "正在连接 FFmpeg 下载服务器",
            TotalBytes: probe.TotalBytes,
            DownloadUrl: probe.FinalUri.AbsoluteUri));

        using var request = new HttpRequestMessage(HttpMethod.Get, packageUri);
        request.Headers.Referrer = new Uri(BuildsPageUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var finalUri = response.RequestMessage?.RequestUri ?? probe.FinalUri;
        var totalBytes = response.Content.Headers.ContentRange?.Length
                         ?? response.Content.Headers.ContentLength
                         ?? probe.TotalBytes;
        if (totalBytes is > MaximumPackageBytes)
            throw new InvalidDataException("FFmpeg 压缩包大小异常，已取消下载。");

        progress?.Report(new FfmpegInstallProgress(
            "正在下载 FFmpeg",
            TotalBytes: totalBytes,
            DownloadUrl: finalUri.AbsoluteUri));

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            archivePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 256,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = new byte[1024 * 256];
        long received = 0;
        var downloadTimer = Stopwatch.StartNew();
        var lastReportAt = DateTimeOffset.MinValue;
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
                break;

            received += read;
            if (received > MaximumPackageBytes)
                throw new InvalidDataException("FFmpeg 压缩包超过允许的最大大小，已取消下载。");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            var now = DateTimeOffset.UtcNow;
            if (now - lastReportAt >= TimeSpan.FromMilliseconds(350))
            {
                lastReportAt = now;
                progress?.Report(new FfmpegInstallProgress(
                    "正在下载 FFmpeg",
                    received,
                    totalBytes,
                    CalculateBytesPerSecond(received, downloadTimer.Elapsed),
                    finalUri.AbsoluteUri));
            }
        }

        await output.FlushAsync(cancellationToken);
        if (received == 0)
            throw new InvalidDataException("FFmpeg 下载结果为空。");

        progress?.Report(new FfmpegInstallProgress(
            "正在下载 FFmpeg",
            received,
            totalBytes,
            CalculateBytesPerSecond(received, downloadTimer.Elapsed),
            finalUri.AbsoluteUri));
    }

    /// <summary>
    /// 先探测重定向后的真实下载地址和文件总大小。
    /// 部分 CDN 的正式 GET 响应不返回 Content-Length，因此依次尝试 HEAD
    /// 和只请求第一个字节的 Range 请求，以便进度条仍能显示真实百分比。
    /// </summary>
    private async Task<PackageProbeResult> ProbePackageAsync(
        Uri packageUri,
        CancellationToken cancellationToken)
    {
        var headResult = await TryProbeAsync(
            HttpMethod.Head,
            packageUri,
            useRange: false,
            cancellationToken: cancellationToken);
        if (headResult.TotalBytes is > 0)
            return headResult;

        var rangeResult = await TryProbeAsync(
            HttpMethod.Get,
            headResult.FinalUri,
            useRange: true,
            cancellationToken: cancellationToken);
        return rangeResult.TotalBytes is > 0
            ? rangeResult
            : headResult;
    }

    private async Task<PackageProbeResult> TryProbeAsync(
        HttpMethod method,
        Uri packageUri,
        bool useRange,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(method, packageUri);
            request.Headers.Referrer = new Uri(BuildsPageUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/zip"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            if (useRange)
                request.Headers.Range = new RangeHeaderValue(0, 0);

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var finalUri = response.RequestMessage?.RequestUri ?? packageUri;
            if (!response.IsSuccessStatusCode)
                return new PackageProbeResult(finalUri, null);

            var totalBytes = response.Content.Headers.ContentRange?.Length;
            if (totalBytes is null
                && (!useRange || response.StatusCode == HttpStatusCode.OK))
            {
                totalBytes = response.Content.Headers.ContentLength;
            }

            if (totalBytes is > MaximumPackageBytes)
                throw new InvalidDataException("FFmpeg 压缩包大小异常，已取消下载。");

            return new PackageProbeResult(finalUri, totalBytes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch
        {
            return new PackageProbeResult(packageUri, null);
        }
    }

    private sealed record PackageProbeResult(Uri FinalUri, long? TotalBytes);

    private async Task VerifyChecksumIfAvailableAsync(
        Uri packageUri,
        string archivePath,
        IProgress<FfmpegInstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            var checksumUri = new Uri(packageUri.AbsoluteUri + ".sha256");
            ValidateChecksumUri(checksumUri);
            using var response = await _httpClient.GetAsync(checksumUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                progress?.Report(new FfmpegInstallProgress(
                    "未能获取 gyan.dev SHA-256 校验值，将继续执行 ZIP 结构校验。"));
                return;
            }

            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            var match = Regex.Match(
                text,
                @"(?<![0-9a-fA-F])(?<hash>[0-9a-fA-F]{64})(?![0-9a-fA-F])",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                progress?.Report(new FfmpegInstallProgress(
                    "gyan.dev SHA-256 文件格式无法识别，将继续执行 ZIP 结构校验。"));
                return;
            }

            progress?.Report(new FfmpegInstallProgress("正在校验 FFmpeg 压缩包 SHA-256…"));
            await using var stream = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 256,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha256 = SHA256.Create();
            var actualHash = Convert.ToHexString(
                await sha256.ComputeHashAsync(stream, cancellationToken));
            var expectedHash = match.Groups["hash"].Value;
            if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "FFmpeg 压缩包 SHA-256 校验失败，文件可能下载不完整或已被篡改。");
            }

            progress?.Report(new FfmpegInstallProgress("FFmpeg 压缩包 SHA-256 校验通过。"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch
        {
            progress?.Report(new FfmpegInstallProgress(
                "无法完成在线 SHA-256 校验，将继续执行 ZIP 结构校验。"));
        }
    }

    private static async Task ExtractArchiveAsync(
        string archivePath,
        string extractPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 256,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);

        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name))
                continue;

            var targetPath = Path.GetFullPath(Path.Combine(extractPath, entry.FullName));
            var rootWithSeparator = Path.GetFullPath(extractPath)
                                    + Path.DirectorySeparatorChar;
            if (!targetPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FFmpeg 压缩包包含非法路径。");

            var directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            await using var input = entry.Open();
            await using var output = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 256,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, 1024 * 256, cancellationToken);
        }
    }

    private static string FindSourceBinDirectory(string extractPath)
    {
        var ffmpegPath = Directory.EnumerateFiles(
                extractPath,
                "ffmpeg.exe",
                SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    "bin",
                    StringComparison.OrdinalIgnoreCase));

        return ffmpegPath is null
            ? throw new InvalidDataException("解压后没有找到 FFmpeg bin 目录。" )
            : Path.GetDirectoryName(ffmpegPath)!;
    }

    private static void CopyFileAtomically(string sourcePath, string destinationPath)
    {
        var temporaryPath = destinationPath + ".new";
        File.Copy(sourcePath, temporaryPath, overwrite: true);
        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static void CopyOptionalDocumentation(string extractPath, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (var fileName in new[] { "LICENSE", "README.txt" })
        {
            var source = Directory.EnumerateFiles(
                    extractPath,
                    fileName,
                    SearchOption.AllDirectories)
                .OrderBy(path => path.Count(character =>
                    character is '/' or '\\'))
                .FirstOrDefault();
            if (source is null)
                continue;

            File.Copy(source, Path.Combine(destinationRoot, fileName), overwrite: true);
        }
    }

    private async Task WriteInstallSourceAsync(
        Uri packageUri,
        string architecture,
        CancellationToken cancellationToken)
    {
        var text = new StringBuilder()
            .AppendLine("FFmpeg was downloaded automatically by HelloCrab.")
            .AppendLine($"InstalledAt: {DateTimeOffset.Now:O}")
            .AppendLine($"OSArchitecture: {architecture}")
            .AppendLine($"BuildsPage: {BuildsPageUrl}")
            .AppendLine($"Package: {packageUri.AbsoluteUri}")
            .ToString();
        await File.WriteAllTextAsync(
            Path.Combine(InstallDirectory, "INSTALL-SOURCE.txt"),
            text,
            cancellationToken);
    }

    private static void ValidatePackageUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("www.gyan.dev", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith(
                "ffmpeg-release-essentials.zip",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("构建页返回了不受信任的 FFmpeg 下载地址。");
        }
    }

    private static void ValidateChecksumUri(Uri uri)
    {
        if (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !uri.Host.Equals("www.gyan.dev", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.EndsWith(
                "ffmpeg-release-essentials.zip.sha256",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("生成了不受信任的 FFmpeg 校验地址。");
        }
    }

    private static double CalculateBytesPerSecond(long received, TimeSpan elapsed)
        => received > 0 && elapsed.TotalSeconds > 0
            ? received / elapsed.TotalSeconds
            : 0;


    private static (string FfmpegPath, string FfprobePath)? FindExistingPair()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var directories = new List<string>
        {
            baseDirectory,
            Path.Combine(baseDirectory, "ffmpeg"),
            Path.Combine(baseDirectory, "ffmpeg", "bin"),
            Path.Combine(baseDirectory, "tools", "ffmpeg"),
            Path.Combine(baseDirectory, "tools", "ffmpeg", "bin")
        };

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            directories.AddRange(pathValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        foreach (var directory in directories.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var ffmpegPath = Path.Combine(directory, "ffmpeg.exe");
                var ffprobePath = Path.Combine(directory, "ffprobe.exe");
                if (IsUsableFile(ffmpegPath) && IsUsableFile(ffprobePath))
                    return (ffmpegPath, ffprobePath);
            }
            catch
            {
                // 忽略 PATH 中的无效目录。
            }
        }

        return null;
    }

    private static bool IsUsableFile(string path)
        => File.Exists(path) && new FileInfo(path).Length > 0;

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不覆盖安装结果。
        }
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
            _httpClient.Dispose();
    }
}
