namespace Luau.Tests;

public sealed class HardeningConfigurationTests
{
    [Fact]
    public void ParameterlessCreateRunsScriptsUnderTheFiniteDefaults()
    {
        // Other tests replace the default wall-clock limit with a safety net
        // (see TestBudgets). This one keeps the shipped profile and checks it
        // end to end: a root from the parameterless overload stops a runaway
        // script. Which default budget stops it first depends on the machine,
        // so the test does not say; either way it cannot outlast the budget.
        using var state = LuauState.Create();

        Assert.Equal(LuauStateOptions.Default, state.Options);
        Assert.Throws<LuauExecutionBudgetException>(() => state.DoString("while true do end"));
    }

    // One-time work a script commonly does in setup. Each needs more than
    // 100,000 interrupt safepoints, the previous default count limit, which
    // stopped all of them. The second element is what the script returns.
    public static TheoryData<string, double> OrdinaryOneTimeWork => new()
    {
        {
            // 150,000 back-edges plus the return.
            "local n = 0 for i = 1, 150000 do n = n + i end return n",
            150_000d * 150_001 / 2
        },
        {
            // 160,400 back-edges plus the return.
            """
            local grid = {}
            for y = 1, 400 do
                local row = {}
                for x = 1, 400 do
                    row[x] = x * y
                end
                grid[y] = row
            end
            return grid[400][400]
            """,
            160_000d
        },
        {
            // About 206,000 safepoints, 188,000 of them in the sort: each
            // comparison is a call that makes two more calls.
            """
            local function distance(entity)
                local dx, dy = entity.x - 1000, entity.y - 1000
                return dx * dx + dy * dy
            end
            local entities = {}
            for i = 1, 3000 do
                entities[i] = { x = (i * 7919) % 4001, y = (i * 104729) % 3989 }
            end
            table.sort(entities, function(a, b) return distance(a) < distance(b) end)
            local ordered = 0
            for i = 2, #entities do
                if distance(entities[i - 1]) <= distance(entities[i]) then
                    ordered += 1
                end
            end
            return ordered
            """,
            2_999d
        },
    };

    [Theory]
    [MemberData(nameof(OrdinaryOneTimeWork))]
    public void DefaultBudgetsLeaveRoomForOrdinaryOneTimeWork(string source, double expected)
    {
        // Every default except the wall clock, which becomes a safety net
        // (see TestBudgets): the count limit is the one under test, and a busy
        // runner can take longer than 250 ms for work that takes a few
        // milliseconds on an idle one.
        using var root = TestBudgets.CreateRoot();
        root.OpenBaseLibrary();
        root.OpenTableLibrary();

        using (var results = root.DoString(source))
        {
            Assert.Equal(expected, Assert.Single(results).Read<double>());
        }

        // The work does not fit the old default, so this test fails with it.
        var exception = Assert.Throws<LuauExecutionBudgetException>(() => root.DoString(
            source,
            executionOptions: LuauExecutionOptions.Unbounded with { InterruptCountLimit = 100_000 }));
        Assert.Equal(LuauExecutionBudgetKind.InterruptCount, exception.BudgetKind);
    }

    [Fact]
    public void NewOptionsAndNamedDefaultsUseTheSameFinitePolicy()
    {
        var options = new LuauStateOptions();
        var execution = new LuauExecutionOptions();

        Assert.Equal(64L * 1024 * 1024, options.MemoryLimitBytes);
        Assert.Equal(1024 * 1024, options.MaxSourceBytes);
        Assert.Equal(4 * 1024 * 1024, options.MaxBytecodeBytes);
        Assert.Equal(1024, options.MaxManagedHandleCount);
        Assert.Equal(16L * 1024 * 1024, options.MaxCachedModuleBytes);
        Assert.Equal(TimeSpan.FromMilliseconds(250), execution.WallClockLimit);
        Assert.Equal(10_000_000, execution.InterruptCountLimit);
        Assert.Equal(64, execution.MaxResultCount);
        Assert.Equal(execution, options.DefaultExecutionOptions);
        Assert.Equal(LuauBytecodePolicy.Reject, options.BytecodePolicy);
        Assert.Null(options.BytecodeValidator);
        Assert.Null(execution.ContinuationScheduler);
        Assert.True(execution.HasBudget);
        Assert.Equal(LuauExecutionOptions.Default, execution);
        Assert.Equal(LuauStateOptions.Default, options);
        options.Validate();

        using var state = LuauState.Create(options);
        Assert.True(state.MemoryUsage.IsTracked);
        Assert.Equal(options.MemoryLimitBytes, state.MemoryUsage.LimitBytes);
        Assert.NotSame(options, state.Options);
        Assert.NotSame(options.DefaultExecutionOptions, state.Options.DefaultExecutionOptions);
        Assert.Equal(options, state.Options);
    }

