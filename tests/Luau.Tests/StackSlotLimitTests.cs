namespace Luau.Tests;

/// <summary>
/// A Luau frame visible to the host holds at most 8000 values (LUAI_MAXCSTACK).
/// Scripts and hosts can exceed that by returning, yielding, or passing
/// thousands of values. Each such operation must fail on its own with a typed
/// exception, write nothing past the native stack, and leave the root usable.
/// </summary>
public sealed class StackSlotLimitTests
{
    // 8003 values: four literals plus 7999 unpacked array entries. The VM can
    // build this frame itself, but it exceeds the host's per-frame limit.
    const string OverfullReturn = "local t = table.create(7999, 1) return 1, 2, 3, 4, table.unpack(t)";
    const int OverfullCount = 8003;

    // Enough repetitions to walk far past Luau's five-slot EXTRA_STACK reserve
    // if any failed operation leaked one value onto the stack.
    const int Repetitions = 64;

    static readonly LuauExecutionOptions UnboundedExecution = LuauExecutionOptions.Unbounded;

    static LuauState CreateRoot(LuauExecutionOptions? execution = null)
    {
        var root = execution == null
            ? LuauState.Create()
            : LuauState.Create(new LuauStateOptions { DefaultExecutionOptions = execution });
        root.OpenBaseLibrary();
        root.OpenTableLibrary();
        root.OpenCoroutineLibrary();
        return root;
    }

    static void AssertRootUsable(LuauState root)
    {
        using var results = root.DoString("return 42", "@stack-limit/recovery.luau");
        Assert.Equal(42, Assert.Single(results).Read<int>());
    }

