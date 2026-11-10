using System.Text;

namespace Luau.Tests;

/// <summary>
/// <c>InvokeVoid</c> and <c>InvokeVoidAsync</c> mean "call this and give me
/// nothing back". Values the function returns are discarded and the call
/// succeeds: no value reaches the host, so <c>MaxResultCount</c> does not
/// apply. The frame ceiling still does, and everything else about the call
/// (errors, budgets, cancellation, the rejection of a root yield, and an empty
/// stack afterwards) is as for any other call.
/// </summary>
public sealed class InvokeVoidDiscardedResultsTests
{
    // The function and script-entrypoint forms of every void overload.
    static readonly string[] OverloadNames =
    [
        "function()",
        "function(argument)",
        "function(span)",
        "function-async()",
        "function-async(argument)",
        "function-async(memory)",
        "entrypoint()",
        "entrypoint(argument)",
        "entrypoint(span)",
        "entrypoint-async()",
        "entrypoint-async(argument)",
        "entrypoint-async(memory)",
    ];

    public static TheoryData<string> Overloads => new(OverloadNames);

    // Options a host passes for hooks it expects to return nothing, as the
    // sample kit and the scheduler tests do.
    static readonly LuauExecutionOptions ZeroResultOptions = TestBudgets.Execution with
    {
        MaxResultCount = 0,
    };

    static readonly LuauValue[] SpanArguments = [3, 5, 8];

    static int ArgumentCountFor(string overload) =>
        overload.EndsWith("()", StringComparison.Ordinal) ? 0
        : overload.EndsWith("(argument)", StringComparison.Ordinal) ? 1
        : SpanArguments.Length;

    /// <summary>
    /// A sandboxed root with a <c>record</c> callback that counts calls and
    /// keeps the last call's argument count, a <c>target</c> host object, and
    /// whatever <paramref name="configure"/> registers before sandboxing.
    /// </summary>
    sealed class Fixture : IDisposable
    {
        readonly List<IDisposable> owned = [];

        public Fixture(Action<LuauState, List<IDisposable>>? configure = null)
        {
            Root = TestBudgets.CreateRoot();
            Root.OpenBaseLibrary();
            Root.OpenTableLibrary();
            Root.OpenCoroutineLibrary();
            Root.OpenBufferLibrary();
            var record = Root.CreateFunction("record", context =>
            {
                Calls++;
                LastArgumentCount = context.ArgumentCount;
            });
            owned.Add(record);
            Root["record"] = record;
            var target = Root.CreateHandle(new HostTarget(), HostTarget.Descriptor);
            owned.Add(target);
            Root["target"] = target;
            configure?.Invoke(Root, owned);
            Root.SandboxRoot();
        }

        public LuauState Root { get; }
        public int Calls { get; private set; }
        public int LastArgumentCount { get; private set; } = -1;

        /// <summary>
        /// Loads <c>function(...) record(...) &lt;body&gt; end</c> on the root
        /// as a function, and as the <c>hook</c> export of a script instance.
        /// </summary>
        public (LuauFunction Function, LuauScriptEntrypoint Entrypoint) Load(string body)
        {
            var source = "function(...) record(...) " + body + " end";
            using var functionOwner = Root.DoString("return " + source, "@void/function.luau");
            var function = functionOwner.Read<LuauFunction>(0).Retain();
            owned.Add(function);
            var instance = LuauScriptInstance.CreateAsync(
                    Root,
                    "void-hooks",
                    (thread, _) => ValueTask.FromResult(
                        thread.DoString("return { hook = " + source + " }", "@void/entrypoint.luau")))
                .AsTask()
                .GetAwaiter()
                .GetResult();
            owned.Add(instance);
            return (function, instance.GetRequiredEntrypoint("hook"));
        }

        public void AssertUsable()
        {
            using var results = Root.DoString("return 40 + 2", "@void/recovery.luau");
            Assert.Equal(42, Assert.Single(results).Read<int>());
        }

        public void Dispose()
        {
            for (var index = owned.Count - 1; index >= 0; index--)
            {
                owned[index].Dispose();
            }

            Root.Dispose();
        }
    }

    sealed class HostTarget
    {
        public static readonly LuauObjectDescriptor<HostTarget> Descriptor = new(
            "VoidHostTarget",
            validateTarget: null,
            [LuauObjectMember<HostTarget>.Property("value", (_, context) => context.Return(7), null)]);
    }

