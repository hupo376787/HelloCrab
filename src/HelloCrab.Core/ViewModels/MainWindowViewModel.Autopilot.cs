namespace HelloCrab.Core.ViewModels;

public sealed partial class MainWindowViewModel
{
    internal void ApplyAutopilotBranding()
    {
        if (_scheduledDownloadLocalization is null)
            return;

        const string titleOverride = """
            {
              "Title": "Autopilot"
            }
            """;

        _scheduledDownloadLocalization.AddOrUpdateLanguageOverridesFromJson(
            "zh-CN",
            titleOverride);
        _scheduledDownloadLocalization.AddOrUpdateLanguageOverridesFromJson(
            "en-US",
            titleOverride);

        if (_scheduledDownloadLocalization.AvailableLanguages.Any(language =>
                language.Culture.Equals("ja-JP", StringComparison.OrdinalIgnoreCase)))
        {
            _scheduledDownloadLocalization.AddOrUpdateLanguageOverridesFromJson(
                "ja-JP",
                titleOverride);
        }
    }
}