    [Fact]
    public void OverfullRootReturnHitsTheResultLimitUnderDefaultLimits()
    {
        using var root = CreateRoot();

        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            var exception = Assert.Throws<LuauResultLimitException>(
                () => root.DoString(OverfullReturn, "@stack-limit/default.luau"));
            Assert.Equal(OverfullCount, exception.ActualCount);
            Assert.Equal(LuauExecutionOptions.Default.MaxResultCount, exception.Limit);
            AssertRootUsable(root);
        }
    }

    [Theory]
    [InlineData(OverfullReturn)]
    [InlineData("local t = table.create(7999, {}) return t, table.unpack(t)")]
    [InlineData("local t = table.create(7999, 'x') return 'x', table.unpack(t)")]
    public void OverfullRootReturnFailsCleanlyWithoutAResultLimit(string source)
    {
        using var root = CreateRoot(UnboundedExecution);

        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            var exception = Assert.Throws<LuauStackLimitException>(
                () => root.DoString(source, "@stack-limit/unbounded.luau"));
            Assert.Equal("@stack-limit/unbounded.luau", exception.ChunkName);
            AssertRootUsable(root);
        }
    }

    [Fact]
    public void ResultsThatLeaveOneFreeSlotAreStillReturned()
    {
        using var root = CreateRoot(UnboundedExecution);

        using var results = root.DoString("return table.unpack(table.create(7999, 7))");

        Assert.Equal(7999, results.Count);
        Assert.Equal(7, results[0].Read<int>());
        Assert.Equal(7, results[7998].Read<int>());
        AssertRootUsable(root);
    }

    [Fact]
    public void OverfullChildThreadReturnFailsCleanlyAndBothThreadsRecover()
    {
        using var root = CreateRoot(UnboundedExecution);
        using var child = root.CreateThread();

        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            Assert.Throws<LuauStackLimitException>(
                () => child.DoString(OverfullReturn, "@stack-limit/child.luau"));
        }

        using (var childResults = child.DoString("return 'child'"))
        {
            Assert.Equal("child", Assert.Single(childResults).Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void OverfullCoroutineYieldAndResumeResultsFailCleanly()
    {
        using var root = CreateRoot(UnboundedExecution);

        using (var yielder = root.CreateThread())
        {
            Assert.Throws<LuauStackLimitException>(() => yielder.DoString(
                "local t = table.create(7999, 1) coroutine.yield(1, 2, table.unpack(t)) return 'unreachable'",
                "@stack-limit/yield.luau"));
        }

        using var coroutine = root.CreateThread();
        using (var first = coroutine.DoString(
            "local n = coroutine.yield('ready') local t = table.create(7999, 2) return n, n, table.unpack(t)"))
        {
            Assert.Equal("ready", Assert.Single(first).Read<string>());
        }

        Assert.Throws<LuauStackLimitException>(() => coroutine.Resume([8001]));
        AssertRootUsable(root);
    }

    [Fact]
    public void ResumeWithMoreArgumentsThanAFrameHoldsIsRejectedAndCanBeRetried()
    {
        using var root = CreateRoot(UnboundedExecution);
        using var coroutine = root.CreateThread();
        using (var first = coroutine.DoString("local a, b = coroutine.yield('ready') return a + b"))
        {
            Assert.Equal("ready", Assert.Single(first).Read<string>());
        }

        var tooMany = new LuauValue[9000];
        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            Assert.Throws<LuauStackLimitException>(() => coroutine.Resume(tooMany));
        }

        using var resumed = coroutine.Resume([20, 22]);
        Assert.Equal(42, Assert.Single(resumed).Read<int>());
        AssertRootUsable(root);
    }

    [Fact]
    public void InvokeWithOverfullResultsOrArgumentsFailsCleanly()
    {
        using var root = CreateRoot(UnboundedExecution);
        using var factory = root.DoString(
            "return function(...) " +
            "local count = select('#', ...) " +
            "if count == 1 then local t = table.create(7999, 0) return 1, 2, table.unpack(t) end " +
            "return count, ... end");
        var function = factory[0].Read<LuauFunction>();

        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            Assert.Throws<LuauStackLimitException>(() => function.Invoke([1]));
        }

        var tooManyArguments = new LuauValue[9000];
        tooManyArguments[0] = 1;
        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            Assert.Throws<LuauStackLimitException>(() => function.Invoke(tooManyArguments));
        }

        using (var results = function.Invoke([2, "a", "b"]))
        {
            Assert.Equal(4, results.Count);
            Assert.Equal(3, results[0].Read<int>());
            Assert.Equal("b", results[3].Read<string>());
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void HostCallbackReturningMoreValuesThanAFrameHoldsFailsCleanly()
    {
        using var root = CreateRoot(UnboundedExecution);
        root["many"] = root.CreateFunction("many", context =>
        {
            for (var index = 0; index < 9000; index++)
            {
                context.Return(index);
            }
        });

        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            var exception = Assert.Throws<LuauManagedCallbackException>(
                () => root.DoString("return many()", "@stack-limit/callback.luau"));
            Assert.Equal("many", exception.CallbackName);
            Assert.IsType<LuauStackLimitException>(exception.InnerException);
            AssertRootUsable(root);
        }

        using var caught = root.DoString("local ok = pcall(many) return ok");
        Assert.False(Assert.Single(caught).Read<bool>());
        AssertRootUsable(root);
    }

    [Fact]
    public async Task AsyncHostCallbackReturningMoreValuesThanAFrameHoldsFailsCleanly()
    {
        using var root = CreateRoot(UnboundedExecution);
        root["many"] = root.CreateAsyncFunction("many", async context =>
        {
            await Task.Yield();
            for (var index = 0; index < 9000; index++)
            {
                context.Return(index);
            }
        });

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var exception = await Assert.ThrowsAsync<LuauManagedCallbackException>(
                () => root.DoStringAsync("return many()", "@stack-limit/async-callback.luau").AsTask());
            Assert.IsType<LuauStackLimitException>(exception.InnerException);
        }

        using var results = await root.DoStringAsync("return 42");
        Assert.Equal(42, Assert.Single(results).Read<int>());
    }

    [Fact]
    public void RequiredModuleReturningAnOverfullFrameFailsCleanly()
    {
        using var root = CreateRoot(UnboundedExecution);
        root.OpenRequireLibrary(new LuauModuleMap(new Dictionary<string, byte[]>
        {
            ["overfull"] = System.Text.Encoding.UTF8.GetBytes(OverfullReturn),
        }));

        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            var exception = Assert.Throws<LuauManagedCallbackException>(
                () => root.DoString("return require('overfull')", "@stack-limit/require.luau"));
            Assert.IsType<LuauStackLimitException>(exception.InnerException);
            AssertRootUsable(root);
        }
    }

    [Fact]
    public void ScriptPassingAFullFrameToACallbackCanStillFailCleanly()
    {
        using var root = CreateRoot(UnboundedExecution);
        var observed = 0;
        root["count"] = root.CreateFunction("count", context =>
        {
            observed = context.ArgumentCount;
            context.Return(context.ArgumentCount);
        });

        // The callback frame already holds 7999 arguments; one return value
        // still fits, so this succeeds.
        using (var results = root.DoString("return count(table.unpack(table.create(7999, 0)))"))
        {
            Assert.Equal(7999, Assert.Single(results).Read<int>());
            Assert.Equal(7999, observed);
        }

        // A full 8000-argument frame cannot hold the return value.
        var exception = Assert.Throws<LuauManagedCallbackException>(
            () => root.DoString("local t = table.create(7999, 0) return count(0, table.unpack(t))"));
        Assert.IsType<LuauStackLimitException>(exception.InnerException);
        AssertRootUsable(root);
    }

    // 7999 and 8000 arguments: the second fills the callback's frame, leaving
    // no room above the arguments for a failure value.
    public static IEnumerable<object[]> CaughtFullFrameCallbackFailures() =>
        from call in new[]
        {
            "fail(table.unpack(table.create(7999, 0)))",
            "fail(0, table.unpack(table.create(7999, 0)))",
        }
        from wrapper in new[]
        {
            "local ok, e = pcall(function() return {0} end) return ok, type(e)",
            "local ok, e = xpcall(function() return {0} end, function(e) return e end) return ok, type(e)",
        }
        select new object[] { string.Format(wrapper, call) };

    static void AssertCallbackFailureWasCaught(LuauResultScope results)
    {
        using (results)
        {
            Assert.Equal(2, results.Count);
            Assert.False(results[0].Read<bool>());
            Assert.Equal("userdata", results[1].Read<string>());
        }
    }

    [Theory]
    [MemberData(nameof(CaughtFullFrameCallbackFailures))]
    public void FailedCallbackWithAFullArgumentFrameIsCaughtByTheScript(string source)
    {
        using var root = CreateRoot(UnboundedExecution);
        root["fail"] = root.CreateFunction("fail", _ => throw new InvalidOperationException("boom"));
        using var thread = root.CreateThread();

        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            AssertCallbackFailureWasCaught(root.DoString(source, "@stack-limit/caught-root.luau"));
            AssertCallbackFailureWasCaught(thread.DoString(source, "@stack-limit/caught-thread.luau"));
            AssertRootUsable(root);
        }
    }

    [Theory]
    [MemberData(nameof(CaughtFullFrameCallbackFailures))]
    public async Task FailedCallbackWithAFullArgumentFrameIsCaughtDuringAsyncExecution(string source)
    {
        using var root = CreateRoot(UnboundedExecution);
        root["fail"] = root.CreateFunction("fail", _ => throw new InvalidOperationException("boom"));

        for (var attempt = 0; attempt < 8; attempt++)
        {
            AssertCallbackFailureWasCaught(await root.DoStringAsync(source, "@stack-limit/caught-async.luau"));
        }

        AssertRootUsable(root);
    }

    [Theory]
    [MemberData(nameof(CaughtFullFrameCallbackFailures))]
    public async Task FailedAsyncCallbackWithAFullArgumentFrameIsCaughtByTheScript(string source)
    {
        using var root = CreateRoot(UnboundedExecution);
        root["fail"] = root.CreateAsyncFunction("fail", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });

        for (var attempt = 0; attempt < 8; attempt++)
        {
            AssertCallbackFailureWasCaught(await root.DoStringAsync(source, "@stack-limit/caught-async-callback.luau"));
        }

        AssertRootUsable(root);
    }

    [Fact]
    public void UncaughtCallbackFailureWithAFullArgumentFrameKeepsItsCause()
    {
        using var root = CreateRoot(UnboundedExecution);
        root["fail"] = root.CreateFunction("fail", _ => throw new InvalidOperationException("boom"));

        for (var attempt = 0; attempt < Repetitions; attempt++)
        {
            var exception = Assert.Throws<LuauManagedCallbackException>(
                () => root.DoString("return fail(0, table.unpack(table.create(7999, 0)))"));
            Assert.Equal("fail", exception.CallbackName);
            Assert.Equal("boom", Assert.IsType<InvalidOperationException>(exception.InnerException).Message);
            AssertRootUsable(root);
        }
    }

    // A failed callback's frame is replaced by its failure value, but a pending
    // failure can also be delivered to a frame the callback never owned. Here
    // the callback fails in a child coroutine whose suspension a non-yieldable
    // metamethod rejects; the failure then reaches the outer Lua function
    // through a later interrupt. That function's locals must survive.
    const string RejectedSuspensionSource = @"