    static async Task InvokeVoid(
        string overload,
        LuauFunction function,
        LuauScriptEntrypoint entrypoint,
        LuauExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LuauValue argument = 2;
        switch (overload)
        {
            case "function()": function.InvokeVoid(options); break;
            case "function(argument)": function.InvokeVoid(argument, options); break;
            case "function(span)": function.InvokeVoid(SpanArguments.AsSpan(), options); break;
            case "function-async()": await function.InvokeVoidAsync(cancellationToken, options); break;
            case "function-async(argument)": await function.InvokeVoidAsync(argument, cancellationToken, options); break;
            case "function-async(memory)": await function.InvokeVoidAsync(SpanArguments.AsMemory(), cancellationToken, options); break;
            case "entrypoint()": entrypoint.InvokeVoid(options); break;
            case "entrypoint(argument)": entrypoint.InvokeVoid(argument, options); break;
            case "entrypoint(span)": entrypoint.InvokeVoid(SpanArguments.AsSpan(), options); break;
            case "entrypoint-async()": await entrypoint.InvokeVoidAsync(cancellationToken, options); break;
            case "entrypoint-async(argument)": await entrypoint.InvokeVoidAsync(argument, cancellationToken, options); break;
            case "entrypoint-async(memory)": await entrypoint.InvokeVoidAsync(SpanArguments.AsMemory(), cancellationToken, options); break;
            default: throw new ArgumentOutOfRangeException(nameof(overload), overload, null);
        }
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public async Task EveryOverloadDiscardsAReturnedValueAndSucceeds(string overload)
    {
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load("return true");
        var top = fixture.Root.GetTop();
        var disposables = fixture.Root.RegisteredDisposableCount;

        for (var call = 1; call <= 3; call++)
        {
            await InvokeVoid(overload, function, entrypoint);

            Assert.Equal(call, fixture.Calls);
            Assert.Equal(ArgumentCountFor(overload), fixture.LastArgumentCount);
            Assert.Equal(top, fixture.Root.GetTop());
        }

        Assert.Equal(disposables, fixture.Root.RegisteredDisposableCount);
        fixture.AssertUsable();
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public async Task EveryOverloadDiscardsAValueUnderAZeroResultLimit(string overload)
    {
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load("return 1");
        var top = fixture.Root.GetTop();

        await InvokeVoid(overload, function, entrypoint, ZeroResultOptions);

        Assert.Equal(1, fixture.Calls);
        Assert.Equal(top, fixture.Root.GetTop());
        fixture.AssertUsable();
    }

    // Each kind of value a function can return, one at a time and together.
    public static TheoryData<string> ReturnedValues => new()
    {
        "return 1.5",
        "return 'text'",
        "return { 1, 2, answer = 3 }",
        "return function() return 1 end",
        "return target",
        "return buffer.create(16)",
        "return coroutine.create(function() end)",
        "return nil",
        "return 1, 'two', { 3 }, print, target, nil, false",
        // A tail call: the hook's last statement returns what another returns.
        "local function step(n) return n * 2, 'stepped' end return step(21)",
    };

    [Theory]
    [MemberData(nameof(ReturnedValues))]
    public async Task ValuesOfEveryKindAreDiscarded(string body)
    {
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load(body);
        var top = fixture.Root.GetTop();
        var disposables = fixture.Root.RegisteredDisposableCount;

        function.InvokeVoid();
        function.InvokeVoid(ZeroResultOptions);
        await function.InvokeVoidAsync();
        entrypoint.InvokeVoid(ZeroResultOptions);
        await entrypoint.InvokeVoidAsync(executionOptions: ZeroResultOptions);

        Assert.Equal(5, fixture.Calls);
        Assert.Equal(top, fixture.Root.GetTop());
        // Discarded tables, functions, and host objects are never wrapped.
        Assert.Equal(disposables, fixture.Root.RegisteredDisposableCount);
        fixture.AssertUsable();
    }

    static string ReturnMany(int count) =>
        // Mixed kinds, so reference values are among the discarded ones.
        "local t = table.create(" + count + ", 0) " +
        "for i = 1, " + count + " do t[i] = if i % 3 == 0 then {} elseif i % 3 == 1 then i else 'v' end " +
        "return table.unpack(t)";

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1_000)]
    public async Task AnyNumberOfValuesUnderTheDefaultLimitIsDiscarded(int count)
    {
        using var fixture = new Fixture();
        Assert.Equal(64, fixture.Root.Options.DefaultExecutionOptions.MaxResultCount);
        var (function, entrypoint) = fixture.Load(ReturnMany(count));
        var top = fixture.Root.GetTop();

        foreach (var overload in OverloadNames)
        {
            await InvokeVoid(overload, function, entrypoint);
            Assert.Equal(top, fixture.Root.GetTop());
        }

        Assert.Equal(12, fixture.Calls);
        fixture.AssertUsable();

        // The limit still applies to a call that delivers the same results.
        if (count <= 64)
        {
            using var delivered = function.Invoke();
            Assert.Equal(count, delivered.Count);
        }
        else
        {
            var exception = Assert.Throws<LuauResultLimitException>(() => function.Invoke());
            Assert.Equal(count, exception.ActualCount);
            Assert.Equal(64, exception.Limit);
        }

        Assert.Equal(top, fixture.Root.GetTop());
    }

