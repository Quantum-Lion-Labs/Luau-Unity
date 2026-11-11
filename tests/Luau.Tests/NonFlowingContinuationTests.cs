using System.Runtime.CompilerServices;

namespace Luau.Tests;

/// <summary>
/// An asynchronous host callback that awaits a helper written as an async
/// method whose builder does not carry the <see cref="ExecutionContext"/>
/// (UniTask's; <see cref="NoFlowTask"/> here) continues in that helper without
/// the operation's ambient marker. With a continuation scheduler, the
/// operation accepts such a caller on the scheduler while it waits for the
/// callback; without one, nothing identifies the caller and it is refused
/// (maintainer guide section 6.8).
/// </summary>
public sealed class NonFlowingContinuationTests : IDisposable
{
    const string AnotherOperation = "The Luau VM is executing on another operation.";
    const string AlreadyExecuting = "The Luau VM is already executing.";
    const string OffScheduler = "The Luau VM can only be accessed from its configured continuation scheduler.";
    const string BeforeYield = "A managed async callback cannot access its Luau state until the VM has yielded.";

    // Each callback sets this before it awaits. A helper that sees it after
    // its own await kept the callback's execution context.
    static readonly AsyncLocal<string?> FlowProbe = new();

    readonly SingleThreadLoop loop = new();

    public void Dispose()
    {
        loop.Dispose();
        Assert.Null(loop.UnhandledException);
    }

    public enum Access
    {
        // Callback context members that do not enter the VM.
        ArgumentCount,
        CallbackName,
        State,
        CancellationToken,

        // Wrapper disposal is deferred while an operation is active; it never
        // enters the VM.
        WrapperDispose,

        // Everything below enters the VM.
        Read,
        Return,
        ToDisplayString,
        TableRead,
        TableWrite,
        GlobalRead,
        GlobalWrite,
        FunctionRetain,
        CreateTable,
        CreateFunction,
        CreateAsyncFunction,
        CreateHandle,
        CreateThread,
        OpenLibrary,
    }

    public enum NestedOperation
    {
        Invoke,
        InvokeVoid,
        InvokeAsync,
        DoString,
        DoStringAsync,
        ResumeCoroutine,
    }

    public static TheoryData<Access> Accesses() => new(Enum.GetValues<Access>());

    public static TheoryData<NestedOperation> NestedOperations() => new(Enum.GetValues<NestedOperation>());

    static bool EntersTheVm(Access access) => access >= Access.Read;

    // --- The scenario -------------------------------------------------------

    static async NoFlowTask LoadInto(LuauTable table, StrongBox<bool> lostContext)
    {
        // Resumes from the synchronization context's queue, or the thread
        // pool, in that thread's context rather than the callback's.
        await Task.Yield();
        lostContext.Value = FlowProbe.Value == null;
        table["loaded"] = true;
    }

