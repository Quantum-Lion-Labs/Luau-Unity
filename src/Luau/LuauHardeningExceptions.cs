using System.Globalization;

namespace Luau;

/// <summary>
/// Identifies which kind of input exceeded a configured loading bound.
/// </summary>
public enum LuauLoadInputKind
{
    /// <summary>UTF-8 source text.</summary>
    Source = 0,

    /// <summary>Precompiled Luau bytecode.</summary>
    Bytecode = 1,
}

/// <summary>
/// Identifies the execution budget that stopped a script.
/// </summary>
public enum LuauExecutionBudgetKind
{
    /// <summary>The wall-clock duration was exhausted.</summary>
    WallClock = 0,

    /// <summary>The interrupt callback count was exhausted.</summary>
    InterruptCount = 1,
}

/// <summary>
/// Base class for configured byte-count limit failures.
/// </summary>
public abstract class LuauLimitException : LuauException
{
    /// <summary>
    /// Initializes a byte-count limit failure.
    /// </summary>
    protected LuauLimitException(string message, string? chunkName, long limitBytes)
        : base(message, chunkName)
    {
        if (limitBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limitBytes), limitBytes, "A byte-count limit must be greater than zero.");
        }

        LimitBytes = limitBytes;
    }

    /// <summary>
    /// Gets the configured limit in bytes.
    /// </summary>
    public long LimitBytes { get; }
}

/// <summary>
/// Thrown before compilation or native loading when source or bytecode exceeds
/// its configured size bound.
/// </summary>
public sealed class LuauLoadLimitException : LuauLimitException
{
    /// <summary>
    /// Initializes a load-size failure.
    /// </summary>
    public LuauLoadLimitException(
        string? chunkName,
        LuauLoadInputKind inputKind,
        long actualBytes,
        long limitBytes)
        : this(chunkName, LoadLimitDetails.Create(inputKind, actualBytes, limitBytes))
    {
    }

    LuauLoadLimitException(string? chunkName, LoadLimitDetails details)
        : base(
            CreateMessage(chunkName, details.InputKind, details.ActualBytes, details.LimitBytes),
            chunkName,
            details.LimitBytes)
    {
        InputKind = details.InputKind;
        ActualBytes = details.ActualBytes;
    }

    /// <summary>
    /// Gets whether source or bytecode exceeded its bound.
    /// </summary>
    public LuauLoadInputKind InputKind { get; }

    /// <summary>
    /// Gets the observed payload size in bytes.
    /// </summary>
    public long ActualBytes { get; }

    static string CreateMessage(string? chunkName, LuauLoadInputKind inputKind, long actualBytes, long limitBytes)
    {
        var label = inputKind == LuauLoadInputKind.Bytecode ? "Bytecode" : "Source";
        return LuauDiagnosticMessages.WithChunk(
            $"{label} size of {actualBytes.ToString(CultureInfo.InvariantCulture)} bytes exceeds the configured " +
            $"{limitBytes.ToString(CultureInfo.InvariantCulture)}-byte limit.",
            chunkName);
    }

    readonly struct LoadLimitDetails
    {
        LoadLimitDetails(LuauLoadInputKind inputKind, long actualBytes, long limitBytes)
        {
            InputKind = inputKind;
            ActualBytes = actualBytes;
            LimitBytes = limitBytes;
        }

        public LuauLoadInputKind InputKind { get; }
        public long ActualBytes { get; }
        public long LimitBytes { get; }

        public static LoadLimitDetails Create(LuauLoadInputKind inputKind, long actualBytes, long limitBytes)
        {
            if (inputKind < LuauLoadInputKind.Source || inputKind > LuauLoadInputKind.Bytecode)
            {
                throw new ArgumentOutOfRangeException(nameof(inputKind), inputKind, "Unknown load input kind.");
            }

            if (limitBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limitBytes), limitBytes, "A load limit must be greater than zero.");
            }

            if (actualBytes <= limitBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(actualBytes),
                    actualBytes,
                    "Actual size must be greater than the configured limit.");
            }

            return new LoadLimitDetails(inputKind, actualBytes, limitBytes);
        }
    }
}

/// <summary>
/// Thrown after the tracked allocator rejects an allocation that would exceed
/// a root state's native memory limit.
/// </summary>
public sealed class LuauMemoryLimitException : LuauLimitException
{
    /// <summary>
    /// Initializes a memory-limit failure from the last stable usage snapshot.
    /// </summary>
    public LuauMemoryLimitException(
        string? chunkName,
        LuauMemoryUsageSnapshot usage,
        long attemptedBytes)
        : this(chunkName, MemoryLimitDetails.Create(usage, attemptedBytes))
    {
    }

