using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using HelloCrab.Core.Services.Media;

namespace HelloCrab.Desktop.FFmpeg;

/// <summary>
/// 使用 ffprobe 检查媒体轨道，并使用 ffmpeg 在不重新编码视频的前提下合并音频。
///
/// 工具查找顺序：
/// 1. 应用程序目录；
/// 2. 应用程序目录下的 ffmpeg、ffmpeg/bin、tools/ffmpeg、tools/ffmpeg/bin；
/// 3. 系统 PATH。
/// </summary>
public sealed class FfmpegMediaService : IMediaProcessor
{
    public async Task<bool> HasAudioStreamAsync(
        string mediaPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaPath);

        var result = await RunProcessAsync(
            ResolveExecutable("ffprobe"),
            new[]
            {
                "-v", "error",
                "-select_streams", "a:0",
                "-show_entries", "stream=index",
                "-of", "csv=p=0",
                mediaPath
            },
            cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new IOException(
                $"ffprobe 检查失败：{TrimProcessText(result.StandardError)}");
        }

        return !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    public async Task DownloadHlsAsync(
        string playlistUrl,
        string outputPath,
        string? userAgent,
        string? referer,
        string? cookieHeader,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        TryDelete(outputPath);

        var isLocalPlaylist = File.Exists(playlistUrl);
        var inputArguments = new List<string>
        {
            "-hide_banner",
            "-loglevel", "error",
            "-y"
        };

        if (!isLocalPlaylist)
        {
            inputArguments.AddRange(new[]
            {
                "-reconnect", "1",
                "-reconnect_streamed", "1",
                "-reconnect_delay_max", "5"
            });
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            inputArguments.Add("-user_agent");
            inputArguments.Add(userAgent);
        }

        var headers = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(referer))
        {
            headers.Append("Referer: ")
                .Append(referer)
                .Append("\r\n");

            if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
            {
                headers.Append("Origin: ")
                    .Append(refererUri.GetLeftPart(UriPartial.Authority))
                    .Append("\r\n");
            }
        }

        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            headers.Append("Cookie: ")
                .Append(cookieHeader)
                .Append("\r\n");
        }

        if (headers.Length > 0)
        {
            inputArguments.Add("-headers");
            inputArguments.Add(headers.ToString());
        }

        if (isLocalPlaylist)
        {
            // 本地化后的 HLS 播放列表可能引用 TS、M4S、初始化片段和 AES key。
            // 明确允许本地 file/crypto 协议与所有分片扩展名，FFmpeg 不再访问网络。
            inputArguments.Add("-protocol_whitelist");
            inputArguments.Add("file,crypto,data");
            inputArguments.Add("-allowed_extensions");
            inputArguments.Add("ALL");
        }

        inputArguments.Add("-i");
        inputArguments.Add(playlistUrl);

        var copyArguments = new List<string>(inputArguments);
        copyArguments.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            "-c", "copy",
            "-movflags", "+faststart",
            "-f", "mp4",
            outputPath
        });

        var copyResult = await RunProcessAsync(
            ResolveExecutable("ffmpeg"),
            copyArguments,
            cancellationToken);
        if (copyResult.ExitCode == 0 && IsUsableFile(outputPath))
            return;

        TryDelete(outputPath);

        // 少数 HLS 音频轨无法直接复制到 MP4；视频保持原码流，仅转 AAC 音频。
        var fallbackArguments = new List<string>(inputArguments);
        fallbackArguments.AddRange(new[]
        {
            "-map", "0:v:0?",
            "-map", "0:a:0?",
            "-c:v", "copy",
            "-c:a", "aac",
            "-b:a", "192k",
            "-movflags", "+faststart",
            "-f", "mp4",
            outputPath
        });

        var transcodeResult = await RunProcessAsync(
            ResolveExecutable("ffmpeg"),
            fallbackArguments,
            cancellationToken);
        if (transcodeResult.ExitCode != 0 || !IsUsableFile(outputPath))
        {
            TryDelete(outputPath);
            var error = string.IsNullOrWhiteSpace(transcodeResult.StandardError)
                ? copyResult.StandardError
                : transcodeResult.StandardError;
            throw new IOException($"ffmpeg 下载 Pinterest HLS 失败：{TrimProcessText(error)}");
        }
    }

    public async Task MergeVideoAndAudioAsync(
        string videoPath,
        string audioPath,
        string outputPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        TryDelete(outputPath);

        // 优先直接复制视频和音频轨，速度最快且不会损失质量。
        var copyResult = await RunProcessAsync(
            ResolveExecutable("ffmpeg"),
            new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", videoPath,
                "-stream_loop", "-1",
                "-i", audioPath,
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-c:v", "copy",
                "-c:a", "copy",
                "-shortest",
                outputPath
            },
            cancellationToken);

        if (copyResult.ExitCode == 0 && IsUsableFile(outputPath))
            return;

        TryDelete(outputPath);

        // 部分音乐源是 MP3/Opus，无法直接封装进 MP4。视频仍然直接复制，
        // 仅把音频转换为与目标容器兼容的格式。
        var extension = Path.GetExtension(outputPath).ToLowerInvariant();
        var audioCodec = extension == ".webm" ? "libopus" : "aac";
        var audioBitrate = extension == ".webm" ? "160k" : "192k";

        var transcodeResult = await RunProcessAsync(
            ResolveExecutable("ffmpeg"),
            new[]
            {
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-i", videoPath,
                "-stream_loop", "-1",
                "-i", audioPath,
                "-map", "0:v:0",
                "-map", "1:a:0",
                "-c:v", "copy",
                "-c:a", audioCodec,
                "-b:a", audioBitrate,
                "-shortest",
                outputPath
            },
            cancellationToken);

        if (transcodeResult.ExitCode != 0 || !IsUsableFile(outputPath))
        {
            TryDelete(outputPath);
            var error = string.IsNullOrWhiteSpace(transcodeResult.StandardError)
                ? copyResult.StandardError
                : transcodeResult.StandardError;
            throw new IOException($"ffmpeg 合并失败：{TrimProcessText(error)}");
        }
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                throw new IOException($"无法启动 {Path.GetFileName(fileName)}。");
        }
        catch (Exception ex) when (ex is Win32Exception or FileNotFoundException)
        {
            throw new FileNotFoundException(
                $"未找到 {Path.GetFileName(fileName)}。请安装 FFmpeg，或把 ffmpeg/ffprobe 放到程序目录或系统 PATH 中。",
                fileName,
                ex);
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static string ResolveExecutable(string baseName)
    {
        var fileName = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, fileName),
            Path.Combine(baseDirectory, "ffmpeg", fileName),
            Path.Combine(baseDirectory, "ffmpeg", "bin", fileName),
            Path.Combine(baseDirectory, "tools", "ffmpeg", fileName),
            Path.Combine(baseDirectory, "tools", "ffmpeg", "bin", fileName)
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathValue))
        {
            foreach (var path in pathValue.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    var candidate = Path.Combine(path, fileName);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // 忽略 PATH 中无效的目录项。
                }
            }
        }

        // 让 Process 自行通过 PATH 查找；若仍找不到，会抛出带操作提示的异常。
        return fileName;
    }

    private static bool IsUsableFile(string path) =>
        File.Exists(path) && new FileInfo(path).Length > 0;

    private static string TrimProcessText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "未返回详细错误信息。";

        const int maxLength = 1600;
        var text = value.Trim();
        return text.Length <= maxLength ? text : text[^maxLength..];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 清理失败由后续文件操作给出更准确的错误。
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 取消流程不再覆盖原始取消异常。
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