    [Fact]
    public async Task CallbackAwaitingANonFlowingHelperCompletesOnTheScheduler()
    {
        var root = await loop.CreateRootAsync();
        try
        {
            var lostContext = new StrongBox<bool>();
            var results = await loop.RunAsync(() =>
            {
                var table = root.CreateTable();
                root["table"] = table;
                root["load"] = root.CreateAsyncFunction("load", async context =>
                {
                    FlowProbe.Value = "callback";
                    await LoadInto(table, lostContext);
                    context.Return(1);
                });
                return ReadAll(root.DoStringAsync("return load(), table.loaded"));
            });

            Assert.True(lostContext.Value, "The helper kept the callback's execution context.");
            Assert.Equal(new object?[] { 1, true }, results);
            Assert.Equal(2, await loop.Run(() => ReadInt(root, "return 2")));
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Fact]
    public async Task CallbackAwaitingANonFlowingHelperFailsWithoutAScheduler()
    {
        using var root = TestBudgets.CreateRoot();
        var lostContext = new StrongBox<bool>();
        using var table = root.CreateTable();
        using var load = root.CreateAsyncFunction("load", async context =>
        {
            FlowProbe.Value = "callback";
            await LoadInto(table, lostContext);
            context.Return(1);
        });
        root["load"] = load;

        // Started on the thread pool, where nothing captures a context.
        var exception = await Assert.ThrowsAsync<LuauManagedCallbackException>(() => Task.Run(
            () => root.DoStringAsync("return load()", "@noflow/unscheduled.luau").AsTask()).WaitAsync(TestBudgets.WaitTimeout));

        Assert.True(lostContext.Value, "The helper kept the callback's execution context.");
        Assert.Equal("load", exception.CallbackName);
        var refusal = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Equal(AnotherOperation, refusal.Message);
        Assert.Equal(2, ReadInt(root, "return 2"));
    }

    // --- Every kind of access, in each configuration -------------------------

    [Theory]
    [MemberData(nameof(Accesses))]
    public async Task OnTheSchedulerEveryAccessAfterLosingTheContextBehavesAsWithIt(Access access)
    {
        var flowing = await RunAccessAsync(loop, access, nonFlowing: false);
        var nonFlowing = await RunAccessAsync(loop, access, nonFlowing: true);

        Assert.True(flowing.KeptContext);
        Assert.False(nonFlowing.KeptContext);
        Assert.Equal(loop.ManagedThreadId, nonFlowing.ThreadId);
        Assert.Null(nonFlowing.Failure);
        Assert.Null(flowing.Failure);
        Assert.Equal(Expected(access), nonFlowing.Observed);
        Assert.Equal(flowing.Observed, nonFlowing.Observed);
        Assert.Equal(ExpectedResults(access), nonFlowing.Results);
        Assert.Equal(flowing.Results, nonFlowing.Results);
        Assert.Null(nonFlowing.AfterwardsFailure);
    }

    [Theory]
    [MemberData(nameof(Accesses))]
    public async Task WithoutASchedulerEveryVmAccessAfterLosingTheContextIsRefused(Access access)
    {
        var flowing = await RunAccessAsync(loop: null, access, nonFlowing: false);
        var nonFlowing = await RunAccessAsync(loop: null, access, nonFlowing: true);

        Assert.True(flowing.KeptContext);
        Assert.False(nonFlowing.KeptContext);
        Assert.Null(flowing.Failure);
        Assert.Equal(Expected(access), flowing.Observed);
        Assert.Equal(ExpectedResults(access), flowing.Results);

        if (!EntersTheVm(access))
        {
            Assert.Null(nonFlowing.Failure);
            Assert.Equal(Expected(access), nonFlowing.Observed);
        }
        else if (access == Access.Read)
        {
            // Read reports any failure as one to convert the argument.
            var failure = Assert.IsType<InvalidOperationException>(nonFlowing.Failure);
            Assert.Equal("Argument 0 of managed callback 'f' cannot be read as Int32.", failure.Message);
            Assert.Equal(AnotherOperation, Assert.IsType<InvalidOperationException>(failure.InnerException).Message);
        }
        else
        {
            Assert.Equal(AnotherOperation, Assert.IsType<InvalidOperationException>(nonFlowing.Failure).Message);
        }

        // The refusal leaves the VM untouched: the operation completes and
        // the root runs the next one.
        Assert.Equal(EntersTheVm(access) ? DefaultResults : ExpectedResults(access), nonFlowing.Results);
        Assert.Null(nonFlowing.AfterwardsFailure);
    }

    [Theory]
    [MemberData(nameof(NestedOperations))]
    public async Task ANewTopLevelOperationFromTheCallbackIsRefusedInEveryConfiguration(NestedOperation operation)
    {
        foreach (var scheduled in new[] { true, false })
        {
            var flowing = await RunNestedAsync(scheduled ? loop : null, operation, nonFlowing: false);
            var nonFlowing = await RunNestedAsync(scheduled ? loop : null, operation, nonFlowing: true);

            Assert.Equal(AlreadyExecuting, Assert.IsType<InvalidOperationException>(flowing.Failure).Message);
            // With a scheduler the continuation is the operation's own caller
            // and meets the active-operation rule; without one it is refused
            // before that.
            Assert.Equal(
                scheduled ? AlreadyExecuting : AnotherOperation,
                Assert.IsType<InvalidOperationException>(nonFlowing.Failure).Message);
            Assert.Equal(DefaultResults, nonFlowing.Results);
            Assert.Null(nonFlowing.AfterwardsFailure);
        }
    }

    // --- Protections that do not change --------------------------------------

    [Fact]
    public async Task AnotherThreadIsRefusedWhileACallbackIsPendingOnTheScheduler()
    {
        var root = await loop.CreateRootAsync();
        try
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<Exception?>? forkedWithMarker = null;
            Task<Exception?>? unrelated = null;
            var run = await loop.Run(() =>
            {
                var table = root.CreateTable();
                root["wait"] = root.CreateAsyncFunction("wait", async context =>
                {
                    // Both run while the callback is pending: a flow forked
                    // from it carries the operation's marker, the other none.
                    forkedWithMarker = Task.Run(() => Record(() => table["k"] = 1));
                    using (ExecutionContext.SuppressFlow())
                    {
                        unrelated = Task.Run(() => Record(() => table["k"] = 1));
                    }

                    await Task.WhenAll(forkedWithMarker, unrelated);
                    await WaitWithoutContext(release.Task);
                    context.Return(3);
                });
                return ReadAll(root.DoStringAsync("return wait()"));
            });

            Assert.Equal(OffScheduler, Assert.IsType<InvalidOperationException>(await forkedWithMarker!).Message);
            Assert.Equal(OffScheduler, Assert.IsType<InvalidOperationException>(await unrelated!).Message);
            release.TrySetResult();
            Assert.Equal(new object?[] { 3 }, await run);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AccessOnTheSchedulerBeforeTheVmHasYieldedIsRefused(bool withMarker)
    {
        // The window runs from native resume returning with a callback
        // queued to the runner suspending the operation for it. A runner off
        // the scheduler posts each of its steps, so work that the script
        // posts while the VM runs is queued before the runner's next step and
        // runs in that window. The runner is moved off the loop for certain:
        // first's task is completed from the test thread, with asynchronous
        // continuations, only once the loop item that started the operation
        // has returned, so the runner already awaits it.
        var root = await loop.CreateRootAsync();
        try
        {
            Exception? refusal = null;
            var attempted = false;
            var inWindow = false;
            var secondStarted = false;
            var secondStartedBeforeAttempt = true;
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var run = await loop.Run(() =>
            {
                var table = root.CreateTable();
                root["first"] = root.CreateAsyncFunction("first", _ => new ValueTask(first.Task));
                root["post"] = root.CreateFunction("post", _ =>
                {
                    var context = ExecutionContext.Capture()!;
                    loop.Post(_ =>
                    {
                        attempted = true;
                        secondStartedBeforeAttempt = secondStarted;
                        // The proof that this attempt landed in the window: a
                        // callback is queued and not yet dispatched.
                        var active = root.Context.GetActiveOperation();
                        inWindow = active != null &&
                            Record(active.ThrowIfAsyncCallbackAccessUnsafe) is InvalidOperationException;
                        if (withMarker)
                        {
                            ExecutionContext.Run(context, _ => refusal = Record(() => table["k"] = 1), null);
                        }
                        else
                        {
                            refusal = Record(() => table["k"] = 1);
                        }
                    }, null);
                });
                root["second"] = root.CreateAsyncFunction("second", context =>
                {
                    secondStarted = true;
                    context.Return(4);
                    return default;
                });
                return ReadAll(root.DoStringAsync("first() post() return second()"));
            });

            first.TrySetResult();
            var results = await run.WaitAsync(TestBudgets.WaitTimeout);

            Assert.True(attempted);
            Assert.True(inWindow, "The access did not run between native resume and the callback's dispatch.");
            Assert.False(secondStartedBeforeAttempt);
            Assert.Equal(
                withMarker ? BeforeYield : AnotherOperation,
                Assert.IsType<InvalidOperationException>(refusal).Message);
            Assert.Equal(new object?[] { 4 }, results);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Fact]
    public async Task AccessWithoutTheMarkerWhileTheVmRunsLuauCodeIsRefusedOnTheScheduler()
    {
        // A synchronous callback runs inside native resume, while the VM runs
        // Luau code; code in it that has lost the context is not accepted.
        var clean = await loop.Run(() => ExecutionContext.Capture()!);
        var root = await loop.CreateRootAsync();
        try
        {
            Exception? withoutMarker = null;
            Exception? fromOtherThread = null;
            var results = await loop.RunAsync(() =>
            {
                var table = root.CreateTable();
                root["park"] = root.CreateAsyncFunction("park", async _ => await Task.Yield());
                root["probe"] = root.CreateFunction("probe", context =>
                {
                    ExecutionContext.Run(clean, _ => withoutMarker = Record(() => table["k"] = 1), null);
                    var other = Task.Run(() => Record(() => table["k"] = 1));
                    Assert.True(other.Wait(TestBudgets.WaitTimeout));
                    fromOtherThread = other.Result;
                    context.Return(5);
                });
                return ReadAll(root.DoStringAsync("park() return probe()"));
            });

            Assert.Equal(AnotherOperation, Assert.IsType<InvalidOperationException>(withoutMarker).Message);
            Assert.Equal(OffScheduler, Assert.IsType<InvalidOperationException>(fromOtherThread).Message);
            Assert.Equal(new object?[] { 5 }, results);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Fact]
    public async Task AnUnrelatedThreadIsRefusedWhileASynchronousOperationRunsLuauCode()
    {
        // Without a scheduler the thread slot identifies the synchronous
        // operation; with one, only the scheduler's thread may enter.
        using var unscheduled = TestBudgets.CreateRoot();
        Assert.Equal(AnotherOperation, Assert.IsType<InvalidOperationException>(
            await AccessFromAnUnrelatedThreadDuring(unscheduled)).Message);

        var scheduled = await loop.CreateRootAsync();
        try
        {
            var refusal = await loop.RunAsync(() => Task.FromResult(AccessFromAnUnrelatedThreadDuring(scheduled).Result));
            Assert.Equal(OffScheduler, Assert.IsType<InvalidOperationException>(refusal).Message);
        }
        finally
        {
            await loop.Run(scheduled.Dispose);
        }
    }

    static async Task<Exception?> AccessFromAnUnrelatedThreadDuring(LuauState root)
    {
        using var table = root.CreateTable();
        using var request = new SemaphoreSlim(0);
        Exception? observed = null;
        Task worker;
        using (ExecutionContext.SuppressFlow())
        {
            worker = Task.Run(async () =>
            {
                await request.WaitAsync(TestBudgets.WaitTimeout);
                observed = Record(() => table["k"] = 1);
            });
        }

        using var block = root.CreateFunction("block", _ =>
        {
            request.Release();
            Assert.True(worker.Wait(TestBudgets.WaitTimeout));
        });
        root["block"] = block;
        root.DoString("block()").Dispose();
        await worker;
        return observed;
    }

    [Fact]
    public async Task NoNewTopLevelOperationStartsWhileACallbackIsPending()
    {
        var root = await loop.CreateRootAsync();
        try
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LuauFunction? function = null;
            LuauState? coroutine = null;
            var run = await loop.Run(() =>
            {
                root.OpenCoroutineLibrary();
                using (var loaded = root.DoString("return function() return 1 end"))
                {
                    function = loaded[0].Read<LuauFunction>().Retain();
                }

                coroutine = root.CreateThread();
                coroutine.DoString("coroutine.yield(1) return 2").Dispose();
                root["wait"] = root.CreateAsyncFunction("wait", async context =>
                {
                    pending.TrySetResult();
                    await WaitWithoutContext(release.Task);
                    context.Return(6);
                });
                return ReadAll(root.DoStringAsync("return wait()"));
            });
            await pending.Task.WaitAsync(TestBudgets.WaitTimeout);

            // Unrelated code on the scheduler, in the loop's own context.
            var attempts = await loop.Run(() => new[]
            {
                Record(() => root.DoString("return 1").Dispose()),
                RecordAsync(() => root.DoStringAsync("return 1")),
                Record(() => function!.Invoke().Dispose()),
                Record(() => function!.InvokeVoid()),
                RecordAsync(() => function!.InvokeAsync()),
                Record(() => coroutine!.Resume().Dispose()),
                RecordAsync(() => coroutine!.ResumeAsync()),
            });

            Assert.All(attempts, attempt =>
                Assert.Equal(AlreadyExecuting, Assert.IsType<InvalidOperationException>(attempt).Message));
            release.TrySetResult();
            Assert.Equal(new object?[] { 6 }, await run);
            Assert.Equal(2, await loop.Run(() =>
            {
                using var resumed = coroutine!.Resume();
                return resumed[0].Read<int>();
            }));
            function!.Dispose();
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Fact]
    public async Task AMarkerForAFinishedOperationIsRefusedOnTheScheduler()
    {
        var root = await loop.CreateRootAsync();
        try
        {
            ExecutionContext? captured = null;
            var table = await loop.Run(() =>
            {
                var created = root.CreateTable();
                root["capture"] = root.CreateAsyncFunction("capture", async _ =>
                {
                    await Task.Yield();
                    captured = ExecutionContext.Capture();
                });
                return created;
            });
            await loop.RunAsync(() => ReadAll(root.DoStringAsync("capture()")));

            Exception? replayed = null;
            var replayedWhileIdle = await loop.Run(() =>
            {
                Exception? idle = null;
                ExecutionContext.Run(captured!, _ => idle = Record(() => table["k"] = 1), null);
                return idle;
            });
            await loop.RunAsync(() =>
            {
                root["later"] = root.CreateAsyncFunction("later", async _ =>
                {
                    await WaitWithoutContext(Task.CompletedTask);
                    ExecutionContext.Run(captured!, _ => replayed = Record(() => table["k"] = 2), null);
                });
                return ReadAll(root.DoStringAsync("later()"));
            });

            // With no operation active, the scheduler is the only rule.
            Assert.Null(replayedWhileIdle);
            Assert.Equal(AnotherOperation, Assert.IsType<InvalidOperationException>(replayed).Message);
            Assert.Equal(1, await loop.Run(() => table["k"].Read<int>()));
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AContinuationResumedByAnotherRootsCallbackCarriesThatRootsMarker(bool otherIsAsync)
    {
        // A completion source that runs its awaiter inline (as UniTask's do)
        // hands the continuation the completer's context: another root's
        // marker when an asynchronous callback completes it, none when a
        // synchronous one does.
        var a = await loop.CreateRootAsync();
        var b = await loop.CreateRootAsync();
        try
        {
            var signal = new InlineSignal();
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exception? observed = new InvalidOperationException("not run");
            var aRun = await loop.Run(() =>
            {
                var table = a.CreateTable();
                a["table"] = table;
                a["wait"] = a.CreateAsyncFunction("wait", async _ => await Helper(table));
                return ReadAll(a.DoStringAsync("wait() return table.k"));
            });

            async NoFlowTask Helper(LuauTable table)
            {
                pending.TrySetResult();
                await signal;
                observed = Record(() => table["k"] = 7);
            }

            await pending.Task.WaitAsync(TestBudgets.WaitTimeout);
            if (otherIsAsync)
            {
                await loop.RunAsync(() =>
                {
                    b["release"] = b.CreateAsyncFunction("release", async _ =>
                    {
                        await Task.Yield();
                        signal.Set();
                    });
                    return ReadAll(b.DoStringAsync("release()"));
                });
            }
            else
            {
                await loop.Run(() =>
                {
                    b["release"] = b.CreateFunction("release", _ => signal.Set());
                    b.DoString("release()").Dispose();
                });
            }

            var results = await aRun;
            if (otherIsAsync)
            {
                Assert.Equal(AnotherOperation, Assert.IsType<InvalidOperationException>(observed).Message);
                Assert.Equal(new object?[] { null }, results);
            }
            else
            {
                Assert.Null(observed);
                Assert.Equal(new object?[] { 7 }, results);
            }
        }
        finally
        {
            await loop.Run(() =>
            {
                a.Dispose();
                b.Dispose();
            });
        }
    }

    // --- Unrelated code on the scheduler while a callback is pending ----------

    [Fact]
    public async Task UnrelatedCodeOnTheSchedulerUsesTheRootWhileACallbackIsPending()
    {
        var root = await loop.CreateRootAsync();
        try
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            object?[]? readAfterRelease = null;
            LuauTable? shared = null;
            LuauTable? meta = null;
            LuauFunction? function = null;
            var run = await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                using (var setup = root.DoString(
                    "shared = { count = 0 } " +
                    "meta = setmetatable({}, { __index = function(_, key) return key .. '!' end }) " +
                    "g = 1 " +
                    "return shared, meta, function() return 1 end"))
                {
                    shared = setup[0].Read<LuauTable>().Retain();
                    meta = setup[1].Read<LuauTable>().Retain();
                    function = setup[2].Read<LuauFunction>().Retain();
                }

                root["wait"] = root.CreateAsyncFunction("wait", async context =>
                {
                    pending.TrySetResult();
                    await WaitWithoutContext(release.Task);
                    // The arguments are where they were.
                    readAfterRelease = [context.ArgumentCount, context.Read<int>(0)];
                    context.Return(context.Read<int>(0) * 2);
                });
                return ReadAll(root.DoStringAsync("local v = wait(5) return v, shared.count, g, meta.x"));
            });
            await pending.Task.WaitAsync(TestBudgets.WaitTimeout);

            // Another component's update on the scheduler, in its own context.
            var observed = await loop.Run(() =>
            {
                var top = root.GetTop();
                shared!["count"] = 3;
                root["g"] = 9;
                using var created = root.CreateTable();
                created["x"] = 1;
                var values = new object?[]
                {
                    shared["count"].Read<int>(),
                    root["g"].Read<int>(),
                    // Runs the Luau metamethod on another thread of the root:
                    // the root itself is suspended.
                    meta!["y"].Read<string>(),
                    created["x"].Read<int>(),
                    Record(() => function!.InvokeVoid())?.Message,
                    root.GetTop() - top,
                };
                return values;
            });

            Assert.Equal(new object?[] { 3, 9, "y!", 1, AlreadyExecuting, 0 }, observed);
            release.TrySetResult();
            Assert.Equal(new object?[] { 10, 3, 9, "x!" }, await run);
            Assert.Equal(new object?[] { 1, 5 }, readAfterRelease);
            Assert.Equal(12, await loop.Run(() => ReadInt(root, "return shared.count + g")));
            await loop.Run(() =>
            {
                shared!.Dispose();
                meta!.Dispose();
                function!.Dispose();
            });
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Fact]
    public async Task FailuresOfUnrelatedCodeStayWithItAndTheOperationCompletes()
    {
        // Luau code that unrelated code reaches runs under the pending
        // operation, as the callback's own would. Its failures must reach the
        // code that caused them and leave the operation alone.
        var root = await loop.CreateRootAsync();
        try
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LuauTable? failing = null;
            LuauTable? hostFailing = null;
            LuauTable? asyncInMetamethod = null;
            var run = await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                root["hostFail"] = root.CreateFunction("hostFail", _ => throw new FormatException("host failure"));
                root["hostAsync"] = root.CreateAsyncFunction("hostAsync", _ => default);
                using (var setup = root.DoString(
                    "return setmetatable({}, { __index = function() error('script failure') end }), " +
                    "setmetatable({}, { __index = function() return hostFail() end }), " +
                    "setmetatable({}, { __index = function() return hostAsync() end })"))
                {
                    failing = setup[0].Read<LuauTable>().Retain();
                    hostFailing = setup[1].Read<LuauTable>().Retain();
                    asyncInMetamethod = setup[2].Read<LuauTable>().Retain();
                }

                root["wait"] = root.CreateAsyncFunction("wait", async context =>
                {
                    pending.TrySetResult();
                    await WaitWithoutContext(release.Task);
                    context.Return(8);
                });
                return ReadAll(root.DoStringAsync("return wait(), 'after'"));
            });
            await pending.Task.WaitAsync(TestBudgets.WaitTimeout);

            var failures = await loop.Run(() => new[]
            {
                Record(() => _ = failing!["x"]),
                Record(() => _ = hostFailing!["x"]),
                Record(() => _ = asyncInMetamethod!["x"]),
            });

            Assert.Contains("script failure", Assert.IsType<LuauException>(failures[0]).Message, StringComparison.Ordinal);
            var hostFailure = Assert.IsType<LuauManagedCallbackException>(failures[1]);
            Assert.Equal("hostFail", hostFailure.CallbackName);
            Assert.IsType<FormatException>(hostFailure.InnerException);
            var asyncFailure = Assert.IsType<LuauManagedCallbackException>(failures[2]);
            Assert.Equal("hostAsync", asyncFailure.CallbackName);
            Assert.Contains("yieldable", asyncFailure.InnerException!.Message, StringComparison.Ordinal);

            release.TrySetResult();
            Assert.Equal(new object?[] { 8, "after" }, await run);
            Assert.Equal(2, await loop.Run(() => ReadInt(root, "return 2")));
            await loop.Run(() =>
            {
                failing!.Dispose();
                hostFailing!.Dispose();
                asyncInMetamethod!.Dispose();
            });
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Fact]
    public async Task RepeatedUnrelatedWorkWhileACallbackIsPendingLeavesTheOperationIntact()
    {
        // Each item is a frame of another component's update: it writes and
        // reads tables and globals, runs a metamethod, creates and drops
        // wrappers, and collects garbage, between the callback's own steps.
        var root = await loop.CreateRootAsync();
        try
        {
            const int Frames = 200;
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LuauTable? shared = null;
            LuauTable? meta = null;
            var run = await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                using (var setup = root.DoString(
                    "shared = { frames = 0 } " +
                    "meta = setmetatable({}, { __index = function(_, key) return key * 2 end }) " +
                    "return shared, meta"))
                {
                    shared = setup[0].Read<LuauTable>().Retain();
                    meta = setup[1].Read<LuauTable>().Retain();
                }

                root["step"] = root.CreateAsyncFunction("step", async context =>
                {
                    var value = context.Read<int>(0);
                    if (value == 0)
                    {
                        pending.TrySetResult();
                        await WaitWithoutContext(release.Task);
                    }
                    else
                    {
                        await WaitWithoutContext(Task.CompletedTask);
                    }

                    context.Return(value + 1);
                });
                return ReadAll(root.DoStringAsync(
                    "local total = 0 " +
                    "for i = 0, 9 do total += step(i) end " +
                    "return total, shared.frames, last"));
            });
            await pending.Task.WaitAsync(TestBudgets.WaitTimeout);

            var frames = new List<Task<int>>();
            for (var frame = 1; frame <= Frames; frame++)
            {
                var current = frame;
                frames.Add(loop.Run(() =>
                {
                    var top = root.GetTop();
                    shared!["frames"] = current;
                    root["last"] = current;
                    using var scratch = root.CreateTable();
                    scratch["frame"] = current;
                    var doubled = meta![current].Read<int>();
                    root.CollectGarbage();
                    Assert.Equal(current, shared["frames"].Read<int>());
                    Assert.Equal(current, scratch["frame"].Read<int>());
                    Assert.Equal(0, root.GetTop() - top);
                    return doubled;
                }));
            }

            var doubledValues = await Task.WhenAll(frames);
            release.TrySetResult();

            Assert.Equal(Enumerable.Range(1, Frames).Select(frame => frame * 2), doubledValues);
            Assert.Equal(new object?[] { 55, Frames, Frames }, await run);
            await loop.Run(() =>
            {
                shared!.Dispose();
                meta!.Dispose();
            });
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Fact]
    public async Task UnrelatedCodeOnTheSchedulerUsesASuspendedCoroutineWhileItsCallbackIsPending()
    {
        // The script runs on a sandboxed child thread; writing that thread's
        // globals while it is suspended runs the sandbox's write guard on
        // another thread of the root.
        var root = await loop.CreateRootAsync();
        try
        {
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            LuauState? script = null;
            var run = await loop.Run(() =>
            {
                root.OpenBaseLibrary();
                root["wait"] = root.CreateAsyncFunction("wait", async context =>
                {
                    pending.TrySetResult();
                    await WaitWithoutContext(release.Task);
                    context.Return(1);
                });
                root.SandboxRoot();
                script = root.CreateSandboxedThread();
                return ReadAll(script.DoStringAsync("local v = wait() return v, score"));
            });
            await pending.Task.WaitAsync(TestBudgets.WaitTimeout);

            var written = await loop.Run(() =>
            {
                script!["score"] = 11;
                return script["score"].Read<int>();
            });

            Assert.Equal(11, written);
            release.TrySetResult();
            Assert.Equal(new object?[] { 1, 11 }, await run);
            await loop.Run(script!.Dispose);
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Fact]
    public async Task UnrelatedCodeOnTheSchedulerDisposesTheRootWhileACallbackIsPending()
    {
        var root = await loop.CreateRootAsync();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tokenCanceled = false;
        Exception? afterDisposal = null;
        var run = await loop.Run(() =>
        {
            var table = root.CreateTable();
            root["wait"] = root.CreateAsyncFunction("wait", async context =>
            {
                var token = context.CancellationToken;
                pending.TrySetResult();
                await WaitWithoutContext(release.Task);
                tokenCanceled = token.IsCancellationRequested;
                afterDisposal = Record(() => table["k"] = 1);
            });
            return ReadAll(root.DoStringAsync("wait()"));
        });
        await pending.Task.WaitAsync(TestBudgets.WaitTimeout);

        await loop.Run(root.Dispose);

        // The operation keeps the native state open until it ends.
        Assert.Equal(0, root.Context.CloseCount);
        release.TrySetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => run);
        Assert.True(tokenCanceled);
        Assert.IsType<ObjectDisposedException>(afterDisposal);
        Assert.Equal(1, root.Context.CloseCount);
        Assert.Throws<ObjectDisposedException>(() => root.CreateTable());
    }

    // --- Hard stops while suspended in a non-flowing method -------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CallerCancellationWhileSuspendedInANonFlowingMethod(bool nonFlowing)
    {
        var root = await loop.CreateRootAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stop = new StopObservation();
            var run = await loop.Run(() =>
            {
                var table = root.CreateTable();
                root["wait"] = root.CreateAsyncFunction("wait", async context =>
                {
                    FlowProbe.Value = "callback";
                    pending.TrySetResult();
                    await (nonFlowing
                        ? AwaitStopWithoutContext(context, table, stop).AsTask()
                        : AwaitStopWithContext(context, table, stop));
                });
                return ReadAll(root.DoStringAsync("wait()", cancellationToken: cancellation.Token));
            });
            await pending.Task.WaitAsync(TestBudgets.WaitTimeout);
            cancellation.Cancel();

            await Assert.ThrowsAsync<LuauExecutionCanceledException>(() => run);
            Assert.Equal(!nonFlowing, stop.KeptContext);
            Assert.True(stop.TokenCanceled);
            Assert.IsType<LuauExecutionCanceledException>(stop.Access);
            Assert.Equal(2, await loop.Run(() => ReadInt(root, "return 2")));
        }
        finally
        {
            await loop.Run(root.Dispose);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WallClockLimitWhileSuspendedInANonFlowingMethod(bool nonFlowing)
    {
        // The callback waits for the watchdog, not for time, but a runner that
        // stalls past the limit before the callback starts stops the script
        // without it, which proves nothing about the callback. Such an attempt
        // is repeated with a longer limit; the test fails if none starts it.
        const int Attempts = 5;
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var root = await loop.CreateRootAsync();
            try
            {
                var stop = new StopObservation();
                var started = false;
                var limit = TimeSpan.FromMilliseconds(500 << attempt);
                var run = await loop.Run(() =>
                {
                    var table = root.CreateTable();
                    root["wait"] = root.CreateAsyncFunction("wait", async context =>
                    {
                        started = true;
                        FlowProbe.Value = "callback";
                        await (nonFlowing
                            ? AwaitStopWithoutContext(context, table, stop).AsTask()
                            : AwaitStopWithContext(context, table, stop));
                    });
                    return ReadAll(root.DoStringAsync(
                        "wait()",
                        executionOptions: LuauExecutionOptions.Unbounded with { WallClockLimit = limit }));
                });

                var exception = await Assert.ThrowsAsync<LuauExecutionBudgetException>(() => run);
                Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
                Assert.Equal(2, await loop.Run(() => ReadInt(root, "return 2")));
                if (!started)
                {
                    continue;
                }

                Assert.Equal(!nonFlowing, stop.KeptContext);
                Assert.True(stop.TokenCanceled);
                Assert.Equal(
                    LuauExecutionBudgetKind.WallClock,
                    Assert.IsType<LuauExecutionBudgetException>(stop.Access).BudgetKind);
                return;
            }
            finally
            {
                await loop.Run(root.Dispose);
            }
        }

        Assert.Fail($"The callback never started before the wall-clock limit in {Attempts} attempts.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposalWhileSuspendedInANonFlowingMethod(bool nonFlowing)
    {
        var root = await loop.CreateRootAsync();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = new StopObservation();
        var run = await loop.Run(() =>
        {
            var table = root.CreateTable();
            root["wait"] = root.CreateAsyncFunction("wait", async context =>
            {
                FlowProbe.Value = "callback";
                pending.TrySetResult();
                await (nonFlowing
                    ? AwaitStopWithoutContext(context, table, stop).AsTask()
                    : AwaitStopWithContext(context, table, stop));
            });
            return ReadAll(root.DoStringAsync("wait()"));
        });
        await pending.Task.WaitAsync(TestBudgets.WaitTimeout);

        // Disposal may come from any thread.
        await Task.Run(root.Dispose);

        await Assert.ThrowsAsync<ObjectDisposedException>(() => run);
        Assert.Equal(!nonFlowing, stop.KeptContext);
        Assert.True(stop.TokenCanceled);
        Assert.IsType<ObjectDisposedException>(stop.Access);
        Assert.Equal(1, root.Context.CloseCount);
    }

    sealed class StopObservation
    {
        internal bool KeptContext;
        internal bool TokenCanceled;
        internal Exception? Access;
    }

    // Waits on the loop for the operation's token, then touches the VM.
    static async NoFlowTask AwaitStopWithoutContext(LuauCallContext context, LuauTable table, StopObservation stop)
    {
        await CanceledAsync(context.CancellationToken);
        Observe(context, table, stop);
    }

    static async Task AwaitStopWithContext(LuauCallContext context, LuauTable table, StopObservation stop)
    {
        await CanceledAsync(context.CancellationToken);
        Observe(context, table, stop);
    }

    static void Observe(LuauCallContext context, LuauTable table, StopObservation stop)
    {
        stop.KeptContext = FlowProbe.Value != null;
        stop.TokenCanceled = context.CancellationToken.IsCancellationRequested;
        stop.Access = Record(() => table["k"] = 1);
    }

    static Task CanceledAsync(CancellationToken token)
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => canceled.TrySetResult());
        return canceled.Task;
    }

    // --- Harness ---------------------------------------------------------------

    sealed class ProbeTarget
    {
    }

    static readonly LuauObjectDescriptor<ProbeTarget> ProbeDescriptor = new(
        "ProbeTarget",
        validateTarget: null,
        new[] { LuauObjectMember<ProbeTarget>.Method("probe", (_, _) => { }) });

    sealed class Fixture
    {
        internal required LuauState Root { get; init; }
        internal required LuauTable Shared { get; init; }
        internal required LuauFunction Script { get; init; }
        internal required LuauTable Disposable { get; init; }
        internal required LuauState Coroutine { get; init; }

        internal static Fixture Create(LuauState root)
        {
            root.OpenBaseLibrary();
            root.OpenCoroutineLibrary();
            using var setup = root.DoString("shared = { k = 5 } g = 7 return shared, function() return 3 end");
            var coroutine = root.CreateThread();
            coroutine.DoString("coroutine.yield(1) return 2").Dispose();
            return new Fixture
            {
                Root = root,
                Shared = setup[0].Read<LuauTable>().Retain(),
                Script = setup[1].Read<LuauFunction>().Retain(),
                Disposable = root.CreateTable(),
                Coroutine = coroutine,
            };
        }
    }

    sealed class Outcome
    {
        internal bool KeptContext;
        internal int ThreadId;
        internal object? Observed;
        internal Exception? Failure;
        internal object?[]? Results;
        internal Exception? AfterwardsFailure;
    }

    static object? Expected(Access access) => access switch
    {
        Access.ArgumentCount => 1,
        Access.CallbackName => "f",
        Access.State => true,
        Access.CancellationToken => true,
        Access.WrapperDispose => true,
        Access.Read => 41,
        Access.ToDisplayString => "41",
        Access.TableRead => 5,
        Access.GlobalRead => 7,
        _ => "done",
    };

    // What `local r = f(41) return r, g, shared.k, type(math)` returns when
    // the callback changed nothing and returned 1.
    static readonly object?[] DefaultResults = [1, 7, 5, "nil"];

    static object?[] ExpectedResults(Access access) =>
    [
        access == Access.Return ? 42 : 1,
        access == Access.GlobalWrite ? 8 : 7,
        access == Access.TableWrite ? 6 : 5,
        access == Access.OpenLibrary ? "table" : "nil",
    ];

    static object? Perform(Access access, LuauCallContext context, Fixture fixture)
    {
        switch (access)
        {
            case Access.ArgumentCount:
                return context.ArgumentCount;
            case Access.CallbackName:
                return context.CallbackName;
            case Access.State:
                return ReferenceEquals(context.State, fixture.Root);
            case Access.CancellationToken:
                return context.CancellationToken.CanBeCanceled;
            case Access.WrapperDispose:
                fixture.Disposable.Dispose();
                return fixture.Disposable.IsDisposed;
            case Access.Read:
                return context.Read<int>(0);
            case Access.Return:
                context.Return(42);
                break;
            case Access.ToDisplayString:
                return context.ToDisplayString(0, 64, out _);
            case Access.TableRead:
                return fixture.Shared["k"].Read<int>();
            case Access.TableWrite:
                fixture.Shared["k"] = 6;
                break;
            case Access.GlobalRead:
                return fixture.Root["g"].Read<int>();
            case Access.GlobalWrite:
                fixture.Root["g"] = 8;
                break;
            case Access.FunctionRetain:
                fixture.Script.Retain().Dispose();
                break;
            case Access.CreateTable:
                using (var table = fixture.Root.CreateTable())
                {
                    table["x"] = 1;
                }

                break;
            case Access.CreateFunction:
                fixture.Root.CreateFunction(_ => { }).Dispose();
                break;
            case Access.CreateAsyncFunction:
                fixture.Root.CreateAsyncFunction(_ => default).Dispose();
                break;
            case Access.CreateHandle:
                fixture.Root.CreateHandle(new ProbeTarget(), ProbeDescriptor).Dispose();
                break;
            case Access.CreateThread:
                fixture.Root.CreateThread().Dispose();
                break;
            case Access.OpenLibrary:
                fixture.Root.OpenMathLibrary();
                break;
        }

        return "done";
    }

    static async ValueTask Perform(NestedOperation operation, Fixture fixture)
    {
        switch (operation)
        {
            case NestedOperation.Invoke:
                fixture.Script.Invoke().Dispose();
                break;
            case NestedOperation.InvokeVoid:
                fixture.Script.InvokeVoid();
                break;
            case NestedOperation.InvokeAsync:
                (await fixture.Script.InvokeAsync()).Dispose();
                break;
            case NestedOperation.DoString:
                fixture.Root.DoString("return 1").Dispose();
                break;
            case NestedOperation.DoStringAsync:
                (await fixture.Root.DoStringAsync("return 1")).Dispose();
                break;
            case NestedOperation.ResumeCoroutine:
                fixture.Coroutine.Resume().Dispose();
                break;
        }
    }

    static async NoFlowTask WithoutContext(Func<Outcome, ValueTask> body, Outcome outcome)
    {
        await Task.Yield();
        await Observe(body, outcome);
    }

    static async Task WithContext(Func<Outcome, ValueTask> body, Outcome outcome)
    {
        await Task.Yield();
        await Observe(body, outcome);
    }

    static async Task Observe(Func<Outcome, ValueTask> body, Outcome outcome)
    {
        outcome.KeptContext = FlowProbe.Value != null;
        outcome.ThreadId = Environment.CurrentManagedThreadId;
        try
        {
            await body(outcome);
        }
        catch (Exception exception)
        {
            outcome.Failure = exception;
        }
    }

    /// <summary>
    /// Runs <c>return f(41)</c>, whose callback performs <paramref name="body"/>
    /// after an await in a helper, on a root of its own; then runs one more
    /// operation on that root.
    /// </summary>
    static async Task<Outcome> RunInCallbackAsync(
        SingleThreadLoop? loop,
        bool nonFlowing,
        Func<Fixture, LuauCallContext, Outcome, ValueTask> body,
        bool bodyReturns)
    {
        var outcome = new Outcome();
        var root = loop == null ? TestBudgets.CreateRoot() : await loop.CreateRootAsync();
        try
        {
            // Without a loop, each step runs on the thread pool, where no
            // synchronization context captures anything.
            Task<T> OnLoop<T>(Func<T> action) => loop == null ? Task.Run(action) : loop.Run(action);

            var fixture = await OnLoop(() => Fixture.Create(root));
            var run = await OnLoop(() =>
            {
                root["f"] = root.CreateAsyncFunction("f", async context =>
                {
                    FlowProbe.Value = "callback";
                    Func<Outcome, ValueTask> step = o => body(fixture, context, o);
                    if (nonFlowing)
                    {
                        await WithoutContext(step, outcome);
                    }
                    else
                    {
                        await WithContext(step, outcome);
                    }

                    if (!bodyReturns || outcome.Failure != null)
                    {
                        context.Return(1);
                    }
                });
                return ReadAll(root.DoStringAsync("local r = f(41) return r, g, shared.k, type(math)"));
            });

            outcome.Results = await run;
            outcome.AfterwardsFailure = await OnLoop(() => Record(() =>
            {
                using var next = root.DoString("return 2");
                Assert.Equal(2, next[0].Read<int>());
            }));
            await OnLoop(() =>
            {
                fixture.Shared.Dispose();
                fixture.Script.Dispose();
                fixture.Disposable.Dispose();
                fixture.Coroutine.Dispose();
                return 0;
            });
            return outcome;
        }
        finally
        {
            if (loop == null)
            {
                root.Dispose();
            }
            else
            {
                await loop.Run(root.Dispose);
            }
        }
    }

    static Task<Outcome> RunAccessAsync(SingleThreadLoop? loop, Access access, bool nonFlowing) =>
        RunInCallbackAsync(
            loop,
            nonFlowing,
            (fixture, context, outcome) =>
            {
                outcome.Observed = Perform(access, context, fixture);
                return default;
            },
            bodyReturns: access == Access.Return);

    static Task<Outcome> RunNestedAsync(SingleThreadLoop? loop, NestedOperation operation, bool nonFlowing) =>
        RunInCallbackAsync(
            loop,
            nonFlowing,
            (fixture, _, _) => Perform(operation, fixture),
            bodyReturns: false);

    /// <summary>
    /// Awaits <paramref name="task"/> inside a non-flowing method, so the
    /// caller's code after it runs as the callback's own work but the
    /// continuation inside the helper has no marker. The callback itself
    /// keeps its context.
    /// </summary>
    static async NoFlowTask WaitWithoutContextCore(Task task)
    {
        await task;
        await Task.Yield();
    }

    static Task WaitWithoutContext(Task task) => WaitWithoutContextCore(task).AsTask();

    internal static async Task<object?[]> ReadAll(ValueTask<LuauResultScope> execution)
    {
        using var results = await execution;
        var values = new object?[results.Length];
        for (var index = 0; index < results.Length; index++)
        {
            values[index] = results[index].Type switch
            {
                LuauType.Nil => null,
                LuauType.Boolean => results[index].Read<bool>(),
                LuauType.Number => results[index].Read<int>(),
                LuauType.String => results[index].Read<string>(),
                var type => type.ToString(),
            };
        }

        return values;
    }

    static int ReadInt(LuauState root, string source)
    {
        using var results = root.DoString(source);
        return results[0].Read<int>();
    }

    internal static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    static Exception? RecordAsync(Func<ValueTask<LuauResultScope>> start)
    {
        try
        {
            var execution = start();
            if (!execution.IsCompleted)
            {
                return new InvalidOperationException("The operation started.");
            }

            execution.Result.Dispose();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