    LuauMemoryLimitException(string? chunkName, MemoryLimitDetails details)
        : base(
            CreateMessage(chunkName, details.Usage, details.AttemptedBytes),
            chunkName,
            details.Usage.LimitBytes!.Value)
    {
        Usage = details.Usage;
        AttemptedBytes = details.AttemptedBytes;
    }

    /// <summary>
    /// Gets the last stable native memory usage snapshot.
    /// </summary>
    public LuauMemoryUsageSnapshot Usage { get; }

    /// <summary>
    /// Gets the native VM usage that the rejected allocation would have
    /// produced.
    /// </summary>
    public long AttemptedBytes { get; }

    static string CreateMessage(
        string? chunkName,
        LuauMemoryUsageSnapshot usage,
        long attemptedBytes)
    {
        var limit = usage.LimitBytes!.Value;
        return LuauDiagnosticMessages.WithChunk(
            $"Native VM memory limit of {limit.ToString(CultureInfo.InvariantCulture)} bytes was exceeded " +
            $"at {usage.CurrentBytes.ToString(CultureInfo.InvariantCulture)} bytes; the attempted usage was " +
            $"{attemptedBytes.ToString(CultureInfo.InvariantCulture)} bytes.",
            chunkName);
    }

    readonly struct MemoryLimitDetails
    {
        MemoryLimitDetails(LuauMemoryUsageSnapshot usage, long attemptedBytes)
        {
            Usage = usage;
            AttemptedBytes = attemptedBytes;
        }

        public LuauMemoryUsageSnapshot Usage { get; }
        public long AttemptedBytes { get; }

        public static MemoryLimitDetails Create(LuauMemoryUsageSnapshot usage, long attemptedBytes)
        {
            var limit = usage.LimitBytes
                ?? throw new ArgumentException(
                    "A memory-limit exception requires a limited usage snapshot.",
                    nameof(usage));

            if (attemptedBytes <= limit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(attemptedBytes),
                    attemptedBytes,
                    "Attempted memory usage must be greater than the configured limit.");
            }

            return new MemoryLimitDetails(usage, attemptedBytes);
        }
    }
}

/// <summary>
/// Thrown when a configured wall-clock or interrupt-count execution budget is
/// exhausted.
/// </summary>
public sealed class LuauExecutionBudgetException : LuauException
{
    /// <summary>
    /// Initializes a wall-clock budget failure.
    /// </summary>
    public LuauExecutionBudgetException(string? chunkName, TimeSpan limit, TimeSpan elapsed)
        : this(chunkName, WallClockBudgetDetails.Create(limit, elapsed))
    {
    }

    LuauExecutionBudgetException(string? chunkName, WallClockBudgetDetails details)
        : base(CreateWallClockMessage(chunkName, details.Limit, details.Elapsed), chunkName)
    {
        BudgetKind = LuauExecutionBudgetKind.WallClock;
        WallClockLimit = details.Limit;
        Elapsed = details.Elapsed;
    }

    /// <summary>
    /// Initializes an interrupt-count budget failure.
    /// </summary>
    public LuauExecutionBudgetException(string? chunkName, long limit, long observedInterruptCount)
        : this(chunkName, InterruptBudgetDetails.Create(limit, observedInterruptCount))
    {
    }

    LuauExecutionBudgetException(string? chunkName, InterruptBudgetDetails details)
        : base(CreateInterruptMessage(chunkName, details.Limit, details.ObservedCount), chunkName)
    {
        BudgetKind = LuauExecutionBudgetKind.InterruptCount;
        InterruptCountLimit = details.Limit;
        ObservedInterruptCount = details.ObservedCount;
    }

    /// <summary>Gets the budget that stopped execution.</summary>
    public LuauExecutionBudgetKind BudgetKind { get; }

    /// <summary>Gets the wall-clock limit when <see cref="BudgetKind"/> is wall-clock.</summary>
    public TimeSpan? WallClockLimit { get; }

    /// <summary>Gets elapsed time when <see cref="BudgetKind"/> is wall-clock.</summary>
    public TimeSpan? Elapsed { get; }

    /// <summary>Gets the interrupt limit when <see cref="BudgetKind"/> is interrupt-count.</summary>
    public long? InterruptCountLimit { get; }

    /// <summary>Gets the observed count when <see cref="BudgetKind"/> is interrupt-count.</summary>
    public long? ObservedInterruptCount { get; }

    static string CreateWallClockMessage(string? chunkName, TimeSpan limit, TimeSpan elapsed)
    {
        return LuauDiagnosticMessages.WithChunk(
            $"Wall-clock execution budget of {limit.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms " +
            $"was exceeded after {elapsed.TotalMilliseconds.ToString(CultureInfo.InvariantCulture)} ms.",
            chunkName);
    }

