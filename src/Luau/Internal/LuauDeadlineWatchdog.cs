using System.Diagnostics;

namespace Luau;

/// <summary>
/// One process-wide timer that backs up the native host's wall-clock check.
/// The host reads its clock only every 1 to 128 safepoints, so after an abrupt
/// slowdown it can notice a deadline late; the watchdog asks the root to poll
/// at the first safepoint after the deadline instead, and cancels the
/// callback-visible token at the deadline.
/// </summary>
/// <remarks>
/// Each root publishes the deadline of its active operation in
/// <see cref="LuauVmContext.WatchdogDeadline"/>, so starting an operation
/// costs a fenced store and a read, not a timer. The timer is armed for the
/// earliest deadline it knows of and re-armed only when an operation starts
/// with an earlier one, or when it fires and finds a later one still running.
/// It therefore fires at an active operation's deadline plus the timer's own
/// resolution (about 1 ms on Linux and Android, up to about 16 ms with the
/// default Windows timer resolution), the same bound as a per-operation timer,
/// and does nothing while no operation with a wall-clock limit is running.
/// </remarks>
internal static class LuauDeadlineWatchdog
{
    const long Idle = long.MaxValue;

    static readonly object gate = new();
    static readonly List<WeakReference<LuauVmContext>> contexts = [];
    static readonly WaitCallback cancelCallback = static source => CancelQuietly((CancellationTokenSource)source!);
    static Timer? timer;
    static long armedDue = Idle;

    /// <summary>Gets the timestamp the timer is armed for, or <see cref="long.MaxValue"/>.</summary>
    internal static long ArmedDue => Volatile.Read(ref armedDue);

    /// <summary>
    /// Adds a root to the set the watchdog scans. Called once per root,
    /// before its first deadline is published and outside the root's gates.
    /// </summary>
    internal static void Register(WeakReference<LuauVmContext> context)
    {
        lock (gate)
        {
            if (contexts.Count == contexts.Capacity && contexts.Count > 0)
            {
                PruneLocked();
            }

            contexts.Add(context);
        }
    }

    /// <summary>
    /// Makes sure the timer fires no later than <paramref name="deadline"/>.
    /// The caller has already published that deadline with a full fence;
    /// <see cref="Tick"/> marks the timer idle with a full fence before it
    /// scans. So either the scan sees the deadline, or this reads the idle
    /// mark and arms the timer itself.
    /// </summary>
    internal static void EnsureArmed(long deadline)
    {
        if (Volatile.Read(ref armedDue) <= deadline)
        {
            return;
        }

        lock (gate)
        {
            if (armedDue <= deadline)
            {
                return;
            }

            ArmLocked(deadline, Stopwatch.GetTimestamp());
        }
    }

    static void ArmLocked(long deadline, long now)
    {
        var remaining = deadline - now;
        // Round up so the timer does not fire just before the deadline.
        var delayMilliseconds = remaining <= 0
            ? 0
            : (long)Math.Ceiling(remaining * 1000d / Stopwatch.Frequency);
        if (delayMilliseconds > int.MaxValue - 1)
        {
            delayMilliseconds = int.MaxValue - 1;
        }

        timer ??= new Timer(static _ => Tick(), null, Timeout.Infinite, Timeout.Infinite);
        timer.Change(delayMilliseconds, Timeout.Infinite);
        // Only once the timer accepted the change: a failed arm leaves the
        // watchdog idle, so the next operation tries again.
        Volatile.Write(ref armedDue, deadline);
    }

    static void Tick()
    {
        LuauVmContext[]? expired = null;
        var expiredCount = 0;
        try
        {
            lock (gate)
            {
                Interlocked.Exchange(ref armedDue, Idle);
                var now = Stopwatch.GetTimestamp();
                var next = Idle;
                for (var index = contexts.Count - 1; index >= 0; index--)
                {
                    if (!contexts[index].TryGetTarget(out var context) || context.IsClosed)
                    {
                        RemoveAtLocked(index);
                        continue;
                    }

                    var deadline = context.WatchdogDeadline;
                    if (deadline == 0)
                    {
                        continue;
                    }

                    if (deadline <= now)
                    {
                        expired ??= new LuauVmContext[Math.Min(contexts.Count, 4)];
                        if (expiredCount == expired.Length)
                        {
                            Array.Resize(ref expired, expired.Length * 2);
                        }

                        expired[expiredCount++] = context;
                    }
                    else if (deadline < next)
                    {
                        next = deadline;
                    }
                }

                if (next != Idle)
                {
                    ArmLocked(next, now);
                }
            }
        }
        catch
        {
            // A timer callback must not fail; a later operation re-arms.
        }

        if (expired == null)
        {
            return;
        }

        // Ask every expired root to poll first. Canceling a token runs host
        // registrations, which may block; give each root's its own work item
        // so one root's handler cannot delay another root's token.
        var sources = new CancellationTokenSource?[expiredCount];
        for (var index = 0; index < expiredCount; index++)
        {
            try
            {
                sources[index] = expired[index].SignalWatchdogDeadline();
            }
            catch
            {
            }
        }

        var last = -1;
        for (var index = 0; index < expiredCount; index++)
        {
            if (sources[index] == null)
            {
                continue;
            }

            if (last >= 0)
            {
                QueueCancel(sources[last]!);
            }

            last = index;
        }

        if (last >= 0)
        {
            CancelQuietly(sources[last]!);
        }
    }

    static void QueueCancel(CancellationTokenSource source)
    {
        try
        {
            ThreadPool.UnsafeQueueUserWorkItem(cancelCallback, source);
        }
        catch
        {
            CancelQuietly(source);
        }
    }

    static void CancelQuietly(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch
        {
            // The operation ended and disposed the source first, or a host
            // registration threw. Either way the poll was already requested.
        }
    }

    static void PruneLocked()
    {
        for (var index = contexts.Count - 1; index >= 0; index--)
        {
            if (!contexts[index].TryGetTarget(out var context) || context.IsClosed)
            {
                RemoveAtLocked(index);
            }
        }
    }

    static void RemoveAtLocked(int index)
    {
        var last = contexts.Count - 1;
        contexts[index] = contexts[last];
        contexts.RemoveAt(last);
    }
}
