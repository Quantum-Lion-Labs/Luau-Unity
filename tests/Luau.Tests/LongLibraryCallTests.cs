using System.Diagnostics;
using System.Text;

namespace Luau.Tests;

/// <summary>
/// One library call that does a great deal of work in C used to run to
/// completion whatever the budgets said: the plain-text search behind
/// <c>string.find</c> and <c>string.split</c> took time that grows with the
/// product of its two inputs, and <c>table.sort</c>, <c>table.concat</c>, and
/// <c>utf8.len</c> ran for up to seconds. The native host now replaces them
/// with versions that reach an interrupt safepoint while they work.
/// </summary>
/// <remarks>
/// <para>
/// Each case's inputs fit the default 64 MB memory limit, and its call takes
/// a hundred milliseconds or more even with the host's linear search (the
/// sorts run for minutes). The statement after the call sets
/// <c>reached</c>, and no safepoint comes between the call's return and that
/// statement, so <c>reached</c> stays nil only when a stop ends the call from
/// inside. That, not the elapsed time, is what each test checks: a call that
/// ran to completion fails it however fast the runner is. The time bounds
/// only catch a hang.
/// </para>
/// <para>
/// The setup runs as its own operation under <see cref="TestBudgets.Execution"/>,
/// so that the budget under test applies to the call alone.
/// </para>
/// </remarks>
public sealed class LongLibraryCallTests
{
    static readonly TimeSpan StopBound = TimeSpan.FromSeconds(10);

    sealed record LongCall(string Setup, string Call);

    // A string of 50 MB with period 2, and a needle of 10,000 bytes with the
    // same period except at its end: upstream's search compares almost the
    // whole needle at every other position, for about three seconds.
    const string PeriodicSubject = "hay = string.rep('ab', 25 * 1048576) needle = string.rep('ab', 5000) .. 'ba'";

    static readonly Dictionary<string, LongCall> Calls = new()
    {
        ["find plain"] = new(PeriodicSubject, "local r = string.find(hay, needle, 1, true)"),
        ["find without specials"] = new(PeriodicSubject, "local r = string.find(hay, needle)"),
        ["find method"] = new(PeriodicSubject, "local r = hay:find(needle, 1, true)"),
        ["find saved reference"] = new(PeriodicSubject + " savedfind = string.find", "local r = savedfind(hay, needle, 1, true)"),
        ["split"] = new(
            "hay = string.rep('ab', 12 * 1048576) needle = string.rep('ab', 5000) .. 'ba'",
            "local r = hay:split(needle)"),
        // Two 12 MB strings that differ only in their last byte, 20,000 times:
        // each comparison reads 12 MB.
        ["sort"] = new(
            "local a = string.rep('x', 12 * 1048576) .. 'a' local b = string.rep('x', 12 * 1048576) .. 'b' " +
            "t = table.create(20000) for i = 1, 20000 do t[i] = i % 2 == 0 and a or b end",
            "table.sort(t)"),
        ["sort with a C comparison"] = new(
            "t = table.create(1000000) for i = 1, 1000000 do t[i] = i end",
            "table.sort(t, rawequal)"),
        // Each number becomes an interned string: about 120 ms.
        ["concat"] = new(
            "t = table.create(700000) for i = 1, 700000 do t[i] = i + 0.5 end",
            "local r = table.concat(t, ',')"),
        ["utf8.len"] = new("s = string.rep('\\u{20AC}', 20 * 1048576)", "local r = utf8.len(s)"),
    };

    public static TheoryData<string> CallNames()
    {
        var names = new TheoryData<string>();
        foreach (var name in Calls.Keys)
        {
            names.Add(name);
        }
        return names;
    }

    static LuauExecutionOptions CountOnly(long limit) =>
        LuauExecutionOptions.Unbounded with { InterruptCountLimit = limit };

    static LuauState CreateRoot()
    {
        // Every default but the wall clock, which the tests that are about it
        // set per operation.
        var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenMathLibrary();
        root.OpenStringLibrary();
        root.OpenTableLibrary();
        root.OpenUtf8Library();
        return root;
    }

    static LuauState Prepare(string name)
    {
        var root = CreateRoot();
        root.DoString(Calls[name].Setup, "@long-call/setup").Dispose();
        return root;
    }

    static bool Reached(LuauState state)
    {
        using var result = state.DoString("return reached");
        return result[0].Type != LuauType.Nil;
    }

    // The stop left the stack as it was and the root runs code again.
    static void RequireUsable(LuauState root, int top)
    {
        Assert.Equal(top, root.GetTop());
        using var result = root.DoString("return 1 + 1");
        Assert.Equal(2, Assert.Single(result).Read<int>());
        Assert.Equal(top, root.GetTop());
    }

