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
/// CPU person detector backed by YoloDotNet. The model is loaded only when the user enables
/// person detection. Detection errors never delete the source image.
/// </summary>
public sealed class YoloPersonImageDetector : IPersonImageDetector
{
    private const string PreferredModelFileName = "person-detection.onnx";
    private const string Yolo11SearchPattern = "yolo11*.onnx";
    private static readonly Regex Yolo11ModelFileNameRegex = new(
        @"^yolo11[a-z]?\.onnx$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const long MinimumModelBytes = 1_000_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Yolo? _yolo;
    private string? _loadedModelPath;
    private bool _modelReadyLogged;
    private bool _disposed;

    public PersonDetectionModelInfo GetModelInfo()
    {
        var modelPath = FindModelPath();
        return modelPath is null
            ? new PersonDetectionModelInfo(false, null, null)
            : new PersonDetectionModelInfo(
                true,
                Path.GetFileName(modelPath),
                modelPath);
    }

    public async Task<PersonImageDetectionResult> DetectAsync(
        string imagePath,
        Action<string>? log = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return new PersonImageDetectionResult(
                DetectionSucceeded: false,
                ContainsPerson: false,
                ErrorMessage: "待检测图片不存在。");
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
                    ErrorMessage:
                        "未找到人像检测 ONNX 模型。请在 Models 文件夹中放置 " +
                        "person-detection.onnx，或名称为 yolo11 加任意单个字母的 ONNX 模型" +
                        "（例如 yolo11n.onnx、yolo11m.onnx）。检测已跳过，图片会保留。");
            }

            if (_yolo is null
                || !string.Equals(_loadedModelPath, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                _yolo?.Dispose();
                _yolo = new Yolo(new YoloOptions
                {
                    ExecutionProvider = new CpuExecutionProvider(modelPath)
                });
                _loadedModelPath = modelPath;
                _modelReadyLogged = false;
            }

            if (!_modelReadyLogged)
            {
                _modelReadyLogged = true;
                log?.Invoke($"YoloDotNet 人像检测模型已就绪：{modelPath}");
            }

            return await Task.Run(
                () => DetectCore(imagePath),
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

    private PersonImageDetectionResult DetectCore(string imagePath)
    {
        using var image = SKBitmap.Decode(imagePath);
        if (image is null)
        {
            return new PersonImageDetectionResult(
                DetectionSucceeded: false,
                ContainsPerson: false,
                ErrorMessage: "SkiaSharp 无法解码该图片格式。");
        }

        var results = _yolo!.RunObjectDetection(image, confidence: 0.20, iou: 0.70);
        foreach (var result in results)
        {
            object boxedResult = result!;
            var label = boxedResult.GetType()
                .GetProperty("Label", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(boxedResult);
            if (label is null)
                continue;

            var labelType = label.GetType();
            var labelName = labelType
                .GetProperty("Name", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(label)?
                .ToString();
            if (string.Equals(labelName, "person", StringComparison.OrdinalIgnoreCase))
                return new PersonImageDetectionResult(true, true);

            // COCO 的 person 类别 ID 为 0。若模型没有暴露英文类别名，则使用 ID 兜底。
            var idValue = labelType
                .GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)?
                .GetValue(label);
            if (idValue is not null
                && int.TryParse(idValue.ToString(), out var labelId)
                && labelId == 0)
            {
                return new PersonImageDetectionResult(true, true);
            }
        }

        return new PersonImageDetectionResult(true, false);
    }

    private static string? FindModelPath()
    {
        var modelDirectories = GetModelDirectories()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 固定名称优先，便于用户显式指定要使用的模型。
        foreach (var directory in modelDirectories)
        {
            var preferredModel = Path.Combine(directory, PreferredModelFileName);
            if (IsValidModelFile(preferredModel))
                return Path.GetFullPath(preferredModel);
        }

        // 未找到固定名称时，在 Models 文件夹中广义搜索 yolo11?.onnx。
        // “?”表示 yolo11 后允许没有字母，或带任意一个字母，例如：
        // yolo11.onnx、yolo11n.onnx、yolo11m.onnx。
        foreach (var directory in modelDirectories)
        {
            foreach (var candidate in EnumerateYolo11Models(directory))
            {
                if (IsValidModelFile(candidate))
                    return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateYolo11Models(string directory)
    {
        if (!Directory.Exists(directory))
            yield break;

        string[] candidates;
        try
        {
            candidates = Directory
                .EnumerateFiles(directory, Yolo11SearchPattern, SearchOption.TopDirectoryOnly)
                .Where(path => Yolo11ModelFileNameRegex.IsMatch(Path.GetFileName(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            yield break;
        }

        foreach (var candidate in candidates)
            yield return candidate;
    }

    private static IEnumerable<string> GetModelDirectories()
    {
        // 首选可执行程序旁边的 Models，便于便携部署。
        yield return Path.Combine(AppContext.BaseDirectory, "Models");

        // 同时兼容按用户存放模型，不要求程序目录可写。
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "HelloCrab", "Models");
    }

    private static bool IsValidModelFile(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= MinimumModelBytes;
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
