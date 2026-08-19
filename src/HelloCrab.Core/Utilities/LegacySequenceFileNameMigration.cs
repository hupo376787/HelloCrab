using System.Globalization;
using System.Text.RegularExpressions;

namespace HelloCrab.Core.Utilities;

/// <summary>
/// 临时启动迁移：把历史下载文件末尾的“ 空格+序号”统一为当前“_序号”格式。
/// 后续确认用户目录都迁移完成后可以删除此类及启动调用。
/// </summary>
public static partial class LegacySequenceFileNameMigration
{
    [GeneratedRegex(@"^(?<prefix>\d{4}-\d{2}-\d{2} \d{2}-\d{2}-\d{2}.*) (?<sequence>\d{1,3})$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacySequenceRegex();

    public static void Run(string? downloadRoot)
    {
        if (string.IsNullOrWhiteSpace(downloadRoot) || !Directory.Exists(downloadRoot))
            return;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(downloadRoot, "*", options);
        }
        catch
        {
            return;
        }

        foreach (var sourcePath in files)
        {
            try
            {
                NormalizeFile(sourcePath);
            }
            catch
            {
                // 临时迁移不能影响程序启动；单个文件失败时继续处理其他文件。
            }
        }
    }

    private static void NormalizeFile(string sourcePath)
    {
        var directory = Path.GetDirectoryName(sourcePath);
        if (string.IsNullOrWhiteSpace(directory))
            return;

        var extension = Path.GetExtension(sourcePath);
        var nameWithoutExtension = Path.GetFileNameWithoutExtension(sourcePath);
        var match = LegacySequenceRegex().Match(nameWithoutExtension);
        if (!match.Success)
            return;

        if (!int.TryParse(
                match.Groups["sequence"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence)
            || sequence <= 0)
        {
            return;
        }

        var prefix = match.Groups["prefix"].Value;
        var normalizedSequence = sequence.ToString("D2", CultureInfo.InvariantCulture);
        var targetPath = Path.Combine(directory, $"{prefix}_{normalizedSequence}{extension}");

        if (Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            return;

        if (File.Exists(targetPath))
        {
            // 新旧两种命名同时存在时，以当前下划线格式为准，删除旧空格格式。
            File.Delete(sourcePath);
            return;
        }

        File.Move(sourcePath, targetPath);
    }
}
