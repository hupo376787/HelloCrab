using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HelloCrab.Core.Services.Localization;
using HelloCrab.Core.ViewModels;

namespace HelloCrab.Core.Views;

public partial class MainWindow
{
    private static readonly IDisposable BatchLogTitleDataContextHandler =
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>((window, _) =>
            Dispatcher.UIThread.Post(
                window.InstallBatchLogTitle,
                DispatcherPriority.Loaded));

    private MainWindowViewModel? _batchLogTitleViewModel;
    private ObservableCollection<string>? _batchLogTitleLogs;
    private TextBlock? _batchLogProgressText;
    private bool _wasManualBatchRunningForLogTitle;
    private bool _wasScheduledBatchRunningForLogTitle;
    private bool _batchLogTitleClosedHooked;
    private int _batchLogTitleInstallAttempts;

    private void InstallBatchLogTitle()
    {
        if (DataContext is not MainWindowViewModel viewModel)
            return;

        if (_batchLogTitleLogs is not null)
            _batchLogTitleLogs.CollectionChanged -= BatchLogTitleLogs_CollectionChanged;
        if (_batchLogTitleViewModel is not null)
            _batchLogTitleViewModel.PropertyChanged -= BatchLogTitleViewModel_PropertyChanged;

        _batchLogTitleViewModel = viewModel;
        _batchLogTitleLogs = viewModel.Logs;
        _batchLogTitleLogs.CollectionChanged += BatchLogTitleLogs_CollectionChanged;
        _batchLogTitleViewModel.PropertyChanged += BatchLogTitleViewModel_PropertyChanged;
        _wasManualBatchRunningForLogTitle = viewModel.IsManualBatchRunning;
        _wasScheduledBatchRunningForLogTitle = viewModel.IsScheduledBatchRunning;

        EnsureBatchLogTitleUi();
        if (_batchLogProgressText is null && _batchLogTitleInstallAttempts++ < 5)
        {
            Dispatcher.UIThread.Post(
                EnsureBatchLogTitleUi,
                DispatcherPriority.Background);
        }

        if (!_batchLogTitleClosedHooked)
        {
            _batchLogTitleClosedHooked = true;
            Closed += BatchLogTitleWindow_Closed;
        }
    }

    private void EnsureBatchLogTitleUi()
    {
        if (_batchLogProgressText is not null)
            return;

        var logTitle = LocalizationService.Current?.Get("Log.Title", "运行日志") ?? "运行日志";
        var titleBlock = this
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(textBlock => string.Equals(
                textBlock.Text,
                logTitle,
                StringComparison.Ordinal));
        if (titleBlock?.Parent is not Grid titleGrid)
            return;

        var row = Grid.GetRow(titleBlock);
        var column = Grid.GetColumn(titleBlock);
        var rowSpan = Grid.GetRowSpan(titleBlock);
        var columnSpan = Grid.GetColumnSpan(titleBlock);

        titleGrid.Children.Remove(titleBlock);

        var titleRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRow(titleRow, row);
        Grid.SetColumn(titleRow, column);
        Grid.SetRowSpan(titleRow, rowSpan);
        Grid.SetColumnSpan(titleRow, columnSpan);

        titleRow.Children.Add(titleBlock);

        _batchLogProgressText = new TextBlock
        {
            Text = string.Empty,
            FontSize = 18,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#EF4444")),
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false
        };
        titleRow.Children.Add(_batchLogProgressText);
        titleGrid.Children.Add(titleRow);
    }

    private void BatchLogTitleLogs_CollectionChanged(
        object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (_batchLogTitleViewModel is not { } viewModel
            || e.NewItems is null
            || (!viewModel.IsManualBatchRunning && !viewModel.IsScheduledBatchRunning))
        {
            return;
        }

        foreach (var item in e.NewItems)
        {
            if (item is not string logText)
                continue;

            if (viewModel.IsManualBatchRunning
                && TryReadFormattedProgress(
                    logText,
                    LocalizationService.Current?.Get(
                        "Batch.Log.ItemStarted",
                        "批量任务 {0}/{1}：正在处理 {2}，地址：{3}")
                    ?? "批量任务 {0}/{1}：正在处理 {2}，地址：{3}",
                    currentArgument: 0,
                    totalArgument: 1,
                    out var manualCurrent,
                    out var manualTotal))
            {
                SetBatchLogProgress(manualCurrent, manualTotal);
                return;
            }

            if (viewModel.IsScheduledBatchRunning
                && TryReadFormattedProgress(
                    logText,
                    LocalizationService.Current?.Get(
                        "Schedule.Log.ItemStarted",
                        "定时任务正在重新采集：{0}（第 {1}/{2} 个）")
                    ?? "定时任务正在重新采集：{0}（第 {1}/{2} 个）",
                    currentArgument: 1,
                    totalArgument: 2,
                    out var scheduledCurrent,
                    out var scheduledTotal))
            {
                SetBatchLogProgress(scheduledCurrent, scheduledTotal);
                return;
            }
        }
    }

