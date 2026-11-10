namespace Luau;

/// <summary>
/// Configures budgets for Luau execution, invocation, or resume operations.
/// State defaults are authoritative. Per-operation values may add or tighten
/// limits but cannot remove state limits or replace its continuation scheduler.
/// </summary>
public sealed record LuauExecutionOptions
{
    TimeSpan? wallClockLimit = TimeSpan.FromMilliseconds(250);
    long? interruptCountLimit = 100_000;
    int? maxResultCount = 64;

    /// <summary>
    /// Gets the finite execution policy used by ordinary script states.
    /// </summary>
    public static LuauExecutionOptions Default { get; } = new();

    /// <summary>
    /// Gets an explicitly unbounded execution policy. Hosts should use this
    /// profile only for trusted work whose resource ownership is controlled by
    /// another layer.
    /// </summary>
    public static LuauExecutionOptions Unbounded { get; } = new()
    {
        WallClockLimit = null,
        InterruptCountLimit = null,
        MaxResultCount = null,
    };

    /// <summary>
    /// Gets the maximum wall-clock duration for an operation, or
    /// <see langword="null"/> when no wall-clock budget is configured.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned duration is zero or negative.
    /// </exception>
    public TimeSpan? WallClockLimit
    {
        get => wallClockLimit;
        init
        {
            if (value.HasValue && value.Value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(WallClockLimit), value, "A wall-clock limit must be greater than zero.");
            }

            wallClockLimit = value;
        }
    }

    /// <summary>
    /// Gets the maximum number of interrupt callbacks permitted for an
    /// operation, or <see langword="null"/> when no interrupt-count budget is
    /// configured.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned count is zero or negative.
    /// </exception>
    public long? InterruptCountLimit
    {
        get => interruptCountLimit;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(InterruptCountLimit), value, "An interrupt-count limit must be greater than zero.");
            }

            interruptCountLimit = value;
        }
    }

    /// <summary>
    /// Gets the host scheduler used for asynchronous managed-callback dispatch
    /// and subsequent VM continuations, or <see langword="null"/> when no
    /// thread affinity is required.
    /// </summary>
    /// <remarks>
    /// Unity hosts should normally configure a
    /// <see cref="LuauSynchronizationContextScheduler"/> captured on the Unity
    /// main thread. This controls Luau's own continuation work; an async host
    /// callback that accesses thread-affine APIs after one of its own awaits
    /// must also preserve or explicitly restore the appropriate context.
    /// </remarks>
    public ILuauContinuationScheduler? ContinuationScheduler { get; init; }

    /// <summary>
    /// Gets the maximum number of values an operation may return, or
    /// <see langword="null"/> when result count is unlimited. This bounds both
    /// the Luau stack output and managed arrays created by convenience APIs.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned count is negative.
    /// </exception>
    public int? MaxResultCount
    {
        get => maxResultCount;
        init
        {
            if (value.HasValue && value.Value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxResultCount), value, "A result-count limit cannot be negative.");
            }

            maxResultCount = value;
        }
    }

    /// <summary>
    /// Gets whether at least one execution budget is configured.
    /// </summary>
    public bool HasBudget => wallClockLimit.HasValue || interruptCountLimit.HasValue || maxResultCount.HasValue;

    static readonly LuauExecutionOptions ZeroResultExecutionOptions = Unbounded with
    {
        MaxResultCount = 0,
    };

    /// <summary>
    /// Returns options that also require an operation to return no values.
    /// </summary>
    internal static LuauExecutionOptions RequireZeroResults(LuauExecutionOptions? executionOptions)
    {
        if (executionOptions == null)
        {
            return ZeroResultExecutionOptions;
        }

        return executionOptions.MaxResultCount == 0
            ? executionOptions
            : executionOptions with { MaxResultCount = 0 };
    }

    internal static LuauExecutionOptions ResolveForOperation(
        LuauExecutionOptions stateDefaults,
        LuauExecutionOptions? operationOptions)
    {
        if (operationOptions == null)
        {
            return stateDefaults;
        }

        if (operationOptions.ContinuationScheduler != null &&
            !ReferenceEquals(operationOptions.ContinuationScheduler, stateDefaults.ContinuationScheduler))
        {
            throw new InvalidOperationException(
                "A per-operation continuation scheduler cannot replace the Luau state's scheduler.");
        }

        return stateDefaults with
        {
            WallClockLimit = Tighter(stateDefaults.WallClockLimit, operationOptions.WallClockLimit),
            InterruptCountLimit = Tighter(stateDefaults.InterruptCountLimit, operationOptions.InterruptCountLimit),
            MaxResultCount = Tighter(stateDefaults.MaxResultCount, operationOptions.MaxResultCount),
            ContinuationScheduler = stateDefaults.ContinuationScheduler,
        };
    }

    static TimeSpan? Tighter(TimeSpan? stateLimit, TimeSpan? operationLimit)
    {
        if (!stateLimit.HasValue) return operationLimit;
        if (!operationLimit.HasValue) return stateLimit;
        return stateLimit.Value <= operationLimit.Value ? stateLimit : operationLimit;
    }

    static long? Tighter(long? stateLimit, long? operationLimit)
    {
        if (!stateLimit.HasValue) return operationLimit;
        if (!operationLimit.HasValue) return stateLimit;
        return Math.Min(stateLimit.Value, operationLimit.Value);
    }

    static int? Tighter(int? stateLimit, int? operationLimit)
    {
        if (!stateLimit.HasValue) return operationLimit;
        if (!operationLimit.HasValue) return stateLimit;
        return Math.Min(stateLimit.Value, operationLimit.Value);
    }
}
