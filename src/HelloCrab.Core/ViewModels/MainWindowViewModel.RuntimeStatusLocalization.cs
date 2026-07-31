using System.ComponentModel;

namespace HelloCrab.Core.ViewModels;

public enum RemoteApiStatusKind
{
    NotStarted,
    Starting,
    RunningPort,
    RunningAddresses,
    Stopping,
    Stopped,
    StartFailedPortInUse,
    StartFailed
}

public sealed partial class MainWindowViewModel
{
    private bool _runtimeStatusLocalizationInitialized;
    private bool _runtimeStatusLocalizationUpdating;
    private RemoteApiStatusKind _remoteApiStatusKind = RemoteApiStatusKind.NotStarted;
    private string? _remoteApiStatusDetail;

    /// <summary>
    /// 桌面宿主在 ViewModel 创建后调用一次。运行状态不依赖磁盘语言包是否已经更新；
    /// 旧语言包缺少新键时，仍按当前中、英、日语言使用正确的回退文案。
    /// </summary>
    public void InitializeRuntimeStatusLocalization()
    {
        if (_runtimeStatusLocalizationInitialized)
            return;

        _runtimeStatusLocalizationInitialized = true;
        PropertyChanged += RuntimeStatusLocalization_PropertyChanged;
        _localization.LanguageChanged += RuntimeStatusLocalization_LanguageChanged;

        RefreshLocalizedPersonDetectionModelStatus();
        RefreshLocalizedRemoteApiStatus();
    }

    public void SetRemoteApiStatus(RemoteApiStatusKind kind, string? detail = null)
    {
        Ui(() =>
        {
            _remoteApiStatusKind = kind;
            _remoteApiStatusDetail = detail;
            RefreshLocalizedRemoteApiStatus();
        });
    }

    private void RuntimeStatusLocalization_LanguageChanged(object? sender, EventArgs e)
        => Ui(() =>
        {
            RefreshLocalizedPersonDetectionModelStatus();
            RefreshLocalizedRemoteApiStatus();
        });

    private void RuntimeStatusLocalization_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (_runtimeStatusLocalizationUpdating)
            return;

        // 旧采集逻辑仍会在模型状态变化时生成一次中文状态。这里立即根据当前语言
        // 重新生成，保证模型下载、开关变化和运行时重新检测后也不会回到中文。
        if (e.PropertyName == nameof(PersonDetectionModelStatusText))
            RefreshLocalizedPersonDetectionModelStatus();
    }

    private void RefreshLocalizedPersonDetectionModelStatus()
    {
        var modelInfo = _personImageDetector.GetModelInfo();
        var text = modelInfo.IsFound
            ? FormatRuntimeStatusText(
                "Runtime.PersonDetection.ModelFound",
                "已发现 YOLO 模型：{0}\n位置：{1}",
                "YOLO model found: {0}\nLocation: {1}",
                "YOLO モデルを検出しました：{0}\n場所：{1}",
                modelInfo.ModelName,
                modelInfo.ModelPath)
            : RuntimeStatusText(
                "Runtime.PersonDetection.ModelMissing",
                "未发现 YOLO 模型。请将 person-detection.onnx、yolo11.onnx，或 yolo11 后带任意一个字母的 ONNX 模型放入程序根目录的 Models 文件夹。",
                "YOLO model not found. Put person-detection.onnx, yolo11.onnx, or a yolo11 ONNX model with any one-letter suffix in the Models folder beside the application.",
                "YOLO モデルが見つかりません。person-detection.onnx、yolo11.onnx、または yolo11 の後に任意の英字 1 文字が付く ONNX モデルを、アプリと同じ場所の Models フォルダーに配置してください。");

        SetRuntimeStatusText(() => PersonDetectionModelStatusText = text);
    }

    private void RefreshLocalizedRemoteApiStatus()
    {
        var detail = _remoteApiStatusDetail ?? string.Empty;
        var text = _remoteApiStatusKind switch
        {
            RemoteApiStatusKind.Starting => RuntimeStatusText(
                "Runtime.Remote.Starting",
                "正在启动远程服务器…",
                "Starting remote server…",
                "リモートサーバーを起動しています…"),
            RemoteApiStatusKind.RunningPort => FormatRuntimeStatusText(
                "Runtime.Remote.RunningPort",
                "运行中 · 端口 {0}",
                "Running · Port {0}",
                "稼働中 · ポート {0}",
                detail),
            RemoteApiStatusKind.RunningAddresses => FormatRuntimeStatusText(
                "Runtime.Remote.RunningAddresses",
                "运行中 · {0}",
                "Running · {0}",
                "稼働中 · {0}",
                detail),
            RemoteApiStatusKind.Stopping => RuntimeStatusText(
                "Runtime.Remote.Stopping",
                "正在关闭远程服务器…",
                "Stopping remote server…",
                "リモートサーバーを停止しています…"),
            RemoteApiStatusKind.Stopped => RuntimeStatusText(
                "Runtime.Remote.Stopped",
                "已关闭 · 手机和网页端无法连接",
                "Stopped · Mobile and web clients cannot connect",
                "停止済み · モバイル端末と Web クライアントは接続できません"),
            RemoteApiStatusKind.StartFailedPortInUse => FormatRuntimeStatusText(
                "Runtime.Remote.StartFailedPortInUse",
                "启动失败：端口 {0} 已被占用，请修改远程端口后保存。",
                "Start failed: port {0} is already in use. Change the remote port and save it.",
                "起動に失敗しました：ポート {0} は既に使用されています。リモートポートを変更して保存してください。",
                detail),
            RemoteApiStatusKind.StartFailed => FormatRuntimeStatusText(
                "Runtime.Remote.StartFailed",
                "启动失败：{0}",
                "Start failed: {0}",
                "起動に失敗しました：{0}",
                detail),
            _ => RuntimeStatusText(
                "Runtime.Remote.NotStarted",
                "远程服务器未启动",
                "Remote server not started",
                "リモートサーバーは起動していません")
        };

        SetRuntimeStatusText(() => RemoteApiStatusText = text);
    }

    private void SetRuntimeStatusText(Action setter)
    {
        if (_runtimeStatusLocalizationUpdating)
            return;

        _runtimeStatusLocalizationUpdating = true;
        try
        {
            setter();
        }
        finally
        {
            _runtimeStatusLocalizationUpdating = false;
        }
    }

    private string RuntimeStatusText(
        string key,
        string chinese,
        string english,
        string japanese)
    {
        var fallback = CurrentRuntimeLanguageFallback(chinese, english, japanese);
        return _localization.Get(key, fallback);
    }

    private string FormatRuntimeStatusText(
        string key,
        string chinese,
        string english,
        string japanese,
        params object?[] arguments)
    {
        var fallback = CurrentRuntimeLanguageFallback(chinese, english, japanese);
        var template = _localization.Get(key, fallback);
        try
        {
            return string.Format(template, arguments);
        }
        catch (FormatException)
        {
            return string.Format(fallback, arguments);
        }
    }

    private string CurrentRuntimeLanguageFallback(
        string chinese,
        string english,
        string japanese)
    {
        var code = _localization.CurrentLanguageCode;
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return english;
        if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return japanese;
        return chinese;
    }
}
