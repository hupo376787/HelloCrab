using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace HelloCrab.Core.Services.Logging;

/// <summary>
/// 将运行日志写入程序根目录 Logs/yyyy-MM-dd.log。
/// 文件按本机日期切换，使用 UTF-8（无 BOM）追加写入。
/// </summary>
public sealed partial class DailyFileLogWriter
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false);
    private readonly object _syncRoot = new();
    private readonly string _logDirectory;

    public DailyFileLogWriter()
        : this(Path.Combine(AppContext.BaseDirectory, "Logs"))
    {
    }

    internal DailyFileLogWriter(string logDirectory)
    {
        _logDirectory = logDirectory;
    }

    public void Write(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var now = DateTime.Now;
        var line = TimestampPrefixRegex().IsMatch(message)
            ? message
            : $"[{now:HH:mm:ss}] {message}";
        var filePath = Path.Combine(_logDirectory, $"{now:yyyy-MM-dd}.log");

        try
        {
            lock (_syncRoot)
            {
                Directory.CreateDirectory(_logDirectory);
                File.AppendAllText(filePath, line + Environment.NewLine, Utf8WithoutBom);
            }
        }
        catch (Exception ex)
        {
            // 日志落盘失败不能反过来导致采集任务或 UI 崩溃。
            Debug.WriteLine($"HelloCrab 写入运行日志失败：{ex}");
        }
    }

    [GeneratedRegex(@"^\[\d{2}:\d{2}:\d{2}\]\s", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPrefixRegex();
}