local read
local ok = pcall(function(value)
    read = function() return value end
    value += 1
    local co = coroutine.create(function() fail() end)
    pcall(function()
        local proxy = setmetatable({}, { __index = function() return coroutine.resume(co) end })
        return proxy.x
    end)
    while true do end
end, 41)
return ok, read()";

    static void AssertInterruptedFrameKeptItsLocals(LuauResultScope results)
    {
        using (results)
        {
            Assert.Equal(2, results.Count);
            Assert.False(results[0].Read<bool>());
            Assert.Equal(42, results[1].Read<int>());
        }
    }

    [Fact]
    public async Task CallbackFailureDeliveredToAnotherFrameLeavesItsLocalsIntact()
    {
        // The pending failure ends the script's loop at its first interrupt.
        // The wall-clock limit only stops the loop if that delivery regresses,
        // so keep it well clear of a busy runner's scheduling delays.
        using var root = CreateRoot(LuauExecutionOptions.Default with
        {
            InterruptCountLimit = null,
            WallClockLimit = TimeSpan.FromSeconds(10),
        });
        root["fail"] = root.CreateFunction("fail", _ => throw new InvalidOperationException("boom"));
        using var thread = root.CreateThread();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            AssertInterruptedFrameKeptItsLocals(root.DoString(RejectedSuspensionSource));
            AssertInterruptedFrameKeptItsLocals(thread.DoString(RejectedSuspensionSource));
            AssertInterruptedFrameKeptItsLocals(await root.DoStringAsync(RejectedSuspensionSource));
            AssertInterruptedFrameKeptItsLocals(await thread.DoStringAsync(RejectedSuspensionSource));
            AssertRootUsable(root);
        }
    }

    // Several full-frame callback failures in one operation, delivered through
    // each path in turn: suspended and injected, raised immediately inside a
    // non-yieldable metamethod, and through nested coroutines. Each must be
    // caught where the script expects, and the chunk's own locals must survive.
    const string RepeatedFullFrameFailuresSource = @"
