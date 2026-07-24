using System.Globalization;

namespace HelloCrab.Core.Utilities;

public static class PlatformFolderHelper
{
    public static string GetFolderName(string? platform)
    {
        var value = platform?.Trim() ?? string.Empty;
        if (value.Equals("bilibili", StringComparison.OrdinalIgnoreCase)
            || value.Equals("b站", StringComparison.OrdinalIgnoreCase)
            || value.Contains("哔哩哔哩", StringComparison.Ordinal))
        {
            return "bilibili";
        }

        if (value.Equals("douyin", StringComparison.OrdinalIgnoreCase)
            || value.Contains("抖音", StringComparison.Ordinal))
        {
            return "douyin";
        }

        if (value.Equals("instagram", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ins", StringComparison.OrdinalIgnoreCase))
        {
            return "instagram";
        }

        if (value.Equals("tiktok", StringComparison.OrdinalIgnoreCase)
            || value.Contains("TikTok", StringComparison.OrdinalIgnoreCase))
        {
            return "tiktok";
        }

        if (value.Equals("pinterest", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Pinterest", StringComparison.OrdinalIgnoreCase))
        {
            return "pinterest";
        }

        if (value.Equals("kuaishou", StringComparison.OrdinalIgnoreCase)
            || value.Contains("快手", StringComparison.Ordinal))
        {
            return "kuaishou";
        }

        if (value.Equals("xiaohongshu", StringComparison.OrdinalIgnoreCase)
            || value.Equals("xhs", StringComparison.OrdinalIgnoreCase)
            || value.Contains("小红书", StringComparison.Ordinal))
        {
            return "xiaohongshu";
        }

        if (value.Equals("weibo", StringComparison.OrdinalIgnoreCase)
            || value.Contains("微博", StringComparison.Ordinal))
        {
            return "weibo";
        }

        if (value.Equals("meipian", StringComparison.OrdinalIgnoreCase)
            || value.Contains("美篇", StringComparison.Ordinal))
        {
            return "meipian";
        }


        if (string.IsNullOrWhiteSpace(value))
            return "other";

        var normalized = value.ToLower(CultureInfo.InvariantCulture);
        return FileNameHelper.Sanitize(normalized, 50);
    }
}