    private void BatchLogTitleViewModel_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is not MainWindowViewModel viewModel)
            return;

        if (e.PropertyName == nameof(MainWindowViewModel.IsManualBatchRunning))
        {
            if (viewModel.IsManualBatchRunning && !_wasManualBatchRunningForLogTitle)
                ClearBatchLogProgress();
            else if (!viewModel.IsManualBatchRunning && !viewModel.IsScheduledBatchRunning)
                ClearBatchLogProgress();

            _wasManualBatchRunningForLogTitle = viewModel.IsManualBatchRunning;
            return;
        }

        if (e.PropertyName == nameof(MainWindowViewModel.IsScheduledBatchRunning))
        {
            if (viewModel.IsScheduledBatchRunning && !_wasScheduledBatchRunningForLogTitle)
                ClearBatchLogProgress();
            else if (!viewModel.IsScheduledBatchRunning && !viewModel.IsManualBatchRunning)
                ClearBatchLogProgress();

            _wasScheduledBatchRunningForLogTitle = viewModel.IsScheduledBatchRunning;
        }
    }

    private void SetBatchLogProgress(int current, int total)
    {
        EnsureBatchLogTitleUi();
        if (_batchLogProgressText is null || current <= 0 || total <= 0)
            return;

        _batchLogProgressText.Text = $"({current} / {total})";
        _batchLogProgressText.IsVisible = true;
    }

    private void ClearBatchLogProgress()
    {
        if (_batchLogProgressText is null)
            return;

        _batchLogProgressText.Text = string.Empty;
        _batchLogProgressText.IsVisible = false;
    }

    private static bool TryReadFormattedProgress(
        string logText,
        string template,
        int currentArgument,
        int totalArgument,
        out int current,
        out int total)
    {
        current = 0;
        total = 0;
        if (string.IsNullOrWhiteSpace(logText) || string.IsNullOrWhiteSpace(template))
            return false;

        var pattern = new StringBuilder();
        var placeholderMatches = Regex.Matches(
            template,
            @"\{(?<index>\d+)(?:[^}]*)\}",
            RegexOptions.CultureInvariant);
        var position = 0;

        foreach (Match placeholder in placeholderMatches)
        {
            pattern.Append(Regex.Escape(template[position..placeholder.Index]));
            if (!int.TryParse(placeholder.Groups["index"].Value, out var argumentIndex))
                return false;

            if (argumentIndex == currentArgument)
                pattern.Append(@"(?<current>\d+)");
            else if (argumentIndex == totalArgument)
                pattern.Append(@"(?<total>\d+)");
            else
                pattern.Append(@".*?");

            position = placeholder.Index + placeholder.Length;
        }

        pattern.Append(Regex.Escape(template[position..]));
        var match = Regex.Match(
            logText,
            pattern.ToString(),
            RegexOptions.CultureInvariant | RegexOptions.Singleline);

        return match.Success
               && int.TryParse(match.Groups["current"].Value, out current)
               && int.TryParse(match.Groups["total"].Value, out total)
               && current > 0
               && total > 0;
    }

    private void BatchLogTitleWindow_Closed(object? sender, EventArgs e)
    {
        if (_batchLogTitleLogs is not null)
            _batchLogTitleLogs.CollectionChanged -= BatchLogTitleLogs_CollectionChanged;
        if (_batchLogTitleViewModel is not null)
            _batchLogTitleViewModel.PropertyChanged -= BatchLogTitleViewModel_PropertyChanged;

        _batchLogTitleLogs = null;
        _batchLogTitleViewModel = null;
        Closed -= BatchLogTitleWindow_Closed;
    }
}