    [Fact]
    public void ThePlainSearchesThatTookASecondFinishWithinTheDefaultLimit()
    {
        // These took about a second each in one uninterruptible call.
        using var root = LuauState.Create();
        root.OpenStringLibrary();
        root.DoString("hay = string.rep('a', 400000) needle = string.rep('a', 199999) .. 'b'").Dispose();

        using var find = root.DoString("return string.find(hay, needle, 1, true)", "@long-call/find-400k");
        Assert.Equal(LuauType.Nil, Assert.Single(find).Type);
        using var split = root.DoString("return #string.split(hay, needle)", "@long-call/split-400k");
        Assert.Equal(1, Assert.Single(split).Read<int>());
        using var found = root.DoString("return string.find(hay .. 'b', needle, 1, true)", "@long-call/find-400k-hit");
        Assert.Equal(200002, found[0].Read<int>());
        Assert.Equal(400001, found[1].Read<int>());
    }

    [Fact]
    public void TheDefaultWallClockLimitStopsALongSort()
    {
        // About ten minutes of comparisons, and one call.
        using var root = LuauState.Create();
        root.OpenTableLibrary();
        root.OpenStringLibrary();
        root.DoString(Calls["sort"].Setup, executionOptions: TestBudgets.Execution).Dispose();
        var top = root.GetTop();
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<LuauExecutionBudgetException>(
            () => root.DoString("table.sort(t) reached = true", "@long-call/default-sort"));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
        Assert.False(Reached(root));
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public void WallClockLimitStopsTheCallInside(string name)
    {
        using var root = Prepare(name);
        var top = root.GetTop();
        var stopwatch = Stopwatch.StartNew();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            Calls[name].Call + " reached = true",
            "@long-call/wall-clock",
            executionOptions: LuauExecutionOptions.Unbounded with { WallClockLimit = TimeSpan.FromMilliseconds(1) }));

