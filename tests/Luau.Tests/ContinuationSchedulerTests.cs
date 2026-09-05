using System.Collections.Concurrent;

namespace Luau.Tests;

public sealed class ContinuationSchedulerTests
{
    [Fact]
    public void SynchronizationContextSchedulerRejectsNullContext()
    {
        Assert.Throws<ArgumentNullException>(() => new LuauSynchronizationContextScheduler(null!));
    }

    [Fact]
    public void CaptureCurrentRequiresSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            Assert.Throws<InvalidOperationException>(LuauSynchronizationContextScheduler.CaptureCurrent);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task DispatcherRunsPostedWorkOnSynchronizationContextOwner()
    {
        using var context = new SingleThreadSynchronizationContext();
        var scheduler = await context.CreateSchedulerAsync();
        var callerThreadId = Environment.CurrentManagedThreadId;

        var invokedThreadId = await LuauContinuationDispatcher.InvokeAsync(
            scheduler,
            static () => Environment.CurrentManagedThreadId);

        Assert.NotEqual(callerThreadId, invokedThreadId);
        Assert.Equal(context.OwnerManagedThreadId, invokedThreadId);
        Assert.Equal(context.OwnerManagedThreadId, scheduler.OwnerManagedThreadId);
        Assert.False(scheduler.CheckAccess());
    }

    [Fact]
    public async Task DispatcherContainsExceptionsRaisedOnScheduledThread()
    {
        using var context = new SingleThreadSynchronizationContext();
        var scheduler = await context.CreateSchedulerAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await LuauContinuationDispatcher.InvokeAsync(
                scheduler,
                static () => throw new InvalidOperationException("scheduled failure")));

