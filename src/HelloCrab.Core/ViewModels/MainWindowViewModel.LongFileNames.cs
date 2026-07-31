using HelloCrab.Core.Services.Settings;

namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel
{
    private bool _longFileNameLocalizationSubscribed;

    public bool EnableLongFileNames
    {
        get => LongFileNameState.Enabled;
        set
        {
            if (LongFileNameState.Enabled == value)
                return;

            LongFileNameState.Set(value);
            OnPropertyChanged(nameof(EnableLongFileNames));
            QueueSettingsSave();
        }
    }

    public string LongFileNameSettingText
    {
        get
        {
            EnsureLongFileNameLocalizationSubscription();
            return _localization.Get(
                "Download.LongFileNames",
                LongFileNameLocalizedText(
                    "解除长路径限制",
                    "Allow longer file names",
                    "長いファイル名を許可"));
        }
    }

    public string LongFileNameSettingDescriptionText
    {
        get
        {
            EnsureLongFileNameLocalizationSubscription();
            return _localization.Get(
                "Download.LongFileNamesDescription",
                LongFileNameLocalizedText(
                    "开启后作品标题文件名最长由 170 提高到 220 个字符；仍受操作系统和文件系统限制。",
                    "When enabled, work title file names can grow from 170 to 220 characters. Operating-system and file-system limits still apply.",
                    "有効にすると、作品タイトルのファイル名上限を 170 文字から 220 文字へ拡張します。OS とファイルシステムの制限は引き続き適用されます。"));
        }
    }

    private void EnsureLongFileNameLocalizationSubscription()
    {
        if (_longFileNameLocalizationSubscribed)
            return;

        _longFileNameLocalizationSubscribed = true;
        _localization.LanguageChanged += (_, _) => Ui(() =>
        {
            OnPropertyChanged(nameof(LongFileNameSettingText));
            OnPropertyChanged(nameof(LongFileNameSettingDescriptionText));
        });
    }

    private string LongFileNameLocalizedText(
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
