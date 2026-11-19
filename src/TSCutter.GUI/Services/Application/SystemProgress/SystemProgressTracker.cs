using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace TSCutter.GUI.Services.Application.SystemProgress;

/// <summary>任务栏按钮或 Dock 图标上显示的进度状态。</summary>
public enum SystemProgressState
{
    None,
    Indeterminate,
    Normal,
    Paused,
    Error,
}

/// <summary>某一时刻要显示的进度；Value 取 0 到 1。</summary>
public readonly record struct SystemProgressSnapshot(SystemProgressState State, double Value)
{
    public static SystemProgressSnapshot None { get; } = new(SystemProgressState.None, 0);

    // 比较时按千分之一取整，避免进度的细小抖动引起重复刷新。
    internal bool LooksSameAs(SystemProgressSnapshot other) =>
        State == other.State && Math.Abs(Math.Round(Value * 1000) - Math.Round(other.Value * 1000)) < 0.5;
}

/// <summary>
/// 汇总所有正在进行的长时间操作，得到全局或某个所有者的进度。
/// 各方法可在任意线程调用；进度值的变化只记录不通知，由显示端定时读取。
/// </summary>
internal sealed class SystemProgressTracker
{
    // 程序在前台时出现的失败只短暂显示，避免错误状态一直留在任务栏上。
    internal static readonly TimeSpan ForegroundFailureDuration = TimeSpan.FromSeconds(3);

    private readonly Lock _sync = new();
    private readonly List<SystemProgressOperation> _active = [];
    private readonly List<Failure> _failures = [];
    private readonly TimeProvider _time;
    private readonly Func<bool> _isAppActive;
    private readonly TimeSpan _showDelay;

    /// <param name="showDelay">操作开始后经过这段时间才显示，很快结束的操作不会让任务栏闪一下。</param>
    public SystemProgressTracker(TimeProvider? time = null, Func<bool>? isAppActive = null, TimeSpan showDelay = default)
    {
        _time = time ?? TimeProvider.System;
        _isAppActive = isAppActive ?? (() => true);
        _showDelay = showDelay;
    }

    /// <summary>开始、结束操作或状态改变时触发；单纯的进度值变化不触发。可能在任意线程触发。</summary>
    public event Action? Changed;

    /// <summary>没有进行中的操作，也没有待显示的失败。</summary>
    public bool IsIdle
    {
        get
        {
            lock (_sync)
            {
                PruneExpiredFailures();
                return _active.Count == 0 && _failures.Count == 0;
            }
        }
    }

    /// <summary>登记一个新操作；初始为不确定进度。开始新操作视为用户已看过之前的失败。</summary>
    public SystemProgressOperation Begin(object? owner = null)
    {
        var operation = new SystemProgressOperation(this, owner, _time.GetUtcNow());
        lock (_sync)
        {
            _failures.Clear();
            _active.Add(operation);
        }
        RaiseChanged();
        return operation;
    }

    /// <summary>用户切回程序后，清除在后台期间出现的失败。</summary>
    public void NotifyAppActivated()
    {
        bool removed;
        lock (_sync)
            removed = _failures.RemoveAll(failure => !failure.RecordedWhileActive) > 0;
        if (removed)
            RaiseChanged();
    }

    /// <summary>取得全部操作汇总后的进度。</summary>
    public SystemProgressSnapshot GetSnapshot() => GetSnapshot(null, matchOwner: false);

    /// <summary>只汇总指定所有者登记的操作。</summary>
    public SystemProgressSnapshot GetSnapshot(object owner) => GetSnapshot(owner, matchOwner: true);

    private SystemProgressSnapshot GetSnapshot(object? owner, bool matchOwner)
    {
        lock (_sync)
        {
            PruneExpiredFailures();
            var shownSince = _time.GetUtcNow() - _showDelay;
            var operations = _active
                .Where(operation => operation.StartedAt <= shownSince && (!matchOwner || ReferenceEquals(operation.Owner, owner)))
                .ToList();
            var failures = matchOwner
                ? _failures.Where(failure => ReferenceEquals(failure.Owner, owner)).ToList()
                : _failures;
            if (operations.Count == 0 && failures.Count == 0)
                return SystemProgressSnapshot.None;

            // 多个操作同时进行时取有确定进度的平均值；都不确定时整体显示为不确定。
            var determinate = operations.Where(operation => operation.HasValue).ToList();
            var average = determinate.Count > 0 ? determinate.Average(operation => operation.Value) : (double?)null;

            if (failures.Count > 0 || operations.Any(operation => operation.HasError))
            {
                // 错误状态必须有可见的长度，没有进度可用时整条显示。
                var value = average ?? (failures.Count > 0 ? failures.Max(failure => failure.Value) : 1);
                return new SystemProgressSnapshot(SystemProgressState.Error, value);
            }
            if (operations.All(operation => operation.IsPaused))
                return new SystemProgressSnapshot(SystemProgressState.Paused, average ?? 1);
            return average is { } normal
                ? new SystemProgressSnapshot(SystemProgressState.Normal, normal)
                : new SystemProgressSnapshot(SystemProgressState.Indeterminate, 0);
        }
    }

