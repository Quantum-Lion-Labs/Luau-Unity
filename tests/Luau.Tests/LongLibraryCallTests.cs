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
/// Each case's inputs fit the default 64 MB memory limit. The statement after
/// the call sets <c>reached</c>, and no safepoint comes between the call's
/// return and that statement, so <c>reached</c> stays nil only when a stop ends
/// the call from inside, or before it began. The interrupt-count tests rule
/// out the second: the count limit is far above the safepoints the script
/// reaches before the call, so a stop there can only come from safepoints
/// inside it. That, not the elapsed time, is what each test checks: a call
/// that ran to completion fails it however fast the runner is. The time
/// bounds only catch a hang.
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
    const string SplitSubject = "hay = string.rep('ab', 12 * 1048576) needle = string.rep('ab', 5000) .. 'ba'";
    // Two 12 MB strings that differ only in their last byte, 20,000 times:
    // each comparison reads 12 MB.
    const string SortSubject =
        "local a = string.rep('x', 12 * 1048576) .. 'a' local b = string.rep('x', 12 * 1048576) .. 'b' " +
        "t = table.create(20000) for i = 1, 20000 do t[i] = i % 2 == 0 and a or b end";
    // Each number becomes an interned string: about 120 ms.
    const string ConcatSubject = "t = table.create(700000) for i = 1, 700000 do t[i] = i + 0.5 end";
    const string Utf8Subject = "s = string.rep('\\u{20AC}', 20 * 1048576)";
    // A 50 MB subject in which the needle's first byte comes only every
    // 128 KB, so a search spends its time in memchr between candidates.
    const string RareCandidates = "hay = string.rep(string.rep('x', 131071) .. 'a', 400)";
    // A 50 MB pattern without special characters, which string.find must
    // classify before it finds the pattern longer than the subject.
    const string LongPattern = "hay = string.rep('x', 5000) short = string.rep('x', 100) needle = string.rep('y', 50 * 1048576)";

    // Calls that take a hundred milliseconds or more even with the host's
    // linear search (the sorts run for minutes), long enough for a stop from
    // another thread or a wall-clock limit to land inside them.
    static readonly Dictionary<string, LongCall> Calls = new()
    {
        ["find plain"] = new(PeriodicSubject, "local r = string.find(hay, needle, 1, true)"),
        ["find without specials"] = new(PeriodicSubject, "local r = string.find(hay, needle)"),
        ["find method"] = new(PeriodicSubject, "local r = hay:find(needle, 1, true)"),
        ["find saved reference"] = new(PeriodicSubject + " savedfind = string.find", "local r = savedfind(hay, needle, 1, true)"),
        ["split"] = new(SplitSubject, "local r = hay:split(needle)"),
        ["sort"] = new(SortSubject, "table.sort(t)"),
        ["sort with a C comparison"] = new(
            "t = table.create(1000000) for i = 1, 1000000 do t[i] = i end",
            "table.sort(t, rawequal)"),
        ["concat"] = new(ConcatSubject, "local r = table.concat(t, ',')"),
        ["utf8.len"] = new(Utf8Subject, "local r = utf8.len(s)"),
    };

    // Every replaced function through every route a script has to it: the
    // library table, a method call (string functions through the string
    // metatable, table functions through a metatable whose __index is the
    // table library; utf8 has no method route), and a reference saved before
    // the call. The deterministic interrupt-count tests run all of them, also
    // on a child thread, inside pcall, in a required module, and in a
    // metamethod a host read runs. Some of these take only milliseconds,
    // though they reach hundreds of safepoints, so the tests that need a long
    // call use Calls alone.
    static readonly Dictionary<string, LongCall> CountedCalls = new(Calls)
    {
        ["find with rare candidates"] = new(RareCandidates, "local r = string.find(hay, 'ab', 1, true)"),
        ["find with rare candidates, method"] = new(RareCandidates, "local r = hay:find('ab', 1, true)"),
        ["find classifying a long pattern"] = new(LongPattern, "local r = string.find(hay, needle)"),
        ["find classifying a long pattern on a short subject"] = new(LongPattern, "local r = short:find(needle)"),
        ["split direct"] = new(SplitSubject, "local r = string.split(hay, needle)"),
        ["split saved reference"] = new(SplitSubject + " savedsplit = string.split", "local r = savedsplit(hay, needle)"),
        ["split with rare candidates"] = new(RareCandidates, "local r = string.split(hay, 'ab')"),
        ["sort method"] = new(SortSubject + " setmetatable(t, {__index = table})", "t:sort()"),
        ["sort saved reference"] = new(SortSubject + " savedsort = table.sort", "savedsort(t)"),
        ["concat method"] = new(ConcatSubject + " setmetatable(t, {__index = table})", "local r = t:concat(',')"),
        ["concat saved reference"] = new(ConcatSubject + " savedconcat = table.concat", "local r = savedconcat(t, ',')"),
        ["utf8.len saved reference"] = new(Utf8Subject + " savedlen = utf8.len", "local r = savedlen(s)"),
    };

    public static TheoryData<string> CallNames() => NamesOf(Calls);

    public static TheoryData<string> CountedCallNames() => NamesOf(CountedCalls);

    static TheoryData<string> NamesOf(Dictionary<string, LongCall> calls)
    {
        var names = new TheoryData<string>();
        foreach (var name in calls.Keys)
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
        root.DoString(CountedCalls[name].Setup, "@long-call/setup").Dispose();
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
        root.DoString(SortSubject, executionOptions: TestBudgets.Execution).Dispose();
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
    [MemberData(nameof(CountedCallNames))]
    public void InterruptCountLimitStopsTheCallInside(string name)
    {
        // The safepoints the call reaches count like pattern-matching steps:
        // the limit stops it at exactly limit + 1. Before the call returns,
        // the script reaches one safepoint of its own, the call instruction.
        using var root = Prepare(name);
        var top = root.GetTop();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            CountedCalls[name].Call + " reached = true",
            "@long-call/count",
            executionOptions: CountOnly(100)));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal(100, exception.InterruptCountLimit);
        Assert.Equal(101, exception.ObservedInterruptCount);
        Assert.False(Reached(root));
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CountedCallNames))]
    public void ACallOnAChildThreadStopsInside(string name)
    {
        using var root = Prepare(name);
        var child = root.CreateThread();
        var rootTop = root.GetTop();
        var childTop = child.GetTop();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => child.DoString(
            CountedCalls[name].Call + " reached = true",
            "@long-call/child",
            executionOptions: CountOnly(100)));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal(101, exception.ObservedInterruptCount);
        Assert.Equal(childTop, child.GetTop());
        Assert.False(Reached(root));
        using var result = child.DoString("return 3");
        Assert.Equal(3, Assert.Single(result).Read<int>());
        RequireUsable(root, rootTop);
    }

    [Theory]
    [MemberData(nameof(CountedCallNames))]
    public void ACallInsidePcallStopsAndTheStopIsRaisedAgain(string name)
    {
        // pcall catches the hard stop, which comes back at the next safepoint.
        // No safepoint comes between pcall's return and the assignment, so
        // completed is false only if the stop ended the call.
        using var root = Prepare(name);
        var top = root.GetTop();

        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "local ok = pcall(function() " + CountedCalls[name].Call + " end) completed = ok",
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
    [MemberData(nameof(CountedCallNames))]
    public void ACallInARequiredModuleStopsInside(string name)
    {
        // require runs the module as a nested protected call inside its host
        // callback, under the requiring operation's budgets.
        using var root = Prepare(name);
        root.OpenRequireLibrary(new LuauModuleMap(new Dictionary<string, byte[]>
        {
            ["long"] = Encoding.UTF8.GetBytes(CountedCalls[name].Call + " reached = true return true"),
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
    [MemberData(nameof(CountedCallNames))]
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
            "probe(setmetatable({}, {__index = function() " + CountedCalls[name].Call + " reached = true end}))",
            "@long-call/metamethod",
            executionOptions: CountOnly(100)));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.False(Reached(root));
        RequireUsable(root, top);
    }

    [Fact]
    public void FindChecksInitBeforeItClassifiesTheLongPattern()
    {
        // With init past the end of the subject, string.find returns nil
        // before it looks at the pattern, as upstream's does: the calls reach
        // no safepoint inside, so a limit of ten lets the script finish.
        // Without init it classifies the 50 MB pattern once, reaching 3,200
        // safepoints, one per 16 KB, and then finds it longer than the subject.
        using var root = Prepare("find classifying a long pattern");

        using (var result = root.DoString(
            "return select('#', string.find(hay, needle, 5002)), string.find(hay, needle, 5002), short:find(needle, 102)",
            "@long-call/find-init",
            executionOptions: CountOnly(10)))
        {
            Assert.Equal(1, result[0].Read<int>());
            Assert.Equal(LuauType.Nil, result[1].Type);
            Assert.Equal(LuauType.Nil, result[2].Type);
        }
        using (var result = root.DoString("return string.find(hay, needle), short:find(needle)", "@long-call/find-long"))
        {
            Assert.Equal(LuauType.Nil, result[0].Type);
            Assert.Equal(LuauType.Nil, result[1].Type);
        }
        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            "local r = string.find(hay, needle)", "@long-call/find-classify", executionOptions: CountOnly(3000)));
        Assert.Equal(3001, exception.ObservedInterruptCount);
        using (var result = root.DoString(
            "local r = string.find(hay, needle) return 1", "@long-call/find-classify-once", executionOptions: CountOnly(3400)))
        {
            Assert.Equal(1, Assert.Single(result).Read<int>());
        }
    }

    public static TheoryData<string> ReplacedLibraries() => new() { "string", "table", "utf8" };

    static void Open(LuauState root, string library)
    {
        switch (library)
        {
            case "string": root.OpenStringLibrary(); break;
            case "table": root.OpenTableLibrary(); break;
            default: root.OpenUtf8Library(); break;
        }
    }

    static readonly Dictionary<string, string[]> ReplacedFunctions = new()
    {
        ["string"] = new[] { "find", "split" },
        ["table"] = new[] { "concat", "sort" },
        ["utf8"] = new[] { "len" },
    };

    static string Removal(string library)
    {
        var builder = new StringBuilder();
        foreach (var function in ReplacedFunctions[library])
        {
            builder.Append(library).Append('.').Append(function).Append(" = nil ");
        }
        return builder.ToString();
    }

    // Calls that each replaced function makes reach the count limit inside
    // the host's version and finish inside upstream's.
    static readonly Dictionary<string, string> StoppingCalls = new()
    {
        ["find"] = "local r = string.find(hay, 'z', 1, true)",
        ["split"] = "local r = string.split(hay, 'zz')",
        ["concat"] = "local r = table.concat(t, ',')",
        ["sort"] = "table.sort(t, rawequal)",
        ["len"] = "local r = utf8.len(hay)",
    };

    static void RequireHostVersions(LuauState root, string library)
    {
        root.DoString(
            "hay = string.rep('x', 20 * 1048576) t = table.create(300000) for i = 1, 300000 do t[i] = i end",
            "@long-call/probe-setup").Dispose();
        foreach (var function in ReplacedFunctions[library])
        {
            var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
                StoppingCalls[function] + " reached = true", "@long-call/probe", executionOptions: CountOnly(50)));
            Assert.Equal(51, exception.ObservedInterruptCount);
            Assert.False(Reached(root));
        }
    }

    [Theory]
    [MemberData(nameof(ReplacedLibraries))]
    public void ReopeningALibraryWhoseTableHasANewIndexMetamethodIsRefused(string library)
    {
        // The metamethod would be handed each function registration stores,
        // upstream's included, before the host replaced it.
        using var root = CreateRoot();
        root.DoString(
            "captured = {} " + Removal(library) +
            "kept = {__newindex = function(t, k, v) captured[#captured + 1] = k rawset(t, k, v) end} " +
            "setmetatable(" + library + ", kept)",
            "@reopen/setup").Dispose();
        var top = root.GetTop();

        var exception = Assert.Throws<LuauException>(() => Open(root, library));

        Assert.Contains(
            $"cannot open the {library} library while the library table has a __newindex metamethod",
            exception.Message);
        // Nothing was registered or captured, and the root runs code.
        using (var unchanged = root.DoString(
            "return #captured, getmetatable(" + library + ") == kept, rawget(" + library + ", '" +
            ReplacedFunctions[library][0] + "') == nil",
            "@reopen/check"))
        {
            Assert.Equal(0, unchanged[0].Read<int>());
            Assert.True(unchanged[1].Read<bool>());
            Assert.True(unchanged[2].Read<bool>());
        }
        RequireUsable(root, top);

        // Without the metamethod, the library opens again with the host's
        // versions.
        root.DoString("setmetatable(" + library + ", {__index = {}})", "@reopen/metatable").Dispose();
        Open(root, library);
        using (var captured = root.DoString("return #captured"))
        {
            Assert.Equal(0, captured[0].Read<int>());
        }
        RequireHostVersions(root, library);
    }

    [Theory]
    [MemberData(nameof(ReplacedLibraries))]
    public void OpeningALibraryUnderAGlobalsNewIndexMetamethodIsRefused(string library)
    {
        // The metamethod would see the new library table stored in the
        // globals table, and could give it a __newindex of its own.
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.DoString(
            """
            captured = {}
            setmetatable(_G, {__newindex = function(g, k, v)
                captured[#captured + 1] = k
                if type(v) == "table" then
                    setmetatable(v, {__newindex = function(t, name, f) captured[#captured + 1] = name rawset(t, name, f) end})
                end
                rawset(g, k, v)
            end})
            """,
            "@open/setup").Dispose();
        var top = root.GetTop();

        var exception = Assert.Throws<LuauException>(() => Open(root, library));

        Assert.Contains(
            $"cannot open the {library} library while the globals table has a __newindex metamethod",
            exception.Message);
        using (var unchanged = root.DoString("return #captured, rawget(_G, '" + library + "') == nil", "@open/check"))
        {
            Assert.Equal(0, unchanged[0].Read<int>());
            Assert.True(unchanged[1].Read<bool>());
        }
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(ReplacedLibraries))]
    public void ReopeningALibraryInstallsTheHostVersions(string library)
    {
        // Script code removed the functions and gave the table a metatable
        // without __newindex, which does not stop the open.
        using var root = CreateRoot();
        root.DoString(Removal(library) + "setmetatable(" + library + ", {__index = function() return nil end})", "@reopen/setup").Dispose();

        Open(root, library);

        RequireHostVersions(root, library);
    }

    // The cancellation and disposal below come from another thread once the
    // script reports that it is about to make the call. The call is made
    // through a metamethod (a library function stored as __index or
    // __newindex), which the VM calls without a safepoint, and no safepoint
    // comes between the report and that call either. So the next safepoint
    // the script reaches after the report is inside the call, or after it
    // has returned, and a stop can land nowhere in between: a stop that
    // leaves reached nil stopped the call from inside. The direct, method,
    // and saved-reference routes are the interrupt-count tests' above. An
    // attempt whose call finished before the stop came, which a runner that
    // stalls the requesting thread can cause, is repeated; a call that cannot
    // be stopped from inside finishes in every attempt.
    const int Attempts = 5;

    static readonly Dictionary<string, LongCall> CrossThreadCalls = new()
    {
        ["find"] = new(PeriodicSubject + " getmetatable('').__index = string.find", "local r = hay[needle]"),
        ["split"] = new(SplitSubject + " getmetatable('').__index = string.split", "local r = hay[needle]"),
        ["sort"] = new(SortSubject + " setmetatable(t, {__index = table.sort})", "local r = t[nil]"),
        ["sort with a C comparison"] = new(
            "t = table.create(1000000) for i = 1, 1000000 do t[i] = i end setmetatable(t, {__newindex = table.sort})",
            "t[rawequal] = 1"),
        ["concat"] = new(ConcatSubject + " setmetatable(t, {__newindex = table.concat})", "t[','] = 1"),
        ["utf8.len"] = new(Utf8Subject + " getmetatable('').__index = utf8.len", "local r = s[1]"),
    };

    public static TheoryData<string> CrossThreadCallNames() => NamesOf(CrossThreadCalls);

    static LuauState PrepareCrossThread(string name)
    {
        var root = CreateRoot();
        root.DoString(CrossThreadCalls[name].Setup, "@long-call/setup").Dispose();
        return root;
    }

    [Theory]
    [MemberData(nameof(CrossThreadCallNames))]
    public async Task CancellationFromAnotherThreadStopsTheCallInside(string name)
    {
        using var root = PrepareCrossThread(name);
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
                    "started() " + CrossThreadCalls[name].Call + " reached = true",
                    "@long-call/cancel",
                    cancellationToken: cancellation.Token);
            });
            Assert.True(started.Wait(TestBudgets.WaitTimeout));
            var stopwatch = Stopwatch.StartNew();
            cancellation.Cancel();

            var canceled = false;
            try
            {
                await execution.WaitAsync(TestBudgets.WaitTimeout);
            }
            catch (LuauExecutionCanceledException)
            {
                canceled = true;
            }
            Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
            var reached = Reached(root);
            // Without a cancellation, the operation finished before it came.
            Assert.True(canceled || reached);
            if (!reached)
            {
                break;
            }
            Assert.True(attempt < Attempts, "The call finished before the cancellation stopped it in every attempt.");
        }
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CrossThreadCallNames))]
    public void DisposalOfAChildThreadFromAnotherThreadStopsTheCallInside(string name)
    {
        using var root = PrepareCrossThread(name);
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
                    child.DoString("started() " + CrossThreadCalls[name].Call + " reached = true", "@long-call/dispose-child").Dispose();
                }
                catch (Exception exception)
                {
                    observed = exception;
                }
            });
            thread.Start();
            Assert.True(started.Wait(TestBudgets.WaitTimeout));
            var stopwatch = Stopwatch.StartNew();

            // Dispose waits for the running operation to stop.
            child.Dispose();

            Assert.True(thread.Join(TestBudgets.WaitTimeout));
            Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
            var reached = Reached(root);
            // Without an exception, the operation finished before the disposal.
            if (observed != null)
            {
                Assert.IsType<ObjectDisposedException>(observed);
            }
            Assert.True(observed != null || reached);
            if (!reached)
            {
                break;
            }
            Assert.True(attempt < Attempts, "The call finished before the disposal stopped it in every attempt.");
        }
        RequireUsable(root, top);
    }

    [Theory]
    [MemberData(nameof(CrossThreadCallNames))]
    public void DisposalOfTheRootFromAnotherThreadStopsTheCallInside(string name)
    {
        // Nothing of the root is left to look at once it is disposed, so the
        // script reports a call that finished to C# while the operation still
        // runs: completed is a store to a table whose __newindex is a C#
        // function, which the VM calls without a safepoint, so the report is
        // made whenever the call returns before the stop.
        for (var attempt = 1; ; attempt++)
        {
            var root = PrepareCrossThread(name);
            using var started = new ManualResetEventSlim();
            root["started"] = root.CreateFunction("started", _ => started.Set());
            var completed = false;
            root["record"] = root.CreateFunction("record", _ => completed = true);
            root.DoString("probe = setmetatable({}, {__newindex = record})").Dispose();
            Exception? observed = null;
            var thread = new Thread(() =>
            {
                try
                {
                    root.DoString("started() " + CrossThreadCalls[name].Call + " probe.completed = true", "@long-call/dispose").Dispose();
                }
                catch (Exception exception)
                {
                    observed = exception;
                }
            });
            thread.Start();
            Assert.True(started.Wait(TestBudgets.WaitTimeout));
            var stopwatch = Stopwatch.StartNew();

            // Dispose waits for the running operation to stop.
            root.Dispose();

            Assert.True(thread.Join(TestBudgets.WaitTimeout));
            Assert.True(stopwatch.Elapsed < StopBound, $"Stopping took {stopwatch.Elapsed}.");
            Assert.True(root.IsDisposed);
            // Without an exception, the operation finished before the disposal.
            if (observed != null)
            {
                Assert.IsType<ObjectDisposedException>(observed);
            }
            Assert.True(observed != null || completed);
            if (!completed)
            {
                break;
            }
            Assert.True(attempt < Attempts, "The call finished before the disposal stopped it in every attempt.");
        }
    }
}