    [Fact]
    public void WithCloningPreservesDefaultsAndDoesNotMutateItsSource()
    {
        var scheduler = new InlineScheduler();
        var source = LuauStateOptions.Default;
        var options = source with
        {
            MemoryLimitBytes = 32L * 1024 * 1024,
            DefaultExecutionOptions = source.DefaultExecutionOptions with
            {
                ContinuationScheduler = scheduler,
            },
        };

        Assert.Equal(64L * 1024 * 1024, source.MemoryLimitBytes);
        Assert.Null(source.DefaultExecutionOptions.ContinuationScheduler);
        Assert.Equal(32L * 1024 * 1024, options.MemoryLimitBytes);
        Assert.Equal(source.MaxSourceBytes, options.MaxSourceBytes);
        Assert.Equal(source.MaxBytecodeBytes, options.MaxBytecodeBytes);
        Assert.Equal(source.MaxManagedHandleCount, options.MaxManagedHandleCount);
        Assert.Equal(source.BytecodePolicy, options.BytecodePolicy);
        Assert.Equal(source.DefaultExecutionOptions.WallClockLimit, options.DefaultExecutionOptions.WallClockLimit);
        Assert.Equal(source.DefaultExecutionOptions.InterruptCountLimit, options.DefaultExecutionOptions.InterruptCountLimit);
        Assert.Equal(source.DefaultExecutionOptions.MaxResultCount, options.DefaultExecutionOptions.MaxResultCount);
        Assert.Same(scheduler, options.DefaultExecutionOptions.ContinuationScheduler);
    }

    [Fact]
    public void SchedulerValidatorAndSingleLimitOverridesPreserveEveryOtherDefault()
    {
        var scheduler = new InlineScheduler();
        var scheduled = LuauExecutionOptions.Default with { ContinuationScheduler = scheduler };
        Assert.Equal(LuauExecutionOptions.Default.WallClockLimit, scheduled.WallClockLimit);
        Assert.Equal(LuauExecutionOptions.Default.InterruptCountLimit, scheduled.InterruptCountLimit);
        Assert.Equal(LuauExecutionOptions.Default.MaxResultCount, scheduled.MaxResultCount);

        var limited = LuauStateOptions.Default with { MaxSourceBytes = 4096 };
        Assert.Equal(LuauStateOptions.Default.MemoryLimitBytes, limited.MemoryLimitBytes);
        Assert.Equal(LuauStateOptions.Default.MaxBytecodeBytes, limited.MaxBytecodeBytes);
        Assert.Equal(LuauStateOptions.Default.MaxManagedHandleCount, limited.MaxManagedHandleCount);
        Assert.Same(LuauStateOptions.Default.DefaultExecutionOptions, limited.DefaultExecutionOptions);
        Assert.Equal(LuauBytecodePolicy.Reject, limited.BytecodePolicy);

        var validator = new AcceptingValidator();
        var validated = LuauStateOptions.Default with
        {
            BytecodePolicy = LuauBytecodePolicy.RequireValidator,
            BytecodeValidator = validator,
        };
        Assert.Equal(LuauStateOptions.Default.MemoryLimitBytes, validated.MemoryLimitBytes);
        Assert.Equal(LuauStateOptions.Default.MaxSourceBytes, validated.MaxSourceBytes);
        Assert.Equal(LuauStateOptions.Default.MaxBytecodeBytes, validated.MaxBytecodeBytes);
        Assert.Equal(LuauStateOptions.Default.MaxManagedHandleCount, validated.MaxManagedHandleCount);
        Assert.Same(LuauStateOptions.Default.DefaultExecutionOptions, validated.DefaultExecutionOptions);
        Assert.Same(validator, validated.BytecodeValidator);
        validated.Validate();
    }