local kept = 41
local function keep() return kept end
kept += 1
local function full() return fail(0, table.unpack(table.create(7999, 0))) end
local a = pcall(full)
local b = pcall(function()
    local proxy = setmetatable({}, { __index = function() return full() end })
    return proxy.x
end)
local c = pcall(full)
local d = coroutine.wrap(function() return pcall(full) end)()
local e = pcall(coroutine.wrap(function() return coroutine.wrap(full)() end))
local co = coroutine.create(function() return coroutine.wrap(function() return pcall(full) end)() end)
local resumed, f = coroutine.resume(co)
local g = pcall(full)
return a, b, c, d, e, resumed, f, g, keep()";

    static void AssertRepeatedFullFrameFailuresWereCaught(LuauResultScope results)
    {
        using (results)
        {
            Assert.Equal(9, results.Count);
            Assert.Equal(
                [false, false, false, false, false, true, false, false],
                results.Take(8).Select(value => value.Read<bool>()));
            Assert.Equal(42, results[8].Read<int>());
        }
    }

    [Fact]
    public async Task RepeatedFullFrameCallbackFailuresInOneOperationAreEachCaught()
    {
        using var root = CreateRoot(UnboundedExecution);
        root["fail"] = root.CreateFunction("fail", _ => throw new InvalidOperationException("boom"));
        using var thread = root.CreateThread();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            AssertRepeatedFullFrameFailuresWereCaught(root.DoString(RepeatedFullFrameFailuresSource));
            AssertRepeatedFullFrameFailuresWereCaught(thread.DoString(RepeatedFullFrameFailuresSource));
            AssertRepeatedFullFrameFailuresWereCaught(await root.DoStringAsync(RepeatedFullFrameFailuresSource));
            AssertRepeatedFullFrameFailuresWereCaught(await thread.DoStringAsync(RepeatedFullFrameFailuresSource));
            AssertRootUsable(root);
        }
    }

    [Fact]
    public async Task RepeatedFullFrameAsyncCallbackFailuresInOneOperationAreEachCaught()
    {
        using var root = CreateRoot(UnboundedExecution);
        root["fail"] = root.CreateAsyncFunction("fail", async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("boom");
        });
        using var thread = root.CreateThread();

        for (var attempt = 0; attempt < 8; attempt++)
        {
            AssertRepeatedFullFrameFailuresWereCaught(await root.DoStringAsync(RepeatedFullFrameFailuresSource));
            AssertRepeatedFullFrameFailuresWereCaught(await thread.DoStringAsync(RepeatedFullFrameFailuresSource));
        }

        AssertRootUsable(root);
    }
}
