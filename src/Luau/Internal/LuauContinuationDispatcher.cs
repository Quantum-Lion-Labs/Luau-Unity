namespace Luau;

/// <summary>
/// Converts the host-facing fire-and-forget scheduler contract into awaitable
/// operations while keeping exceptions inside the originating execution.
/// </summary>
internal static class LuauContinuationDispatcher
{
    internal static ValueTask InvokeAsync(
        ILuauContinuationScheduler? scheduler,
        Action action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (scheduler == null || scheduler.CheckAccess())
        {
            action();
            return default;
        }

        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executionContext = ExecutionContext.Capture();
        try
        {
            scheduler.Post(() =>
            {
                void Invoke()
                {
                    try
                    {
                        action();
                        completion.TrySetResult(null);
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                }

                if (executionContext == null)
                {
                    Invoke();
                }
                else
                {
                    ExecutionContext.Run(executionContext, static state => ((Action)state!).Invoke(), (Action)Invoke);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return new ValueTask(completion.Task);
    }

    /// <summary>
    /// Runs <paramref name="action"/> as <see cref="InvokeAsync(ILuauContinuationScheduler?, Action)"/>
    /// does, and never while the current thread is inside a native access of
    /// <paramref name="context"/>. A continuation that code inside an access
    /// completes inline (a host function that a metamethod calls completes a
    /// source that runs its continuations synchronously) runs nested in that
    /// access, on its thread, where the scheduler's check passes and the
    /// reentrant gate does not wait; the action then goes to the scheduler,
    /// or with none to the thread pool, and runs once that thread has left
    /// the access. On the usual path, already on the scheduler and outside
    /// any access, it runs inline as before.
    /// </summary>
    internal static ValueTask InvokeOutsideNativeAccessAsync<TState>(
        ILuauContinuationScheduler? scheduler,
        LuauVmContext context,
        Action<TState> action,
        TState state)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (!context.IsInsideNativeAccess && (scheduler == null || scheduler.CheckAccess()))
        {
            action(state);
            return default;
        }

        return new OutsideNativeAccessWork<TState>(scheduler, context, action, state).Start();
    }

    sealed class OutsideNativeAccessWork<TState>
    {
        readonly ILuauContinuationScheduler? scheduler;
        readonly LuauVmContext context;
        readonly Action<TState> action;
        readonly TState state;
        readonly ExecutionContext? executionContext = ExecutionContext.Capture();
        readonly TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Action run;

        internal OutsideNativeAccessWork(
            ILuauContinuationScheduler? scheduler,
            LuauVmContext context,
            Action<TState> action,
            TState state)
        {
            this.scheduler = scheduler;
            this.context = context;
            this.action = action;
            this.state = state;
            run = Run;
        }

        internal ValueTask Start()
        {
            Schedule();
            return new ValueTask(completion.Task);
        }

        void Schedule()
        {
            try
            {
                if (scheduler != null)
                {
                    scheduler.Post(run);
                }
                else
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static work => ((OutsideNativeAccessWork<TState>)work!).Run(), this);
                }
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        void Run()
        {
            // A scheduler that runs queued work from inside an access (a
            // nested message loop in a host function) can deliver this there.
            // Posting it again from here would feed a loop that runs until
            // its queue is empty for ever, and the access cannot end before
            // that loop returns. Another thread waits for the access to end
            // and posts it then, once.
            if (context.IsInsideNativeAccess)
            {
                try
                {
                    ThreadPool.UnsafeQueueUserWorkItem(
                        static work => ((OutsideNativeAccessWork<TState>)work!).ScheduleAfterNativeAccess(),
                        this);
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }

                return;
            }

            if (executionContext == null)
            {
                Invoke();
            }
            else
            {
                ExecutionContext.Run(
                    executionContext,
                    static work => ((OutsideNativeAccessWork<TState>)work!).Invoke(),
                    this);
            }
        }

        void ScheduleAfterNativeAccess()
        {
            context.WaitForNativeAccessToEnd();
            Schedule();
        }

        void Invoke()
        {
            try
            {
                action(state);
                completion.TrySetResult(null);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }
    }

    internal static ValueTask<T> InvokeAsync<T>(
        ILuauContinuationScheduler? scheduler,
        Func<T> action)
    {
        if (action == null)
        {
            throw new ArgumentNullException(nameof(action));
        }

        if (scheduler == null || scheduler.CheckAccess())
        {
            return new ValueTask<T>(action());
        }

        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var executionContext = ExecutionContext.Capture();
        try
        {
            scheduler.Post(() =>
            {
                void Invoke()
                {
                    try
                    {
                        completion.TrySetResult(action());
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetException(exception);
                    }
                }

                if (executionContext == null)
                {
                    Invoke();
                }
                else
                {
                    ExecutionContext.Run(executionContext, static state => ((Action)state!).Invoke(), (Action)Invoke);
                }
            });
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }

        return new ValueTask<T>(completion.Task);
    }
}