        Assert.Equal("scheduled failure", exception.Message);
        Assert.Null(context.UnhandledException);
    }

    [Fact]
    public async Task DispatcherExecutesInlineWhenSchedulerAlreadyHasAccess()
    {
        var scheduler = new InlineScheduler();
        var invoked = false;

        await LuauContinuationDispatcher.InvokeAsync(scheduler, () => invoked = true);

        Assert.True(invoked);
        Assert.Equal(0, scheduler.PostCount);
    }

    [Fact]
    public async Task AsyncVmExecutionAndCallbacksStayOnConfiguredScheduler()
    {
        using var context = new SingleThreadSynchronizationContext();
        var scheduler = await context.CreateSchedulerAsync();
        var callbackStartThreadId = 0;
        var callbackContinuationThreadId = 0;
        var postCallbackThreadId = 0;
        LuauState? state = null;

        try
        {
            state = await LuauContinuationDispatcher.InvokeAsync(
                scheduler,
                () =>
                {
                    var created = LuauState.Create(new LuauStateOptions
                    {
                        DefaultExecutionOptions = new LuauExecutionOptions
                        {
                            ContinuationScheduler = scheduler,
                            // Thread affinity is independent of runner load.
                            WallClockLimit = TimeSpan.FromSeconds(10),
                        },
                    });
                    try
                    {
                        created["hostAsyncValue"] = created.CreateAsyncFunction(
                            "hostAsyncValue",
                            async context =>
                            {
                                callbackStartThreadId = Environment.CurrentManagedThreadId;
                                await Task.Delay(1, context.CancellationToken);
                                callbackContinuationThreadId = Environment.CurrentManagedThreadId;
                                context.Return(41d);
                            });
                        created["hostAfterAsync"] = created.CreateFunction(
                            "hostAfterAsync",
                            context =>
                            {
                                postCallbackThreadId = Environment.CurrentManagedThreadId;
                                context.Return(true);
                            });
                        return created;
                    }
                    catch
                    {
                        created.Dispose();
                        throw;
                    }
                });

            var execution = await LuauContinuationDispatcher.InvokeAsync(
                scheduler,
                () => state.DoStringAsync(
                    "local value = hostAsyncValue(); return value + 1, hostAfterAsync()",
                    "@scheduler/affinity.luau"));
            var results = await execution.AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(2, results.Length);
            Assert.Equal(42, results[0].Read<int>());
            Assert.True(results[1].Read<bool>());
            Assert.Equal(context.OwnerManagedThreadId, callbackStartThreadId);
            Assert.Equal(context.OwnerManagedThreadId, callbackContinuationThreadId);
            Assert.Equal(context.OwnerManagedThreadId, postCallbackThreadId);
            Assert.Null(context.UnhandledException);
        }
        finally
        {
            if (state != null)
            {
                await LuauContinuationDispatcher.InvokeAsync(scheduler, state.Dispose);
            }
        }
    }

    [Fact]
    public async Task CallbackStateAccessOffConfiguredSchedulerFailsWithoutCorruptingVm()
    {
        using var context = new SingleThreadSynchronizationContext();
        var scheduler = await context.CreateSchedulerAsync();
        LuauState? state = null;

        try
        {
            state = await LuauContinuationDispatcher.InvokeAsync(
                scheduler,
                () =>
                {
                    var created = LuauState.Create(new LuauStateOptions
                    {
                        DefaultExecutionOptions = new LuauExecutionOptions
                        {
                            ContinuationScheduler = scheduler,
                        },
                    });
                    created["leaveOwner"] = created.CreateAsyncFunction(
                        "leaveOwner",
                        async context =>
                        {
                            await Task.Delay(1, context.CancellationToken).ConfigureAwait(false);
                            context.Return(1);
                        });
                    return created;
                });

            var execution = await LuauContinuationDispatcher.InvokeAsync(
                scheduler,
                () => state.DoStringAsync("return leaveOwner()", "@scheduler/off-owner.luau"));
            var exception = await Assert.ThrowsAsync<LuauManagedCallbackException>(
                () => execution.AsTask().WaitAsync(TimeSpan.FromSeconds(10)));

            Assert.IsType<InvalidOperationException>(exception.InnerException);

            var recovery = await LuauContinuationDispatcher.InvokeAsync(
                scheduler,
                () => state.DoString("return 9", "@scheduler/recovery.luau"));
            Assert.Equal(9, Assert.Single(recovery).Read<int>());
        }
        finally
        {
            if (state != null)
            {
                await LuauContinuationDispatcher.InvokeAsync(scheduler, state.Dispose);
            }
        }
    }

    [Fact]
    public async Task RootYieldAfterAsyncCallbackResetsOnConfiguredScheduler()
    {
        using var context = new SingleThreadSynchronizationContext();
        var scheduler = await context.CreateSchedulerAsync();
        LuauState? state = null;
        try
        {
            state = await LuauContinuationDispatcher.InvokeAsync(scheduler, () =>
            {
                var created = LuauState.Create(new LuauStateOptions
                {
                    DefaultExecutionOptions = new LuauExecutionOptions
                    {
                        ContinuationScheduler = scheduler,
                        WallClockLimit = TimeSpan.FromSeconds(10),
                    },
                });
                created.OpenCoroutineLibrary();
                created["pause"] = created.CreateAsyncFunction(async _ => await Task.Yield());
                return created;
            });

            var execution = await LuauContinuationDispatcher.InvokeAsync(scheduler,
                () => state.DoStringAsync("pause(); coroutine.yield(7); return 9", "@scheduler/yield.luau"));
            var exception = await Assert.ThrowsAsync<LuauException>(
                () => execution.AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Contains("cannot yield", exception.Message);
            Assert.Equal("@scheduler/yield.luau", exception.ChunkName);

            await LuauContinuationDispatcher.InvokeAsync(scheduler, () =>
            {
                Assert.Equal(0, state.GetTop());
                using var result = state.DoString("return 11");
                Assert.Equal(11, result.Read<int>(0));
            });
        }
        finally
        {
            if (state != null)
                await LuauContinuationDispatcher.InvokeAsync(scheduler, state.Dispose);
        }
    }

    sealed class InlineScheduler : ILuauContinuationScheduler
    {
        public int PostCount { get; private set; }

        public bool CheckAccess() => true;

        public void Post(Action continuation)
        {
            PostCount++;
            continuation();
        }
    }

    sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
    {
        readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> work = new();
        readonly TaskCompletionSource<int> started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        readonly Thread thread;

        public SingleThreadSynchronizationContext()
        {
            thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Luau continuation scheduler test",
            };
            thread.Start();
        }

        public int OwnerManagedThreadId => started.Task.GetAwaiter().GetResult();

        public Exception? UnhandledException { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (callback == null)
            {
                throw new ArgumentNullException(nameof(callback));
            }

            work.Add((callback, state));
        }

        public async Task<LuauSynchronizationContextScheduler> CreateSchedulerAsync()
        {
            await started.Task.ConfigureAwait(false);
            var completion = new TaskCompletionSource<LuauSynchronizationContextScheduler>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Post(_ =>
            {
                try
                {
                    completion.TrySetResult(LuauSynchronizationContextScheduler.CaptureCurrent());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }, null);
            return await completion.Task.ConfigureAwait(false);
        }

        void Run()
        {
            SetSynchronizationContext(this);
            started.TrySetResult(Environment.CurrentManagedThreadId);
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
            finally
            {
                SetSynchronizationContext(null);
            }
        }

        public void Dispose()
        {
            work.CompleteAdding();
            thread.Join(TimeSpan.FromSeconds(5));
            work.Dispose();
        }
    }
}