    [Fact]
    public void ManyCallsInARowLeaveNothingOnTheStack()
    {
        // An earlier defect left one value per rejected call on the stack
        // until the frame filled; check the depth after every call, not only
        // that the calls succeed.
        const int Calls = 40_000;
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load("return 1, 'two', {}, target");
        var top = fixture.Root.GetTop();
        var disposables = fixture.Root.RegisteredDisposableCount;
        LuauValue argument = 0.5;

        for (var call = 0; call < Calls; call++)
        {
            switch (call % 4)
            {
                case 0: function.InvokeVoid(); break;
                case 1: function.InvokeVoid(argument, ZeroResultOptions); break;
                case 2: entrypoint.InvokeVoid(SpanArguments.AsSpan()); break;
                default: entrypoint.InvokeVoid(argument, ZeroResultOptions); break;
            }

            if (fixture.Root.GetTop() != top)
            {
                Assert.Fail($"Call {call} left the root stack at {fixture.Root.GetTop()} instead of {top}.");
            }
        }

        Assert.Equal(Calls, fixture.Calls);
        Assert.Equal(disposables, fixture.Root.RegisteredDisposableCount);
        fixture.AssertUsable();
    }

    [Fact]
    public async Task ManyAsynchronousCallsInARowLeaveNothingOnTheStack()
    {
        const int Calls = 4_000;
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load("return 1, 'two', {}, target");
        var top = fixture.Root.GetTop();

        for (var call = 0; call < Calls; call++)
        {
            if (call % 2 == 0)
            {
                await function.InvokeVoidAsync();
            }
            else
            {
                await entrypoint.InvokeVoidAsync(executionOptions: ZeroResultOptions);
            }

            if (fixture.Root.GetTop() != top)
            {
                Assert.Fail($"Call {call} left the root stack at {fixture.Root.GetTop()} instead of {top}.");
            }
        }

        Assert.Equal(Calls, fixture.Calls);
        fixture.AssertUsable();
    }

    // 8003 values: more than one frame holds (StackSlotLimitTests).
    const string OverfullReturn = "local t = table.create(7999, 1) return 1, 2, 3, 4, table.unpack(t)";

    public static TheoryData<string, bool> OverloadsWithZeroLimit()
    {
        var data = new TheoryData<string, bool>();
        foreach (var overload in OverloadNames)
        {
            data.Add(overload, false);
            data.Add(overload, true);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(OverloadsWithZeroLimit))]
    public async Task MoreValuesThanAFrameHoldsStillFailAndLeaveTheRootUsable(string overload, bool zeroLimit)
    {
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load(OverfullReturn);
        var options = zeroLimit ? ZeroResultOptions : null;
        var top = fixture.Root.GetTop();

        // Enough attempts to fill the frame if a failure leaked one value.
        for (var attempt = 0; attempt < 64; attempt++)
        {
            var exception = await Assert.ThrowsAsync<LuauStackLimitException>(
                () => InvokeVoid(overload, function, entrypoint, options));
            Assert.Contains("8000", exception.Message, StringComparison.Ordinal);
            Assert.Equal(top, fixture.Root.GetTop());
        }

        Assert.Equal(64, fixture.Calls);
        fixture.AssertUsable();
    }