    static string CreateInterruptMessage(string? chunkName, long limit, long observedInterruptCount)
    {
        return LuauDiagnosticMessages.WithChunk(
            $"Interrupt-count execution budget of {limit.ToString(CultureInfo.InvariantCulture)} was exceeded " +
            $"at count {observedInterruptCount.ToString(CultureInfo.InvariantCulture)}.",
            chunkName);
    }

    readonly struct WallClockBudgetDetails
    {
        WallClockBudgetDetails(TimeSpan limit, TimeSpan elapsed)
        {
            Limit = limit;
            Elapsed = elapsed;
        }

        public TimeSpan Limit { get; }
        public TimeSpan Elapsed { get; }

        public static WallClockBudgetDetails Create(TimeSpan limit, TimeSpan elapsed)
        {
            if (limit <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "A wall-clock limit must be greater than zero.");
            }

            if (elapsed < limit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(elapsed),
                    elapsed,
                    "Elapsed time must be at least the configured limit.");
            }

            return new WallClockBudgetDetails(limit, elapsed);
        }
    }

    readonly struct InterruptBudgetDetails
    {
        InterruptBudgetDetails(long limit, long observedCount)
        {
            Limit = limit;
            ObservedCount = observedCount;
        }

        public long Limit { get; }
        public long ObservedCount { get; }

        public static InterruptBudgetDetails Create(long limit, long observedCount)
        {
            if (limit <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), limit, "An interrupt-count limit must be greater than zero.");
            }

            if (observedCount < limit)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(observedCount),
                    observedCount,
                    "The observed interrupt count must be at least the configured limit.");
            }

            return new InterruptBudgetDetails(limit, observedCount);
        }
    }
}

/// <summary>
/// Thrown when a script returns more values than the host-configured result
/// bound. The runner removes the rejected values before throwing.
/// </summary>
public sealed class LuauResultLimitException : LuauException
{
    /// <summary>Initializes a result-count limit failure.</summary>
    public LuauResultLimitException(string? chunkName, int actualCount, int limit)
        : base(CreateMessage(chunkName, actualCount, limit), chunkName)
    {
        if (limit < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "A result-count limit cannot be negative.");
        }

        if (actualCount <= limit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actualCount),
                actualCount,
                "Actual result count must exceed the configured limit.");
        }

        ActualCount = actualCount;
        Limit = limit;
    }

    /// <summary>Gets the rejected result count.</summary>
    public int ActualCount { get; }

    /// <summary>Gets the configured maximum result count.</summary>
    public int Limit { get; }

    static string CreateMessage(string? chunkName, int actualCount, int limit)
    {
        return LuauDiagnosticMessages.WithChunk(
            $"Result count of {actualCount.ToString(CultureInfo.InvariantCulture)} exceeds the configured " +
            $"limit of {limit.ToString(CultureInfo.InvariantCulture)}.",
            chunkName);
    }
}

/// <summary>
/// Thrown when an operation needs more Luau stack slots than one frame
/// permits. A Luau stack frame holds at most 8000 values, so returning,
/// yielding, or passing thousands of values fails this way. The runtime
/// removes the operation's values before throwing and the state remains
/// usable.
/// </summary>
public sealed class LuauStackLimitException : LuauException
{
    /// <summary>Initializes a stack-slot limit failure.</summary>
    public LuauStackLimitException(string? chunkName)
        : base(
            LuauDiagnosticMessages.WithChunk(
                "The operation needs more Luau stack slots than one frame permits.",
                chunkName),
            chunkName)
    {
    }

    internal LuauStackLimitException(string? chunkName, string operation)
        : base(
            LuauDiagnosticMessages.WithChunk(
                $"The Luau stack frame has no free slot to {operation}; one frame holds at most 8000 values.",
                chunkName),
            chunkName)
    {
    }
}

/// <summary>Identifies which managed string-decoding budget was exceeded.</summary>
public enum LuauDecodedResultLimitKind
{
    /// <summary>One native UTF-8 string exceeded the per-string limit.</summary>
    StringBytes = 0,

    /// <summary>Decoded strings exceeded the aggregate operation budget.</summary>
    OperationBytes = 1,
}

