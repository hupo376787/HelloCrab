namespace HelloCrab.Core.Services.Localization;

/// <summary>
/// Runtime localization helper for services that are not created through the UI view-model.
/// All user-visible runtime text still comes from the normal language packs; the supplied
/// fallback only protects startup/test scenarios where LocalizationService.Current is null.
/// </summary>
public static class RuntimeLocalization
{
    public static string Get(string key, string fallback)
        => LocalizationService.Current?.Get(key, fallback) ?? fallback;

    public static string Format(string key, string fallback, params object?[] arguments)
    {
        var template = Get(key, fallback);
        try
        {
            return string.Format(template, arguments);
        }
        catch (FormatException)
        {
            try
            {
                return string.Format(fallback, arguments);
            }
            catch (FormatException)
            {
                return template;
            }
        }
    }
}