    [Fact]
    public void ResultsThatLeaveOneFreeSlotAreDiscarded()
    {
        using var fixture = new Fixture();
        var (function, _) = fixture.Load("return table.unpack(table.create(7998, 7))");
        var top = fixture.Root.GetTop();

        function.InvokeVoid();
        function.InvokeVoid(ZeroResultOptions);

        Assert.Equal(top, fixture.Root.GetTop());
        fixture.AssertUsable();
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public async Task ARaisedErrorStillFails(string overload)
    {
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load("error('hook failed') return 1");
        var top = fixture.Root.GetTop();

        var exception = await Assert.ThrowsAsync<LuauException>(
            () => InvokeVoid(overload, function, entrypoint, ZeroResultOptions));

        Assert.Contains("hook failed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(top, fixture.Root.GetTop());
        fixture.AssertUsable();
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public async Task AnExceededBudgetStillFails(string overload)
    {
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load("for i = 1, 1000 do end return 1, 2");
        var tight = ZeroResultOptions with { InterruptCountLimit = 100 };
        var top = fixture.Root.GetTop();

        var exception = await Assert.ThrowsAsync<LuauExecutionBudgetException>(
            () => InvokeVoid(overload, function, entrypoint, tight));

        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
        Assert.Equal(top, fixture.Root.GetTop());
        fixture.AssertUsable();
        // The same function fits a larger budget and its values are discarded.
        await InvokeVoid(overload, function, entrypoint, ZeroResultOptions);
        Assert.Equal(top, fixture.Root.GetTop());
    }

    [Theory]
    [MemberData(nameof(Overloads))]
    public async Task AYieldOnTheRootIsStillRejected(string overload)
    {
        using var fixture = new Fixture();
        var (function, entrypoint) = fixture.Load("coroutine.yield(7, 8) return 1");
        var top = fixture.Root.GetTop();

        var exception = await Assert.ThrowsAsync<LuauException>(
            () => InvokeVoid(overload, function, entrypoint));

        Assert.Contains("yield", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(top, fixture.Root.GetTop());
        fixture.AssertUsable();
    }

    [Theory]
    [InlineData("function-async()")]
    [InlineData("function-async(argument)")]
    [InlineData("function-async(memory)")]
    [InlineData("entrypoint-async()")]
    [InlineData("entrypoint-async(argument)")]
    [InlineData("entrypoint-async(memory)")]
    public async Task CancellationStillStopsTheCall(string overload)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new Fixture((root, owned) =>
        {
            var cancel = root.CreateFunction("cancel", _ => cancellation.Cancel());
            owned.Add(cancel);
            root["cancel"] = cancel;
        });
        var (function, entrypoint) = fixture.Load("cancel() while true do end return 1");
        var top = fixture.Root.GetTop();

        await Assert.ThrowsAsync<LuauExecutionCanceledException>(
            () => InvokeVoid(overload, function, entrypoint, ZeroResultOptions, cancellation.Token));

        Assert.Equal(top, fixture.Root.GetTop());
        await Assert.ThrowsAsync<LuauExecutionCanceledException>(
            () => InvokeVoid(overload, function, entrypoint, ZeroResultOptions, cancellation.Token));
        fixture.AssertUsable();
    }

    [Fact]
    public async Task AnAsyncCallbackBeforeTheReturnIsAwaitedAndItsValuesDiscarded()
    {
        using var fixture = new Fixture((root, owned) =>
        {
            var later = root.CreateAsyncFunction("later", async context =>
            {
                await Task.Yield();
                context.Return(context.Read<int>(0) * 2);
            });
            owned.Add(later);
            root["later"] = later;
        });
        var (function, entrypoint) = fixture.Load("return later(21), 'done'");
        var top = fixture.Root.GetTop();

        await function.InvokeVoidAsync();
        await entrypoint.InvokeVoidAsync(executionOptions: ZeroResultOptions);

        Assert.Equal(2, fixture.Calls);
        Assert.Equal(top, fixture.Root.GetTop());
        fixture.AssertUsable();
    }

    [Fact]
    public void AModuleRequiredDuringAVoidCallStillDeliversItsResult()
    {
        // The module's result reaches the host's module cache, so the call's
        // result limit applies to it; the state default allows it.
        using var fixture = new Fixture((root, _) =>
            root.OpenRequireLibrary(new LuauModuleMap(new Dictionary<string, byte[]>
            {
                ["shared"] = Encoding.UTF8.GetBytes("return { answer = 42 }"),
                ["other"] = Encoding.UTF8.GetBytes("return { answer = 43 }"),
            })));
        var (function, _) = fixture.Load("return require('shared').answer");
        var (zeroLimited, _) = fixture.Load("return require('other').answer");
        var top = fixture.Root.GetTop();

        function.InvokeVoid();
        function.InvokeVoid(ZeroResultOptions);

        // Under a zero limit, the module's own result is over it.
        var exception = Assert.Throws<LuauManagedCallbackException>(() => zeroLimited.InvokeVoid(ZeroResultOptions));
        Assert.IsType<LuauResultLimitException>(exception.InnerException);
        Assert.Equal(top, fixture.Root.GetTop());
        zeroLimited.InvokeVoid();
        Assert.Equal(top, fixture.Root.GetTop());
        fixture.AssertUsable();
    }

    [Fact]
    public void TheOldStrictnessIsStillAvailableThroughCallsThatDeliverResults()
    {
        using var fixture = new Fixture();
        var (function, _) = fixture.Load("return 1, 2");
        var top = fixture.Root.GetTop();

        Assert.Throws<ArgumentException>(() => function.InvokeInto(Span<LuauValue>.Empty));
        var exception = Assert.Throws<LuauResultLimitException>(
            () => function.Invoke(executionOptions: ZeroResultOptions));
        Assert.Equal(2, exception.ActualCount);
        Assert.Equal(0, exception.Limit);

        Assert.Equal(top, fixture.Root.GetTop());
        fixture.AssertUsable();
    }
}