    [Fact]
    public void UnboundedResourcesMustBeChosenExplicitlyAndStillRejectArtifacts()
    {
        var options = LuauStateOptions.UnboundedResources;

        Assert.Null(options.MemoryLimitBytes);
        Assert.Null(options.MaxSourceBytes);
        Assert.Null(options.MaxBytecodeBytes);
        Assert.Null(options.MaxManagedHandleCount);
        Assert.Null(options.DefaultExecutionOptions.WallClockLimit);
        Assert.Null(options.DefaultExecutionOptions.InterruptCountLimit);
        Assert.Null(options.DefaultExecutionOptions.MaxResultCount);
        Assert.False(options.DefaultExecutionOptions.HasBudget);
        Assert.Equal(LuauBytecodePolicy.Reject, options.BytecodePolicy);
        options.Validate();

        var output = LuauCompiler.Compile("return 42"u8);
        var artifact = LuauBytecodeArtifact.Create(
            output,
            "tests/unbounded-policy.luau",
            "tests/unbounded-policy");
        using var state = LuauState.Create(options);

        var exception = Assert.Throws<LuauException>(() => state.ExecuteVerifiedBytecode(artifact));
        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PerOperationOptionsOnlyTightenStatePolicy()
    {
        var scheduler = new InlineScheduler();
        var defaults = new LuauExecutionOptions
        {
            WallClockLimit = TimeSpan.FromSeconds(2),
            InterruptCountLimit = 100,
            MaxResultCount = 8,
            ContinuationScheduler = scheduler,
        };

        var effective = LuauExecutionOptions.ResolveForOperation(
            defaults,
            new LuauExecutionOptions
            {
                WallClockLimit = TimeSpan.FromSeconds(5),
                InterruptCountLimit = 50,
                MaxResultCount = 2,
            });

        Assert.Equal(TimeSpan.FromSeconds(2), effective.WallClockLimit);
        Assert.Equal(50, effective.InterruptCountLimit);
        Assert.Equal(2, effective.MaxResultCount);
        Assert.Same(scheduler, effective.ContinuationScheduler);
    }

    [Fact]
    public void PerOperationOptionsCannotReplaceStateScheduler()
    {
        var defaults = new LuauExecutionOptions
        {
            ContinuationScheduler = new InlineScheduler(),
        };

        Assert.Throws<InvalidOperationException>(() =>
            LuauExecutionOptions.ResolveForOperation(
                defaults,
                new LuauExecutionOptions
                {
                    ContinuationScheduler = new InlineScheduler(),
                }));
    }

    [Fact]
    public void StateOptionsRetainConfiguredLimits()
    {
        var execution = new LuauExecutionOptions
        {
            WallClockLimit = TimeSpan.FromMilliseconds(250),
            InterruptCountLimit = 50,
            MaxResultCount = 25,
        };
        var validator = new AcceptingValidator();
        var options = new LuauStateOptions
        {
            MemoryLimitBytes = 1_000_000,
            MaxSourceBytes = 10_000,
            MaxBytecodeBytes = 20_000,
            DefaultExecutionOptions = execution,
            BytecodePolicy = LuauBytecodePolicy.RequireValidator,
            BytecodeValidator = validator,
        };

        options.Validate();

        Assert.Equal(1_000_000, options.MemoryLimitBytes);
        Assert.Equal(10_000, options.MaxSourceBytes);
        Assert.Equal(20_000, options.MaxBytecodeBytes);
        Assert.Same(execution, options.DefaultExecutionOptions);
        Assert.Equal(25, execution.MaxResultCount);
        Assert.Same(validator, options.BytecodeValidator);
        Assert.True(execution.HasBudget);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void MemoryLimitMustBePositive(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauStateOptions { MemoryLimitBytes = value });
    }

    [Fact]
    public void MemoryLimitMustLeaveRoomForRepresentableFailureDiagnostics()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauStateOptions { MemoryLimitBytes = long.MaxValue });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void SourceLimitMustBePositive(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauStateOptions { MaxSourceBytes = value });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void BytecodeLimitMustBePositive(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauStateOptions { MaxBytecodeBytes = value });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ManagedHandleLimitMustBePositive(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauStateOptions { MaxManagedHandleCount = value });
    }

    [Fact]
    public void WallClockLimitMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauExecutionOptions { WallClockLimit = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauExecutionOptions { WallClockLimit = TimeSpan.FromTicks(-1) });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InterruptCountLimitMustBePositive(long value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauExecutionOptions { InterruptCountLimit = value });
    }

    [Fact]
    public void ResultCountLimitCannotBeNegative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauExecutionOptions { MaxResultCount = -1 });
        Assert.Equal(0, new LuauExecutionOptions { MaxResultCount = 0 }.MaxResultCount);
    }

    [Fact]
    public void RequireValidatorMustHaveValidator()
    {
        var options = new LuauStateOptions
        {
            BytecodePolicy = LuauBytecodePolicy.RequireValidator,
        };

        var exception = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains(nameof(LuauStateOptions.BytecodeValidator), exception.Message);
    }

    [Fact]
    public void RejectPolicyDoesNotRequireValidator()
    {
        var options = new LuauStateOptions
        {
            BytecodePolicy = LuauBytecodePolicy.Reject,
        };

        options.Validate();
    }

    [Fact]
    public void UndefinedBytecodePolicyIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LuauStateOptions
        {
            BytecodePolicy = (LuauBytecodePolicy)int.MaxValue,
        });
    }

    [Fact]
    public void DefaultExecutionOptionsCannotBeNull()
    {
        Assert.Throws<ArgumentNullException>(() => new LuauStateOptions
        {
            DefaultExecutionOptions = null!,
        });
    }

    sealed class AcceptingValidator : ILuauBytecodeValidator
    {
        public bool IsValid(LuauBytecodeArtifact artifact, ReadOnlySpan<byte> bytecode) => true;
    }

    sealed class InlineScheduler : ILuauContinuationScheduler
    {
        public bool CheckAccess() => true;

        public void Post(Action continuation) => continuation();
    }
}
