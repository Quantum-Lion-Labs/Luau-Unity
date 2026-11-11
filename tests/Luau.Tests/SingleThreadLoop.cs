using System.Collections.Concurrent;

namespace Luau.Tests;

/// <summary>
/// A single-threaded <see cref="SynchronizationContext"/> with a
/// <see cref="LuauSynchronizationContextScheduler"/> captured on it, the shape
/// of Unity's main thread. Like Unity's context, it runs posted work in the
/// loop thread's own execution context, not the poster's.
/// </summary>
/// <remarks>
/// With <c>currentContext: false</c> the loop leaves no context current on
/// its thread once the scheduler is captured, as a scheduler that is not a
/// <see cref="SynchronizationContext"/> would. A task completed there then
/// runs its <c>ConfigureAwait(false)</c> continuations inline, as it does on a
/// thread-pool thread; with a current context it queues them to the pool.
/// </remarks>
internal sealed class SingleThreadLoop : SynchronizationContext, IDisposable
{
    readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> work = new();
    readonly Thread thread;
    readonly bool currentContext;
    readonly TaskCompletionSource<LuauSynchronizationContextScheduler> ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    int postCount;

    internal SingleThreadLoop(bool currentContext = true)
    {
        this.currentContext = currentContext;

        // The loop thread must not inherit the creator's execution context:
        // a test may create it from inside an operation's flow.
        using (ExecutionContext.SuppressFlow())
        {
            thread = new Thread(Pump) { IsBackground = true, Name = "Luau single-thread loop" };
            thread.Start();
        }

        Scheduler = ready.Task.GetAwaiter().GetResult();
    }

    internal LuauSynchronizationContextScheduler Scheduler { get; }

    internal int ManagedThreadId => Scheduler.OwnerManagedThreadId;

    internal Exception? UnhandledException { get; private set; }

    /// <summary>How many items have been posted to the loop so far.</summary>
    internal int PostCount => Volatile.Read(ref postCount);

    public override void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Interlocked.Increment(ref postCount);
        work.Add((callback, state));
    }

    /// <summary>Posts <paramref name="action"/> and returns its result.</summary>
    internal Task<T> Run<T>(Func<T> action)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try
            {
                completion.TrySetResult(action());
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }, null);
        return completion.Task.WaitAsync(TestBudgets.WaitTimeout);
    }

    internal Task Run(Action action) => Run(() =>
    {
        action();
        return true;
    });

    /// <summary>
    /// Posts <paramref name="start"/>, which starts asynchronous work on the
    /// loop, and waits for that work.
    /// </summary>
    internal async Task<T> RunAsync<T>(Func<Task<T>> start)
    {
        var started = await Run(start);
        return await started.WaitAsync(TestBudgets.WaitTimeout);
    }

    /// <summary>Creates a root whose continuation scheduler is this loop's.</summary>
    internal Task<LuauState> CreateRootAsync(LuauExecutionOptions? execution = null) =>
        Run(() => LuauState.Create(TestBudgets.StateOptions with
        {
            DefaultExecutionOptions = (execution ?? TestBudgets.Execution) with
            {
                ContinuationScheduler = Scheduler,
            },
        }));

    void Pump()
    {
        SetSynchronizationContext(this);
        var scheduler = LuauSynchronizationContextScheduler.CaptureCurrent();
        if (!currentContext)
        {
            SetSynchronizationContext(null);
        }

        ready.TrySetResult(scheduler);
        try
        {
            foreach (var item in work.GetConsumingEnumerable())
            {
                item.Callback(item.State);
            }
        }
        catch (Exception exception)
        {
            UnhandledException = exception;
        }
    }

    public void Dispose()
    {
        work.CompleteAdding();
        thread.Join(TestBudgets.WaitTimeout);
        work.Dispose();
    }
}
