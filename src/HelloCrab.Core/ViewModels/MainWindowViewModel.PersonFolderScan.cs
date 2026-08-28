using System.Diagnostics;

namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel
{
    private static readonly HashSet<string> PersonFolderScanImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".webp", ".bmp", ".gif",
        ".tif", ".tiff", ".heic", ".heif", ".avif"
    };

    private CancellationTokenSource? _personFolderScanCts;
    private bool _isPersonFolderScanRunning;
    private string _personFolderScanStatusText = string.Empty;

    public bool IsPersonFolderScanRunning
    {
        get => _isPersonFolderScanRunning;
        private set
        {
            if (SetProperty(ref _isPersonFolderScanRunning, value))
                OnPropertyChanged(nameof(CanStartPersonFolderScan));
        }
    }

    public bool CanStartPersonFolderScan
        => !IsPersonFolderScanRunning
           && !IsBusy
           && !IsCapturing
           && !IsScheduledBatchRunning
           && !IsManualBatchRunning;

    public string PersonFolderScanStatusText
    {
        get => _personFolderScanStatusText;
        private set => SetProperty(ref _personFolderScanStatusText, value ?? string.Empty);
    }

    public void CancelPersonFolderScan()
        => _personFolderScanCts?.Cancel();

    public async Task ScanPersonFolderAsync(
        string? folderPath,
        CancellationToken cancellationToken = default)
    {
        if (!CanStartPersonFolderScan)
        {
            var message = PersonFolderScanText(
                "PersonScan.Busy",
                "当前已有下载、批量或其他任务运行，不能开始文件夹人像扫描。",
                "A download, batch, or other task is already running. Folder person scanning cannot start.",
                "ダウンロード、一括処理、または別のタスクが実行中のため、フォルダー人物スキャンを開始できません。");
            PersonFolderScanStatusText = message;
            AddLog(message);
            return;
        }

        if (string.IsNullOrWhiteSpace(folderPath))
            return;

        string normalizedFolder;
        try
        {
            normalizedFolder = Path.GetFullPath(folderPath);
        }
        catch (Exception ex)
        {
            var message = PersonFolderScanText(
                "PersonScan.InvalidFolder",
                "扫描文件夹路径无效：{0}",
                "The scan folder path is invalid: {0}",
                "スキャン対象フォルダーのパスが無効です：{0}",
                ex.Message);
            PersonFolderScanStatusText = message;
            AddLog(message);
            return;
        }

        if (!Directory.Exists(normalizedFolder))
        {
            var message = PersonFolderScanText(
                "PersonScan.FolderMissing",
                "扫描文件夹不存在：{0}",
                "The scan folder does not exist: {0}",
                "スキャン対象フォルダーが存在しません：{0}",
                normalizedFolder);
            PersonFolderScanStatusText = message;
            AddLog(message);
            return;
        }

        var modelInfo = RefreshPersonDetectionModelStatus();
        if (!modelInfo.IsFound)
        {
            var message = PersonFolderScanText(
                "PersonScan.ModelMissing",
                "未找到 YOLO 人像检测模型，无法开始扫描。",
                "No YOLO person-detection model was found, so the scan cannot start.",
                "YOLO 人物検出モデルが見つからないため、スキャンを開始できません。");
            PersonFolderScanStatusText = message;
            AddLog(message);
            return;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _personFolderScanCts = linkedCts;
        IsPersonFolderScanRunning = true;
        IsBusy = true;
        RefreshCommands();

        var scannedCount = 0;
        var deletedCount = 0;
        var failedCount = 0;
        var totalCount = 0;

        try
        {
            PersonFolderScanStatusText = PersonFolderScanText(
                "PersonScan.Enumerating",
                "正在扫描文件夹中的图片…",
                "Finding images in the folder…",
                "フォルダー内の画像を検索しています…");

            var imagePaths = await Task.Run(
                () => EnumeratePersonFolderScanImages(normalizedFolder),
                linkedCts.Token);
            totalCount = imagePaths.Length;

            AddLog(PersonFolderScanText(
                "PersonScan.Started",
                "开始人像扫描：{0}；共发现 {1} 张图片；检测置信度 {2}。",
                "Person scan started: {0}; {1} images found; confidence {2}.",
                "人物スキャンを開始：{0}；画像 {1} 枚；検出信頼度 {2}。",
                normalizedFolder,
                totalCount,
                PersonDetectionConfidenceText));

            if (totalCount == 0)
            {
                var emptySummary = PersonFolderScanText(
                    "PersonScan.Completed",
                    "扫描完成：扫描 {0} 张，删除 {1} 张，保留 {2} 张，失败 {3} 张。",
                    "Scan complete: scanned {0}, deleted {1}, kept {2}, failed {3}.",
                    "スキャン完了：{0} 枚をスキャン、{1} 枚削除、{2} 枚保持、{3} 枚失敗。",
                    0,
                    0,
                    0,
                    0);
                PersonFolderScanStatusText = emptySummary;
                StatusText = emptySummary;
                return;
            }

            var progressClock = Stopwatch.StartNew();
            foreach (var imagePath in imagePaths)
            {
                linkedCts.Token.ThrowIfCancellationRequested();

                try
                {
                    var detection = await _personImageDetector.DetectAsync(
                        imagePath,
                        PersonDetectionConfidence,
                        log: null,
                        cancellationToken: linkedCts.Token);
                    scannedCount++;

                    if (!detection.DetectionSucceeded)
                    {
                        // 与下载时的人像检测策略一致：检测失败绝不删除源图片。
                        failedCount++;
                    }
                    else if (!detection.ContainsPerson)
                    {
                        try
                        {
                            File.Delete(imagePath);
                            deletedCount++;
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            // 删除失败同样保留图片，并计入失败。
                            failedCount++;
                        }
                    }
                }
                catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // 单张图片异常不影响剩余文件；防误删，原文件保持不动。
                    scannedCount++;
                    failedCount++;
                }

                if (scannedCount == totalCount || progressClock.ElapsedMilliseconds >= 250)
                {
                    progressClock.Restart();
                    PersonFolderScanStatusText = PersonFolderScanText(
                        "PersonScan.Progress",
                        "扫描中：{0}/{1}，已删除 {2} 张，检测失败 {3} 张。",
                        "Scanning: {0}/{1}, deleted {2}, detection failures {3}.",
                        "スキャン中：{0}/{1}、削除 {2} 枚、検出失敗 {3} 枚。",
                        scannedCount,
                        totalCount,
                        deletedCount,
                        failedCount);
                }
            }

            var keptCount = Math.Max(0, scannedCount - deletedCount);
            var summary = PersonFolderScanText(
                "PersonScan.Completed",
                "扫描完成：扫描 {0} 张，删除 {1} 张，保留 {2} 张，失败 {3} 张。",
                "Scan complete: scanned {0}, deleted {1}, kept {2}, failed {3}.",
                "スキャン完了：{0} 枚をスキャン、{1} 枚削除、{2} 枚保持、{3} 枚失敗。",
                scannedCount,
                deletedCount,
                keptCount,
                failedCount);
            PersonFolderScanStatusText = summary;
            StatusText = summary;
        }
        catch (OperationCanceledException)
        {
            var canceled = PersonFolderScanText(
                "PersonScan.Canceled",
                "人像扫描已取消：已扫描 {0}/{1} 张，删除 {2} 张。",
                "Person scan canceled: scanned {0}/{1}, deleted {2}.",
                "人物スキャンをキャンセルしました：{0}/{1} 枚をスキャン、{2} 枚削除。",
                scannedCount,
                totalCount,
                deletedCount);
            PersonFolderScanStatusText = canceled;
            AddLog(canceled);
        }
        catch (Exception ex)
        {
            var failed = PersonFolderScanText(
                "PersonScan.Failed",
                "人像扫描失败：{0}",
                "Person scan failed: {0}",
                "人物スキャンに失敗しました：{0}",
                ex.Message);
            PersonFolderScanStatusText = failed;
            AddLog(failed);
        }
        finally
        {
            if (ReferenceEquals(_personFolderScanCts, linkedCts))
                _personFolderScanCts = null;

            // 先保持扫描状态，再释放通用忙碌状态，避免按钮在两个状态切换间短暂可用。
            IsBusy = false;
            IsPersonFolderScanRunning = false;
            OnPropertyChanged(nameof(CanStartPersonFolderScan));
            RefreshCommands();
        }
    }

    private static string[] EnumeratePersonFolderScanImages(string rootFolder)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        return Directory
            .EnumerateFiles(rootFolder, "*", options)
            .Where(path => PersonFolderScanImageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private string PersonFolderScanText(
        string key,
        string zhCn,
        string enUs,
        string jaJp,
        params object?[] arguments)
    {
        var code = _localization.CurrentLanguageCode;
        var fallback = code.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? zhCn
            : code.StartsWith("ja", StringComparison.OrdinalIgnoreCase)
                ? jaJp
                : enUs;
        var template = _localization.Get(key, fallback);
        return arguments.Length == 0
            ? template
            : string.Format(template, arguments);
    }
}
