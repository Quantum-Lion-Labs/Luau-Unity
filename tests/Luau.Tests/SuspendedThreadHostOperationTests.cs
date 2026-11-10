namespace Luau.Tests;

/// <summary>
/// A direct host operation (a global or table access, a display conversion,
/// or a bytecode load) can reach a metamethod, which is Luau code. A thread
/// that has yielded, is suspended by a pending async callback, or ended with an
/// error cannot run code: the VM used to leave the metamethod early with a
/// broken stack, and the next resume of the thread crashed the process. The
/// script decides when it yields, so it decided whether an ordinary host call
/// corrupted its own coroutine.
/// </summary>
/// <remarks>
/// Every test checks the suspended thread's status and stack before it
/// resumes the thread. A regression that corrupts the continuation fails that
/// check instead of crashing the test host on the resume; the native
/// conformance suite covers the crash itself in its own process.
/// </remarks>
public sealed class SuspendedThreadHostOperationTests
{
    static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);

    // Explicit, generous limits: async callbacks here wait on test signals,
    // and nothing may depend on the default wall-clock budget.
    internal static readonly LuauStateOptions Options = LuauStateOptions.UnboundedResources with
    {
        DefaultExecutionOptions = LuauExecutionOptions.Unbounded with
        {
            WallClockLimit = TimeSpan.FromMinutes(2),
        },
    };

    internal static LuauState CreateRoot(bool sandbox)
    {
        var root = LuauState.Create(Options);
        try
        {
            root.OpenBaseLibrary();
            root.OpenCoroutineLibrary();
            root.OpenStringLibrary();
            if (sandbox)
            {
                root.SandboxRoot();
            }

            return root;
        }
        catch
        {
            root.Dispose();
            throw;
        }
    }

    internal static void AssertSuspended(LuauState thread, int expectedTop)
    {
        Assert.Equal(LuauThreadStatus.Suspended, thread.GetStatus());
        Assert.Equal(expectedTop, thread.GetTop());
    }

    static void AssertRootUsable(LuauState root)
    {
        using var results = root.DoString("return 40 + 2");
        Assert.Equal(42, Assert.Single(results).Read<int>());
    }

    [Fact]
    public void ReportedReproductionWritesTheGlobalAndTheThreadResumes()
    {
        using var root = CreateRoot(sandbox: true);
        using var thread = root.CreateSandboxedThread();
        using (var yielded = thread.DoString("coroutine.yield(1) return 'A', x"))
        {
            Assert.Equal(1, Assert.Single(yielded).Read<int>());
        }

        var top = thread.GetTop();

        // The sandbox's write guard is Luau code; it used to run on the
        // yielded thread and leave its stack broken.
        thread["x"] = 5;

        AssertSuspended(thread, top);
        using (var resumed = thread.Resume())
        {
            Assert.Equal(2, resumed.Count);
            Assert.Equal("A", resumed[0].Read<string>());
            Assert.Equal(5, resumed[1].Read<int>());
        }

        Assert.Equal(LuauThreadStatus.Dead, thread.GetStatus());
        AssertRootUsable(root);
    }

    [Fact]
    public void SandboxGuardStillRejectsProtectedGlobalsOnAYieldedThread()
    {
        using var root = CreateRoot(sandbox: true);
        using var thread = root.CreateSandboxedThread();
        using (thread.DoString("coroutine.yield() return type(print)"))
        {
        }

        var top = thread.GetTop();
        var failure = Assert.Throws<LuauException>(() => thread["print"] = 1);
        Assert.Contains("protected host global 'print'", failure.Message, StringComparison.Ordinal);

        AssertSuspended(thread, top);
        using (var resumed = thread.Resume())
        {
            Assert.Equal("function", Assert.Single(resumed).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void GlobalReadAndWriteThroughFunctionMetamethodsOnAYieldedThread()
    {
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using (thread.DoString(
            "setmetatable(getfenv(), { " +
            "  __index = function(_, key) return key .. '!' end, " +
            "  __newindex = function(t, key, value) rawset(t, key, value * 2) end }) " +
            "local before = coroutine.yield() return before, doubled"))
        {
        }

        var top = thread.GetTop();
        Assert.Equal("missing!", thread["missing"].Read<string>());
        thread["doubled"] = 21;
        Assert.Equal(42, thread["doubled"].Read<int>());

        AssertSuspended(thread, top);
        using (var resumed = thread.Resume(new LuauValue[] { "first" }))
        {
            Assert.Equal("first", resumed[0].Read<string>());
            Assert.Equal(42, resumed[1].Read<int>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void ScriptCreatedCoroutineGlobalsAreReachableWhileItIsYielded()
    {
        using var root = CreateRoot(sandbox: true);
        using var script = root.CreateSandboxedThread();
        using var created = script.DoString(
            "local co = coroutine.create(function() coroutine.yield() return value end) " +
            "assert(coroutine.resume(co)) return co");
        var coroutine = created.Read<LuauState>(0);
        var top = coroutine.GetTop();

        coroutine["value"] = "set by host";
        Assert.Equal("set by host", coroutine["value"].Read<string>());

        Assert.Equal(top, coroutine.GetTop());
        using var resumed = script.DoString(
            "return function(co) local ok, value = coroutine.resume(co) return ok, value end");
        using var results = resumed.Read<LuauFunction>(0).Invoke(new LuauValue[] { coroutine });
        Assert.True(results[0].Read<bool>());
        Assert.Equal("set by host", results[1].Read<string>());
    }

    [Fact]
    public void ErrorInAMetamethodOnAYieldedThreadLeavesTheContinuationIntact()
    {
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using (thread.DoString(
            "setmetatable(getfenv(), { __index = function() error('no such global') end }) " +
            "coroutine.yield() return 'A'"))
        {
        }

        var top = thread.GetTop();
        var failure = Assert.Throws<LuauException>(() => { _ = thread["missing"]; });
        Assert.Contains("no such global", failure.Message, StringComparison.Ordinal);

        AssertSuspended(thread, top);
        using (var resumed = thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void MetamethodThatYieldsOnAHostAccessFailsAndLeavesTheContinuationIntact()
    {
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using (thread.DoString(
            "setmetatable(getfenv(), { __index = function() coroutine.yield() return 1 end }) " +
            "coroutine.yield() return 'A'"))
        {
        }

        var top = thread.GetTop();
        var failure = Assert.Throws<LuauException>(() => { _ = thread["missing"]; });
        Assert.Contains("yield", failure.Message, StringComparison.Ordinal);

        AssertSuspended(thread, top);
        using (var resumed = thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void InterruptBudgetStopsARunawayMetamethodOnAYieldedThread()
    {
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using (thread.DoString(
            "setmetatable(getfenv(), { __index = function() while true do end end }) " +
            "coroutine.yield() return 'A'"))
        {
        }

        var top = thread.GetTop();

        // A direct operation outside an operation uses the root's default
        // options; run it from a callback so the explicit budget applies.
        using var read = root.CreateFunction("readMissing", _ => { var ignored = thread["missing"]; });
        root["readMissing"] = read;
        var budgeted = Options.DefaultExecutionOptions with { InterruptCountLimit = 10_000 };
        Assert.Throws<LuauExecutionBudgetException>(
            () => root.DoString("readMissing()", executionOptions: budgeted).Dispose());

        AssertSuspended(thread, top);
        using (var resumed = thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void ManagedCallbackFailureInsideAMetamethodOnAYieldedThreadIsReported()
    {
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using var fail = root.CreateFunction("fail", _ => throw new InvalidOperationException("host refused"));
        root["fail"] = fail;
        using (thread.DoString(
            "setmetatable(getfenv(), { __index = function() return fail() end }) " +
            "coroutine.yield() return 'A'"))
        {
        }

        var top = thread.GetTop();
        var failure = Assert.Throws<LuauManagedCallbackException>(() => { _ = thread["missing"]; });
        Assert.Contains("host refused", failure.ToString(), StringComparison.Ordinal);

        AssertSuspended(thread, top);
        using (var resumed = thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void MetamethodCanReachAnotherYieldedThreadThroughAHostCallback()
    {
        using var root = CreateRoot(sandbox: false);
        using var first = root.CreateThread();
        using var second = root.CreateThread();
        using var readSecond = root.CreateFunction("readSecond", context =>
            context.Return(second["fromSecond"].Read<string>()));
        root["readSecond"] = readSecond;
        using (second.DoString("coroutine.yield() return 'B'"))
        {
        }

        root["fromSecond"] = "nested";
        using (first.DoString(
            "setmetatable(getfenv(), { __index = function(_, key) " +
            "  if key == 'relay' then return readSecond() end end }) " +
            "coroutine.yield() return 'A'"))
        {
        }

        Assert.Equal("nested", first["relay"].Read<string>());

        using (var resumed = first.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        using (var resumed = second.Resume())
        {
            Assert.Equal("B", Assert.Single(resumed).Read<string>());
        }
    }

    [Fact]
    public void BytecodeLoadedOnAYieldedThreadUsesItsGlobalsAndResolvesImportsSafely()
    {
        using var root = CreateRoot(sandbox: true);
        using var thread = root.CreateSandboxedThread();
        // Loading resolves the import "probe.value" against the thread's
        // environment, and probe's __index is Luau code.
        using (thread.DoString(
            "probe = setmetatable({}, { __index = function(_, key) return key .. ' via index' end }) " +
            "secret = 7 coroutine.yield() return 'A'"))
        {
        }

        var top = thread.GetTop();
        var output = LuauCompiler.Compile("return probe.value, secret"u8);
        using var function = thread.LoadCompilerOutput(output, "@suspended/load.luau");

        AssertSuspended(thread, top);
        using (var invoked = function.Invoke())
        {
            Assert.Equal("value via index", invoked[0].Read<string>());
            Assert.Equal(7, invoked[1].Read<int>());
        }

        using (var resumed = thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public async Task AsyncCallbackFormatsAnArgumentWithToString()
    {
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        string? formatted = null;
        using var log = root.CreateAsyncFunction("log", context =>
        {
            formatted = context.ToDisplayString(0, 256, out _);
            context.Return(formatted);
            return default;
        });
        root["log"] = log;

        using (var results = await thread.DoStringAsync(
            "local value = setmetatable({}, { __tostring = function() return 'formatted by script' end }) " +
            "return log(value), 'after'").AsTask().WaitAsync(TestTimeout))
        {
            Assert.Equal("formatted by script", results[0].Read<string>());
            Assert.Equal("after", results[1].Read<string>());
        }

        Assert.Equal("formatted by script", formatted);
        AssertRootUsable(root);
    }

    [Fact]
    public async Task AsyncCallbackOnAChildIndexesATableThroughLuauCodeOnTheRunnableRoot()
    {
        // The child is suspended, but table operations run on the root, which
        // can run code. The interrupt poll used to report the callback's own
        // suspension as new work and stop that code as a hard stop.
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using var inspect = root.CreateAsyncFunction("inspect", context =>
        {
            using var table = context.Read<LuauTable>(0);
            context.Return(table["anything"].Read<string>());
            return default;
        });
        root["inspect"] = inspect;

        using (var results = await thread.DoStringAsync(
            "local value = setmetatable({}, { __index = function(_, key) " +
            "  local n = 0 for i = 1, 1000 do n += i end return key .. ' ' .. n end }) " +
            "return inspect(value)").AsTask().WaitAsync(TestTimeout))
        {
            Assert.Equal("anything 500500", Assert.Single(results).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public async Task AsyncCallbackOnTheSuspendedRootFormatsAndIndexesItsArguments()
    {
        // Table operations run on the root, and an async callback invoked from
        // root code suspends the root itself.
        using var root = CreateRoot(sandbox: false);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string? formatted = null;
        string? indexed = null;
        using var inspect = root.CreateAsyncFunction("inspect", async context =>
        {
            formatted = context.ToDisplayString(0, 256, out _);
            using var table = context.Read<LuauTable>(0);
            indexed = table["anything"].Read<string>();
            table["written"] = 21;
            await release.Task.ConfigureAwait(false);
            context.Return(indexed);
        });
        root["inspect"] = inspect;

        var running = root.DoStringAsync(
            "local store = {} " +
            "local value = setmetatable({}, { " +
            "  __tostring = function() return 'formatted' end, " +
            "  __index = function(_, key) return 'index ' .. key end, " +
            "  __newindex = function(_, key, v) store[key] = v * 2 end }) " +
            "return inspect(value), store.written").AsTask();
        release.SetResult();

        using (var results = await running.WaitAsync(TestTimeout))
        {
            Assert.Equal("index anything", results[0].Read<string>());
            Assert.Equal(42, results[1].Read<int>());
        }

        Assert.Equal("formatted", formatted);
        Assert.Equal("index anything", indexed);
        AssertRootUsable(root);
    }

    [Fact]
    public async Task AsyncCallbackWritesAndReadsGlobalsOfItsSuspendedSandboxedThread()
    {
        using var root = CreateRoot(sandbox: false);
        using var host = root.CreateAsyncFunction("host", context =>
        {
            context.State["fromHost"] = 5;
            context.Return(context.State["fromScript"].Read<int>());
            return default;
        });
        root["host"] = host;
        root.SandboxRoot();
        using var thread = root.CreateSandboxedThread();

        using (var results = await thread.DoStringAsync(
            "fromScript = 2 local seen = host() return seen, fromHost").AsTask().WaitAsync(TestTimeout))
        {
            Assert.Equal(2, results[0].Read<int>());
            Assert.Equal(5, results[1].Read<int>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public async Task AsyncCallbackOnAFullFrameNeedsNoMoreSlotsThanADirectAccess()
    {
        // The frame holds 7999 arguments. Writing a global pushes the value
        // into the last slot, as on a running thread, and still succeeds;
        // once a result fills the frame, a read that needs one more slot is
        // refused with the frame-slot limit, also as on a running thread.
        using var root = CreateRoot(sandbox: false);
        root.OpenTableLibrary();
        Exception? writeFailure = null;
        Exception? readFailure = null;
        using var host = root.CreateAsyncFunction("host", context =>
        {
            writeFailure = Record.Exception(() => context.State["fromHost"] = 5);
            context.Return(1);
            readFailure = Record.Exception(() => { _ = context.State["fromHost"]; });
            return default;
        });
        root["host"] = host;
        using var thread = root.CreateThread();

        using (var results = await thread.DoStringAsync(
            "local t = table.create(7999, 1) local one = host(table.unpack(t)) return one, fromHost")
            .AsTask().WaitAsync(TestTimeout))
        {
            Assert.Equal(1, results[0].Read<int>());
            Assert.Equal(5, results[1].Read<int>());
        }

        Assert.Null(writeFailure);
        Assert.IsType<LuauStackLimitException>(readFailure);
        AssertRootUsable(root);
    }

    [Fact]
    public async Task AsyncCallbackOnTheSuspendedRootOpensALibraryAndLoadsBytecode()
    {
        using var root = CreateRoot(sandbox: false);
        var output = LuauCompiler.Compile("return string.upper(greeting)"u8);
        LuauFunction? loaded = null;
        using var configure = root.CreateAsyncFunction("configure", context =>
        {
            // Both register into, or resolve against, the suspended root's
            // globals, which may have metamethods.
            context.State.OpenTableLibrary();
            loaded = context.State.LoadCompilerOutput(output, "@suspended/root-load.luau");
            context.Return(loaded);
            return default;
        });
        root["configure"] = configure;

        using (var results = await root.DoStringAsync(
            "greeting = 'hello' local upper = configure() return upper(), type(table.concat)")
            .AsTask().WaitAsync(TestTimeout))
        {
            Assert.Equal("HELLO", results[0].Read<string>());
            Assert.Equal("function", results[1].Read<string>());
        }

        loaded?.Dispose();
        AssertRootUsable(root);
    }

    [Fact]
    public void HostOperationThreadThatAHostLeftSuspendedIsReplaced()
    {
        // A metamethod can hand the host the thread it runs on; the host can
        // then leave that thread suspended. Later operations must not run
        // code there.
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using (thread.DoString(
            "setmetatable(getfenv(), { __index = function(_, key) " +
            "  if key == 'runner' then return coroutine.running() end return key end }) " +
            "coroutine.yield() return 'A'"))
        {
        }

        var runner = thread["runner"].Read<LuauState>();
        Assert.False(runner.IsMainThread);
        using (var yielded = runner.DoString("coroutine.yield('held') return 'released'"))
        {
            Assert.Equal("held", Assert.Single(yielded).Read<string>());
        }

        Assert.Equal("after", thread["after"].Read<string>());
        thread["written"] = 1;

        using (var released = runner.Resume())
        {
            Assert.Equal("released", Assert.Single(released).Read<string>());
        }

        using (var resumed = thread.Resume())
        {
            Assert.Equal("A", Assert.Single(resumed).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void MetamethodThatResumesTheSuspendedThreadItselfDoesNotCorruptIt()
    {
        // The metamethod runs on another thread, so it can resume the
        // yielded thread whose global the host reads. That is ordinary Luau
        // behavior; the host operation must still leave both threads sound.
        using var root = CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using (thread.DoString(
            "local me = coroutine.running() " +
            "setmetatable(getfenv(), { __index = function() local ok, value = coroutine.resume(me) return value end }) " +
            "coroutine.yield() return 'finished by the metamethod'"))
        {
        }

        Assert.Equal("finished by the metamethod", thread["missing"].Read<string>());

        // The script finished the coroutine, so there is nothing to resume.
        Assert.Throws<LuauException>(() => thread.Resume().Dispose());
        using (var next = thread.DoString("return 'next call'"))
        {
            Assert.Equal("next call", Assert.Single(next).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void SandboxingAScriptCoroutineThatEndedWithAnErrorIsRejectedBeforeAnyChange()
    {
        using var root = CreateRoot(sandbox: true);
        using var script = root.CreateSandboxedThread();
        using var created = script.DoString(
            "local co = coroutine.create(function() error('boom') end) " +
            "assert(not coroutine.resume(co)) return co");
        var errored = created.Read<LuauState>(0);

        var first = Assert.Throws<InvalidOperationException>(errored.SandboxThread);
        Assert.Contains("ended with an error", first.Message, StringComparison.Ordinal);

        // Nothing was replaced: a retry reports the same rejection rather
        // than a half-applied sandbox or a native failure.
        var second = Assert.Throws<InvalidOperationException>(errored.SandboxThread);
        Assert.Equal(first.Message, second.Message);

        // The coroutine still shares its creator's globals.
        errored["sharedWithCreator"] = "yes";
        Assert.Equal("yes", script["sharedWithCreator"].Read<string>());
        AssertRootUsable(root);
    }
}

/// <summary>
/// A host that publishes per-frame state to a script keeps doing so while the
/// script waits in a yield, so the suspended-thread paths are per-frame paths
/// too and must not allocate once warm.
/// </summary>
[Collection(AllocationSensitiveCollection.Name)]
public sealed class SuspendedThreadHostOperationAllocationTests
{
    [Fact]
    public void GlobalWriteAndReadOnAYieldedSandboxedThreadAllocateNothingOnceWarm()
    {
        using var root = SuspendedThreadHostOperationTests.CreateRoot(sandbox: true);
        using var thread = root.CreateSandboxedThread();
        using (thread.DoString("coroutine.yield() return score"))
        {
        }

        var top = thread.GetTop();
        var value = LuauValue.FromNumber(42);

        Assert.Equal(0, OperationReuseTests.AllocatedBytesPerWindow(() => thread["score"] = value));
        Assert.Equal(0, OperationReuseTests.AllocatedBytesPerWindow(() =>
        {
            if (thread["score"].Type != LuauType.Number)
            {
                throw new InvalidOperationException("Unexpected global value.");
            }
        }));

        SuspendedThreadHostOperationTests.AssertSuspended(thread, top);
        using var resumed = thread.Resume();
        Assert.Equal(42, Assert.Single(resumed).Read<int>());
    }

    [Fact]
    public void GlobalReadThroughAFunctionIndexOnAYieldedThreadAllocatesNothingOnceWarm()
    {
        using var root = SuspendedThreadHostOperationTests.CreateRoot(sandbox: false);
        using var thread = root.CreateThread();
        using (thread.DoString(
            "setmetatable(getfenv(), { __index = function() return 5 end }) coroutine.yield() return 'A'"))
        {
        }

        var top = thread.GetTop();
        Assert.Equal(0, OperationReuseTests.AllocatedBytesPerWindow(() =>
        {
            if (thread["missing"].Type != LuauType.Number)
            {
                throw new InvalidOperationException("Unexpected global value.");
            }
        }));

        SuspendedThreadHostOperationTests.AssertSuspended(thread, top);
        using var resumed = thread.Resume();
        Assert.Equal("A", Assert.Single(resumed).Read<string>());
    }
}
