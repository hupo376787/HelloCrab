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
/// 仅在启用人像检测后加载模型，检测失败时不会删除源图片。
/// </summary>
public sealed class YoloPersonImageDetector : IPersonImageDetector
{
    private const string PreferredModelFileName = "person-detection.onnx";
    private const string Yolo11SearchPattern = "yolo11*.onnx";

    private static readonly Regex Yolo11ModelFileNameRegex = new(
        @"^yolo11[a-z]?\.onnx$",
        RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant
        | RegexOptions.Compiled);

    private const long MinimumModelBytes = 1_000_000;

    /// <summary>
    /// Yolo 实例不并行执行检测，同时保护模型的加载和释放。
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Yolo? _yolo;
    private string? _loadedModelPath;
    private bool _disposed;

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

        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken);

        try
        {
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

            EnsureModelLoaded(modelPath);

            return await Task.Run(
                () => DetectCore(imagePath, confidence),
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
            _gate.Release();
        }
    }

    private void EnsureModelLoaded(string modelPath)
    {
        if (_yolo is not null
            && string.Equals(
                _loadedModelPath,
                modelPath,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _yolo?.Dispose();

        _yolo = new Yolo(new YoloOptions
        {
            ExecutionProvider = new CpuExecutionProvider(modelPath)
        });

        _loadedModelPath = modelPath;
    }

    private PersonImageDetectionResult DetectCore(
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

        var results = _yolo!.RunObjectDetection(
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
        if (_disposed)
            return;

        _disposed = true;

        await _gate.WaitAsync();

        try
        {
            _yolo?.Dispose();
            _yolo = null;
            _loadedModelPath = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