/// <summary>
/// Thrown before allocating a managed string when native UTF-8 data exceeds a
/// configured per-string or aggregate decoding budget.
/// </summary>
public sealed class LuauDecodedResultLimitException : LuauLimitException
{
    /// <summary>Initializes a decoded-result limit failure.</summary>
    public LuauDecodedResultLimitException(
        string? chunkName,
        LuauDecodedResultLimitKind limitKind,
        long actualBytes,
        long limitBytes)
        : base(CreateMessage(chunkName, limitKind, actualBytes, limitBytes), chunkName, limitBytes)
    {
        if (limitKind < LuauDecodedResultLimitKind.StringBytes ||
            limitKind > LuauDecodedResultLimitKind.OperationBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(limitKind), limitKind, "Unknown decoded-result limit.");
        }
        if (actualBytes <= limitBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(actualBytes),
                actualBytes,
                "The observed decoded byte count must exceed the configured limit.");
        }

        LimitKind = limitKind;
        ActualBytes = actualBytes;
    }

    /// <summary>Gets the decoding budget that was exceeded.</summary>
    public LuauDecodedResultLimitKind LimitKind { get; }

    /// <summary>Gets the observed UTF-8 byte count.</summary>
    public long ActualBytes { get; }

    static string CreateMessage(
        string? chunkName,
        LuauDecodedResultLimitKind limitKind,
        long actualBytes,
        long limitBytes)
    {
        var label = limitKind == LuauDecodedResultLimitKind.StringBytes
            ? "Decoded string size"
            : "Aggregate decoded result size";
        return LuauDiagnosticMessages.WithChunk(
            $"{label} of {actualBytes.ToString(CultureInfo.InvariantCulture)} bytes exceeds the configured " +
            $"{limitBytes.ToString(CultureInfo.InvariantCulture)}-byte limit.",
            chunkName);
    }
}

/// <summary>
/// Reports exhaustion of the process-wide opaque native reference-token space.
/// Tokens are intentionally never reused, so the process must be restarted
/// before new reference-valued wrappers can be created.
/// </summary>
public sealed class LuauReferenceLimitException : LuauException
{
    /// <summary>Initializes a native reference-token exhaustion failure.</summary>
    public LuauReferenceLimitException()
        : base("The Luau host exhausted its process-wide opaque reference-token space.")
    {
    }
}

/// <summary>
/// Thrown when cancellation stops Luau execution. It derives from
/// <see cref="OperationCanceledException"/> so tasks retain normal .NET
/// cancellation semantics while exposing chunk context.
/// </summary>
public sealed class LuauExecutionCanceledException : OperationCanceledException
{
    /// <summary>
    /// Initializes a cancellation failure for a chunk.
    /// </summary>
    public LuauExecutionCanceledException(string? chunkName, CancellationToken cancellationToken = default)
        : base(
            LuauDiagnosticMessages.WithChunk("Luau execution was canceled.", chunkName),
            cancellationToken)
    {
        ChunkName = chunkName;
    }

    /// <summary>
    /// Gets the exact host-provided chunk name, or null when none was supplied.
    /// </summary>
    public string? ChunkName { get; }
}

/// <summary>
/// Wraps an exception thrown by a managed function after it has been caught at
/// the unmanaged callback boundary.
/// </summary>
public sealed class LuauManagedCallbackException : LuauException
{
    /// <summary>
    /// Initializes a controlled managed-callback failure.
    /// </summary>
    public LuauManagedCallbackException(
        string? chunkName,
        string? callbackName,
        Exception innerException)
        : this(chunkName, callbackName, CallbackFailureDetails.Create(innerException))
    {
    }

    LuauManagedCallbackException(
        string? chunkName,
        string? callbackName,
        CallbackFailureDetails details)
        : base(
            CreateMessage(chunkName, callbackName, details.SafeMessage),
            chunkName,
            details.InnerException)
    {
        CallbackName = callbackName;
    }

    /// <summary>
    /// Gets the host-provided callback name, or null when the callback was unnamed.
    /// </summary>
    public string? CallbackName { get; }

    static string CreateMessage(string? chunkName, string? callbackName, string safeMessage)
    {
        var callback = string.IsNullOrEmpty(callbackName)
            ? "Managed callback"
            : $"Managed callback '{callbackName}'";

        return LuauDiagnosticMessages.WithChunk($"{callback} failed: {safeMessage}", chunkName);
    }

    readonly struct CallbackFailureDetails
    {
        CallbackFailureDetails(Exception innerException, string safeMessage)
        {
            InnerException = innerException;
            SafeMessage = safeMessage;
        }

        public Exception InnerException { get; }
        public string SafeMessage { get; }

        public static CallbackFailureDetails Create(Exception? innerException)
        {
            if (innerException is null)
            {
                throw new ArgumentNullException(nameof(innerException));
            }

            string safeMessage;
            try
            {
                safeMessage = innerException.Message;
            }
            catch
            {
                var type = innerException.GetType();
                safeMessage = $"<{type.FullName ?? type.Name} message unavailable>";
            }

            return new CallbackFailureDetails(innerException, safeMessage);
        }
    }
}

static class LuauDiagnosticMessages
{
    public static string WithChunk(string message, string? chunkName)
    {
        return string.IsNullOrEmpty(chunkName) ? message : $"{chunkName}: {message}";
    }
}