    internal void End(SystemProgressOperation operation, bool failed)
    {
        lock (_sync)
        {
            if (!_active.Remove(operation))
                return;
            if (failed)
            {
                var value = operation.HasValue && operation.Value > 0 ? operation.Value : 1;
                _failures.Add(new Failure(operation.Owner, value, _time.GetUtcNow(), _isAppActive()));
            }
        }
        RaiseChanged();
    }

    internal Lock SyncRoot => _sync;

    internal void RaiseChanged() => Changed?.Invoke();

    private void PruneExpiredFailures()
    {
        if (_failures.Count == 0)
            return;
        var now = _time.GetUtcNow();
        _failures.RemoveAll(failure => failure.RecordedWhileActive && now - failure.At >= ForegroundFailureDuration);
    }

    private sealed record Failure(object? Owner, double Value, DateTimeOffset At, bool RecordedWhileActive);
}

/// <summary>
/// 一个登记中的长时间操作。结束时调用 Complete、Cancel 或 Fail 之一；
/// 未注明结果就 Dispose 时按完成处理，清除显示。
/// </summary>
public sealed class SystemProgressOperation : IDisposable
{
    private readonly SystemProgressTracker _tracker;
    private double _value;
    private bool _hasValue;
    private bool _isPaused;
    private bool _hasError;
    private bool _ended;

    internal SystemProgressOperation(SystemProgressTracker tracker, object? owner, DateTimeOffset startedAt)
    {
        _tracker = tracker;
        Owner = owner;
        StartedAt = startedAt;
    }

    internal object? Owner { get; }

    internal DateTimeOffset StartedAt { get; }

    internal double Value
    {
        get { lock (_tracker.SyncRoot) return _value; }
    }

    internal bool HasValue
    {
        get { lock (_tracker.SyncRoot) return _hasValue; }
    }

    internal bool IsPaused
    {
        get { lock (_tracker.SyncRoot) return _isPaused; }
    }

    internal bool HasError
    {
        get { lock (_tracker.SyncRoot) return _hasError; }
    }

    /// <summary>报告进度，取 0 到 1；超出范围的值会被截断，非数值忽略。</summary>
    public void Report(double fraction)
    {
        if (double.IsNaN(fraction))
            return;
        bool becameDeterminate;
        lock (_tracker.SyncRoot)
        {
            if (_ended)
                return;
            becameDeterminate = !_hasValue;
            _value = Math.Clamp(fraction, 0, 1);
            _hasValue = true;
        }
        if (becameDeterminate)
            _tracker.RaiseChanged();
    }

    /// <summary>按百分比报告进度，取 0 到 100。</summary>
    public void ReportPercent(double percent) => Report(percent / 100);

    /// <summary>暂时无法给出具体进度。</summary>
    public void SetIndeterminate() => Update(() =>
    {
        var changed = _hasValue;
        _hasValue = false;
        _value = 0;
        return changed;
    });

    public void SetPaused(bool paused) => Update(() =>
    {
        var changed = _isPaused != paused;
        _isPaused = paused;
        return changed;
    });

    /// <summary>操作仍在继续，但已有部分内容失败，例如批量处理中的某一项。</summary>
    public void MarkError() => Update(() =>
    {
        var changed = !_hasError;
        _hasError = true;
        return changed;
    });

    public void Complete() => End(failed: false);

    public void Cancel() => End(failed: false);

    /// <summary>以失败结束，错误状态会保留到用户切回程序。</summary>
    public void Fail() => End(failed: true);

    /// <summary>按异常结束：取消异常按取消处理，其余按失败处理。</summary>
    public void Fail(Exception exception) => End(failed: exception is not OperationCanceledException);

    public void Dispose() => End(failed: false);

    private void End(bool failed)
    {
        lock (_tracker.SyncRoot)
        {
            if (_ended)
                return;
            _ended = true;
        }
        _tracker.End(this, failed);
    }

    private void Update(Func<bool> apply)
    {
        bool changed;
        lock (_tracker.SyncRoot)
        {
            if (_ended)
                return;
            changed = apply();
        }
        if (changed)
            _tracker.RaiseChanged();
    }
}
