using FluentScheduler;
using ScheduleEditor.Models;
using ScheduleEditor.Services;

namespace HelloCrab.Core.Services.Scheduling;

/// <summary>
/// HelloCrab 的定时下载运行时。
/// ScheduleEditor 1.0.0 在 Daily/Weekly/Monthly 模式中使用
/// Every(1).Days().At(...)，FluentScheduler 6.0.0 会抛出
/// "Use Everyday instead."。这里使用官方要求的 Everyday().At(...)
/// 创建每天触发器，同时继续复用 ScheduleEditor 的配置、校验和持久化。
/// </summary>
internal sealed class ScheduledDownloadFluentScheduleService : IFluentScheduleService
{
    private readonly SemaphoreSlim _executionGate = new(1, 1);

    private Schedule? _schedule;
    private bool _disposed;

    public ScheduleOptions? CurrentOptions { get; private set; }

    public bool IsRunning => _schedule?.Running == true;

    public DateTimeOffset? NextRun
    {
        get
        {
            if (!IsRunning || CurrentOptions is not { IsEnabled: true } options)
                return null;

            // Weekly/Monthly 的 FluentScheduler 触发器每天唤醒一次，
            // 真实下次业务运行时间必须由 ScheduleCalculator 计算。
            if (options.RepeatType is ScheduleRepeatType.Weekly or ScheduleRepeatType.Monthly)
            {
                return ScheduleCalculator.GetNextRun(
                    options,
                    DateTimeOffset.Now);
            }

            return _schedule?.NextRun is { } nextRun
                ? ToDateTimeOffset(nextRun)
                : ScheduleCalculator.GetNextRun(options, DateTimeOffset.Now);
        }
    }

    public event EventHandler? ScheduleChanged;

    public event EventHandler<ScheduleExecutionEventArgs>? ExecutionStarted;

    public event EventHandler<ScheduleExecutionEventArgs>? ExecutionCompleted;

    public event EventHandler<ScheduleExecutionEventArgs>? ExecutionFailed;

    public event EventHandler<ScheduleExecutionEventArgs>? ExecutionSkipped;

    public void Apply(
        ScheduleOptions options,
        Func<CancellationToken, Task> job)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(job);

        var normalized = options.Normalize();
        ScheduleOptionsValidator.Validate(normalized);

        StopCore(raiseChanged: false);
        CurrentOptions = normalized.DeepClone();

        if (!normalized.IsEnabled)
        {
            ScheduleChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        var appliedOptions = normalized.DeepClone();
        var hour = appliedOptions.ExecutionTime.Hours;
        var minute = appliedOptions.ExecutionTime.Minutes;

        _schedule = appliedOptions.RepeatType switch
        {
            ScheduleRepeatType.EverySeconds => new Schedule(
                cancellationToken => ExecuteJobAsync(job, cancellationToken),
                run => run.Every(appliedOptions.Interval).Seconds()),

            ScheduleRepeatType.EveryMinutes => new Schedule(
                cancellationToken => ExecuteJobAsync(job, cancellationToken),
                run => run.Every(appliedOptions.Interval).Minutes()),

            ScheduleRepeatType.EveryHours => new Schedule(
                cancellationToken => ExecuteJobAsync(job, cancellationToken),
                run => run.Every(appliedOptions.Interval).Hours()),

            // FluentScheduler 6.0.0 要求每天固定时刻必须使用 Everyday。
            ScheduleRepeatType.Daily => new Schedule(
                cancellationToken => ExecuteJobAsync(job, cancellationToken),
                run => run.Everyday().At(hour, minute)),

            // 每周、每月每天在目标时刻唤醒，再由业务条件过滤日期。
            ScheduleRepeatType.Weekly or ScheduleRepeatType.Monthly => new Schedule(
                cancellationToken => ExecuteIfCalendarMatchesAsync(
                    appliedOptions,
                    job,
                    cancellationToken),
                run => run.Everyday().At(hour, minute)),

            ScheduleRepeatType.Cron => new Schedule(
                cancellationToken => ExecuteJobAsync(job, cancellationToken),
                appliedOptions.CronExpression),

            _ => throw new ArgumentOutOfRangeException(
                nameof(options),
                appliedOptions.RepeatType,
                "不支持的重复方式。")
        };

        _schedule.Start();
        ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCore(raiseChanged: true);
    }

    private void StopCore(bool raiseChanged)
    {
        if (_schedule is not null)
        {
            _schedule.Stop();
            _schedule = null;
        }

        if (raiseChanged)
            ScheduleChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task ExecuteIfCalendarMatchesAsync(
        ScheduleOptions options,
        Func<CancellationToken, Task> job,
        CancellationToken cancellationToken)
    {
        var now = DateTime.Now;

        if (options.RepeatType == ScheduleRepeatType.Weekly &&
            !options.WeekDays.Contains(now.DayOfWeek))
        {
            return Task.CompletedTask;
        }

        if (options.RepeatType == ScheduleRepeatType.Monthly &&
            now.Day != options.DayOfMonth)
        {
            return Task.CompletedTask;
        }

        return ExecuteJobAsync(job, cancellationToken);
    }

    private async Task ExecuteJobAsync(
        Func<CancellationToken, Task> job,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.Now;

        if (!await _executionGate.WaitAsync(0, cancellationToken))
        {
            ExecutionSkipped?.Invoke(
                this,
                new ScheduleExecutionEventArgs(
                    startedAt,
                    endedAt: DateTimeOffset.Now,
                    skippedBecauseAlreadyRunning: true));
            return;
        }

        try
        {
            ExecutionStarted?.Invoke(
                this,
                new ScheduleExecutionEventArgs(startedAt));

            await job(cancellationToken);

            ExecutionCompleted?.Invoke(
                this,
                new ScheduleExecutionEventArgs(
                    startedAt,
                    DateTimeOffset.Now));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 停止调度时的取消属于正常流程。
        }
        catch (Exception exception)
        {
            ExecutionFailed?.Invoke(
                this,
                new ScheduleExecutionEventArgs(
                    startedAt,
                    DateTimeOffset.Now,
                    exception));
        }
        finally
        {
            _executionGate.Release();
        }
    }

    private static DateTimeOffset ToDateTimeOffset(DateTime dateTime)
    {
        return dateTime.Kind switch
        {
            DateTimeKind.Utc => new DateTimeOffset(dateTime),
            DateTimeKind.Local => new DateTimeOffset(dateTime),
            _ => new DateTimeOffset(
                dateTime,
                TimeZoneInfo.Local.GetUtcOffset(dateTime))
        };
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_schedule is not null)
        {
            try
            {
                _schedule.StopAndBlock(TimeSpan.FromSeconds(5));
            }
            catch
            {
                _schedule.Stop();
            }

            _schedule = null;
        }

        // 任务可能仍在 finally 中释放信号量，因此不主动 Dispose。
        GC.SuppressFinalize(this);
    }
}
