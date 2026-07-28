using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using HelloCrab.Core.Services.Settings;

namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel
{
    private string? _remoteApiPortDraft;
    private IRelayCommand? _applyRemoteApiPortCommand;

    /// <summary>桌面远程服务当前应实际监听的端口。</summary>
    public int EffectiveRemoteApiPort => RemoteApiPortState.Current;

    public string RemoteApiPortDraft
    {
        get => _remoteApiPortDraft ??=
            EffectiveRemoteApiPort.ToString(CultureInfo.InvariantCulture);
        set => SetProperty(ref _remoteApiPortDraft, value ?? string.Empty);
    }

    public IRelayCommand ApplyRemoteApiPortCommand
        => _applyRemoteApiPortCommand ??= new RelayCommand(ApplyRemoteApiPort);

    public event EventHandler<int>? RemoteApiPortChanged;

    private void ApplyRemoteApiPort()
    {
        var text = RemoteApiPortDraft.Trim();
        if (!int.TryParse(
                text,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var port)
            || port is < 1024 or > 65535)
        {
            var message = RemotePortText(
                "远程端口无效：请输入 1024–65535 之间的整数。",
                "Invalid remote port. Enter an integer from 1024 to 65535.",
                "リモートポートが無効です。1024～65535 の整数を入力してください。");
            SetRemoteApiStatus(message);
            AddRemoteLog(message);
            return;
        }

        RemoteApiPortDraft = port.ToString(CultureInfo.InvariantCulture);
        if (port == EffectiveRemoteApiPort)
        {
            AddRemoteLog(RemotePortText(
                $"远程端口没有变化：{port}",
                $"The remote port is unchanged: {port}",
                $"リモートポートは変更されていません：{port}"));
            return;
        }

        RemoteApiPortState.Set(port);
        OnPropertyChanged(nameof(EffectiveRemoteApiPort));
        QueueSettingsSave();

        SetRemoteApiStatus(RemoteApiEnabled
            ? RemotePortText(
                $"正在切换远程端口到 {port}…",
                $"Switching the remote port to {port}…",
                $"リモートポートを {port} に切り替えています…")
            : RemotePortText(
                $"已关闭 · 已保存端口 {port}",
                $"Disabled · Port {port} saved",
                $"無効 · ポート {port} を保存しました"));

        AddRemoteLog(RemotePortText(
            $"远程端口已保存：{port}",
            $"Remote port saved: {port}",
            $"リモートポートを保存しました：{port}"));
        RemoteApiPortChanged?.Invoke(this, port);
    }

    private string RemotePortText(string zhCn, string enUs, string jaJp)
    {
        var code = _localization.CurrentLanguageCode;
        if (code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return enUs;
        if (code.StartsWith("ja", StringComparison.OrdinalIgnoreCase))
            return jaJp;
        return zhCn;
    }
}
