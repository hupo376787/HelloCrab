using System.Collections.Concurrent;
using HelloCrab.Core.Services.Localization;
using System.Reflection;
using System.Text.RegularExpressions;
using HelloCrab.Core.Services.Images;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Extensions;
using YoloDotNet.Models;

namespace HelloCrab.Desktop.AI;

/// <summary>
/// 使用 YoloDotNet 和 CPU 执行人像检测。
/// 下载后台队列仍为单消费者；手动文件夹扫描可并发调用，最多按需创建 5 个独立 Yolo 实例。
/// </summary>
public sealed class YoloPersonImageDetector : IPersonImageDetector
{
    private const string PreferredModelFileName = "person-detection.onnx";
    private const string Yolo11SearchPattern = "yolo11*.onnx";
    private const int MaxConcurrentDetections = 5;

    private static readonly Regex Yolo11ModelFileNameRegex = new(
        @"^yolo11[a-z]?\.onnx$",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private const long MinimumModelBytes = 1_000_000;

    private readonly SemaphoreSlim _concurrencyGate = new(
        MaxConcurrentDetections,
        MaxConcurrentDetections);
    private readonly ConcurrentBag<DetectorWorker> _idleWorkers = new();
    private int _disposed;

    public PersonDetectionModelInfo GetModelInfo()
    {
        var modelPath = FindModelPath();

        return modelPath is null
            ? new PersonDetectionModelInfo(
                IsFound: false,
                ModelName: null,
                ModelPath: null)
            : new PersonDetectionModelInfo(
                IsFound: true,
                ModelName: Path.GetFileName(modelPath),
                ModelPath: modelPath);
    }

    public async Task<PersonImageDetectionResult> DetectAsync(
        string imagePath,
        double confidence,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return new PersonImageDetectionResult(
                DetectionSucceeded: false,
                ContainsPerson: false,
                ErrorMessage: RuntimeLocalization.Get("Person.Error.FileMissing", "待检测图片不存在。"));
        }

        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        await _concurrencyGate.WaitAsync(cancellationToken);
        DetectorWorker? worker = null;

        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            var modelPath = FindModelPath();

            if (modelPath is null)
            {
                return new PersonImageDetectionResult(
                    DetectionSucceeded: false,
                    ContainsPerson: false,
                    ErrorMessage: RuntimeLocalization.Get(
                        "Person.Error.ModelMissing",
                        "未找到人像检测 ONNX 模型。请在 Models 文件夹中放置 person-detection.onnx，或名称为 yolo11 加任意单个字母的 ONNX 模型（例如 yolo11n.onnx、yolo11m.onnx）。检测已跳过，图片会保留。"));
            }

            worker = RentWorker(modelPath);

            return await Task.Run(
                () => DetectCore(worker.Yolo, imagePath, confidence),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new PersonImageDetectionResult(
                DetectionSucceeded: false,
                ContainsPerson: false,
                ErrorMessage: ex.Message);
        }
        finally
        {
            if (worker is not null)
                ReturnWorker(worker);
            _concurrencyGate.Release();
        }
    }

    private DetectorWorker RentWorker(string modelPath)
    {
        while (_idleWorkers.TryTake(out var worker))
        {
            if (string.Equals(
                    worker.ModelPath,
                    modelPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return worker;
            }

            worker.Dispose();
        }

        return new DetectorWorker(modelPath);
    }

    private void ReturnWorker(DetectorWorker worker)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            worker.Dispose();
            return;
        }

        _idleWorkers.Add(worker);
    }

    private static PersonImageDetectionResult DetectCore(
        Yolo yolo,
        string imagePath,
        double confidence)
    {
        using var input = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        using var image = SKBitmap.Decode(input);

        if (image is null)
        {
            return new PersonImageDetectionResult(
                DetectionSucceeded: false,
                ContainsPerson: false,
                ErrorMessage: RuntimeLocalization.Get("Person.Error.DecodeFailed", "SkiaSharp 无法解码该图片格式。"));
        }

        var normalizedConfidence = Math.Clamp(
            confidence,
            min: 0.10,
            max: 0.95);

        var results = yolo.RunObjectDetection(
            image,
            confidence: normalizedConfidence,
            iou: 0.70);

        foreach (var result in results)
        {
            if (IsPersonResult(result))
            {
                return new PersonImageDetectionResult(
                    DetectionSucceeded: true,
                    ContainsPerson: true);
            }
        }

        return new PersonImageDetectionResult(
            DetectionSucceeded: true,
            ContainsPerson: false);
    }

    private static bool IsPersonResult(object? result)
    {
        if (result is null)
            return false;

        var label = result
            .GetType()
            .GetProperty(
                "Label",
                BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(result);

        if (label is null)
            return false;

        var labelType = label.GetType();

        var labelName = labelType
            .GetProperty(
                "Name",
                BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(label)?
            .ToString();

        if (string.Equals(
                labelName,
                "person",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var idValue = labelType
            .GetProperty(
                "Id",
                BindingFlags.Public | BindingFlags.Instance)?
            .GetValue(label);

        return idValue is not null
               && int.TryParse(idValue.ToString(), out var labelId)
               && labelId == 0;
    }

    private static string? FindModelPath()
    {
        var modelDirectories = GetModelDirectories()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var directory in modelDirectories)
        {
            var preferredModel = Path.Combine(
                directory,
                PreferredModelFileName);

            if (IsValidModelFile(preferredModel))
            {
                return Path.GetFullPath(preferredModel);
            }
        }

        foreach (var directory in modelDirectories)
        {
            foreach (var candidate in EnumerateYolo11Models(directory))
            {
                if (IsValidModelFile(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateYolo11Models(
        string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        string[] candidates;

        try
        {
            candidates = Directory
                .EnumerateFiles(
                    directory,
                    Yolo11SearchPattern,
                    SearchOption.TopDirectoryOnly)
                .Where(path =>
                    Yolo11ModelFileNameRegex.IsMatch(
                        Path.GetFileName(path)))
                .OrderBy(
                    path => Path.GetFileName(path),
                    StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var candidate in candidates)
        {
            yield return candidate;
        }
    }

    private static IEnumerable<string> GetModelDirectories()
    {
        yield return Path.Combine(
            AppContext.BaseDirectory,
            "Models");

        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(
                localAppData,
                "HelloCrab",
                "Models");
        }
    }

    private static bool IsValidModelFile(string path)
    {
        try
        {
            return File.Exists(path)
                   && new FileInfo(path).Length >= MinimumModelBytes;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var acquiredPermits = 0;
        try
        {
            for (; acquiredPermits < MaxConcurrentDetections; acquiredPermits++)
                await _concurrencyGate.WaitAsync();

            while (_idleWorkers.TryTake(out var worker))
                worker.Dispose();
        }
        finally
        {
            for (var index = 0; index < acquiredPermits; index++)
                _concurrencyGate.Release();
        }
    }

    private sealed class DetectorWorker : IDisposable
    {
        public DetectorWorker(string modelPath)
        {
            ModelPath = modelPath;
            Yolo = new Yolo(new YoloOptions
            {
                ExecutionProvider = new CpuExecutionProvider(modelPath)
            });
        }

        public string ModelPath { get; }
        public Yolo Yolo { get; }

        public void Dispose() => Yolo.Dispose();
    }
}