        Assert.Equal(LuauExecutionBudgetKind.WallClock, exception.BudgetKind);
        Assert.Equal("@long-call/wall-clock", exception.ChunkName);
        Assert.True(exception.Elapsed >= TimeSpan.FromMilliseconds(1));
        Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
        Assert.False(Reached(root));
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public void InterruptCountLimitStopsTheCallInside(string name)
    {
        // The safepoints the call reaches count like pattern-matching steps:
        // the limit stops it at exactly limit + 1.
        using var root = Prepare(name);
        var top = root.GetTop();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            Calls[name].Call + " reached = true",
            "@long-call/count",
            executionOptions: CountOnly(100)));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal(100, exception.InterruptCountLimit);
        Assert.Equal(101, exception.ObservedInterruptCount);
        Assert.False(Reached(root));
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public void ACallOnAChildThreadStopsInside(string name)
    {
        using var root = Prepare(name);
        var child = root.CreateThread();
        var rootTop = root.GetTop();
        var childTop = child.GetTop();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => child.DoString(
            Calls[name].Call + " reached = true",
            "@long-call/child",
            executionOptions: CountOnly(100)));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal(childTop, child.GetTop());
        Assert.False(Reached(root));
        using var result = child.DoString("return 3");
        Assert.Equal(3, Assert.Single(result).Read<int>());
        RequireUsable(root, rootTop);
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public void ACallInsidePcallStopsAndTheStopIsRaisedAgain(string name)
    {
        // pcall catches the hard stop, which comes back at the next safepoint.
        using var root = Prepare(name);
        var top = root.GetTop();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "local ok = pcall(function() " + Calls[name].Call + " end) completed = ok",
            "@long-call/pcall",
            executionOptions: CountOnly(100)));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        using (var completed = root.DoString("return completed"))
        {
            Assert.False(completed[0].Read<bool>());
        }
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public void ACallInARequiredModuleStopsInside(string name)
    {
        // require runs the module as a nested protected call inside its host
        // callback, under the requiring operation's budgets.
        using var root = Prepare(name);
        root.OpenRequireLibrary(new LuauModuleMap(new Dictionary<string, byte[]>
        {
            ["long"] = Encoding.UTF8.GetBytes(Calls[name].Call + " reached = true return true"),
        }));
        var top = root.GetTop();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "local m = require('long')",
            "@long-call/require",
            executionOptions: CountOnly(100)));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.False(Reached(root));
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public void ACallInAMetamethodOfAHostReadStopsInside(string name)
    {
        // A host callback reads a table whose __index runs the call: a direct
        // host operation nested inside the callback.
        using var root = Prepare(name);
        using var probe = root.CreateFunction("probe", context =>
        {
            using var table = context.Read<LuauTable>(0);
            _ = table["key"];
        });
        root["probe"] = probe;
        var top = root.GetTop();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "probe(setmetatable({}, {__index = function() " + Calls[name].Call + " reached = true end}))",
            "@long-call/metamethod",
            executionOptions: CountOnly(100)));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.False(Reached(root));
        RequireUsable(root, top);
    }

    // The cancellation and disposal below come from another thread shortly
    // after the script reports that it is about to make the call, so that the
    // call has started. The sorts run for minutes, so the request always lands
    // inside them. The other calls take a hundred milliseconds or more, and a
    // runner that stalls the requesting thread for that long lets the call
    // finish first; such an attempt is repeated. A call that cannot be stopped
    // from inside finishes in every attempt.
    const int Attempts = 5;
    static readonly TimeSpan RequestDelay = TimeSpan.FromMilliseconds(20);

    [Theory]
    [MemberData(nameof(CallNames))]
    public async Task CancellationFromAnotherThreadStopsTheCallInside(string name)
    {
        using var root = Prepare(name);
        var top = root.GetTop();
        for (var attempt = 1; ; attempt++)
        {
            // An attempt that finished left its results behind.
            root.CollectGarbage();
            root.DoString("reached = nil").Dispose();
            using var cancellation = new CancellationTokenSource();
            using var started = new ManualResetEventSlim();
            using var signal = root.CreateFunction("started", _ => started.Set());
            root["started"] = signal;

            var execution = Task.Run(async () =>
            {
                using var _ = await root.DoStringAsync(
                    "started() " + Calls[name].Call + " reached = true",
                    "@long-call/cancel",
                    cancellationToken: cancellation.Token);
            });
            Assert.True(started.Wait(TestBudgets.WaitTimeout));
            Thread.Sleep(RequestDelay);
            var stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();

            await Assert.ThrowsAsync<LuauExecutionCanceledException>(() => execution.WaitAsync(TestBudgets.WaitTimeout));
            Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
            if (!Reached(root))
            {
                break;
            }
            Assert.True(attempt < Attempts, "The call finished before the cancellation stopped it in every attempt.");
        }
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public void DisposalOfAChildThreadFromAnotherThreadStopsTheCallInside(string name)
    {
        using var root = Prepare(name);
        var top = root.GetTop();
        using var started = new ManualResetEventSlim();
        using var signal = root.CreateFunction("started", _ => started.Set());
        root["started"] = signal;
        for (var attempt = 1; ; attempt++)
        {
            started.Reset();
            root.CollectGarbage();
            root.DoString("reached = nil").Dispose();
            var child = root.CreateThread();
            Exception? observed = null;
            var thread = new Thread(() =>
            {
                try
                {
                    child.DoString("started() " + Calls[name].Call + " reached = true", "@long-call/dispose-child").Dispose();
                }
                catch (Exception exception)
                {
                    observed = exception;
                }
            });
            thread.Start();
            Assert.True(started.Wait(TestBudgets.WaitTimeout));
            Thread.Sleep(RequestDelay);
            var stopwatch = Stopwatch.StartNew();

            // Dispose waits for the running operation to stop.
            child.Dispose();

            Assert.True(thread.Join(TestBudgets.WaitTimeout));
            Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
            Assert.IsType<ObjectDisposedException>(observed);
            if (!Reached(root))
            {
                break;
            }
            Assert.True(attempt < Attempts, "The call finished before the disposal stopped it in every attempt.");
        }
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CallNames))]
    public void DisposalOfTheRootFromAnotherThreadStopsTheCall(string name)
    {
        // Nothing of the root is left to show where the call stopped, so this
        // relies on the time bound, which the sorts would exceed many times
        // over; the child-thread test above shows the stop is inside.
        var root = Prepare(name);
        using var started = new ManualResetEventSlim();
        root["started"] = root.CreateFunction("started", _ => started.Set());
        Exception? observed = null;
        var thread = new Thread(() =>
        {
            try
            {
                root.DoString("started() " + Calls[name].Call + " reached = true", "@long-call/dispose").Dispose();
            }
            catch (Exception exception)
            {
                observed = exception;
            }
        });
        thread.Start();
        Assert.True(started.Wait(TestBudgets.WaitTimeout));
        Thread.Sleep(RequestDelay);
        var stopwatch = Stopwatch.StartNew();

        // Dispose waits for the running operation to stop.
        root.Dispose();

        Assert.True(thread.Join(TestBudgets.WaitTimeout));
        Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
        Assert.IsType<ObjectDisposedException>(observed);
        Assert.True(root.IsDisposed);
    }
}
