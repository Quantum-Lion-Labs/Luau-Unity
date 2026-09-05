using System.Globalization;

namespace Luau;

/// <summary>Bounds immutable module maps and precompiled bundles.</summary>
public sealed record LuauModuleLimits
{
    int? maxModuleCount = 256;
    long? maxTotalSourceBytes = 4L * 1024 * 1024;
    int? maxBytecodeBytesPerModule = 4 * 1024 * 1024;
    long? maxTotalBytecodeBytes = 16L * 1024 * 1024;
    int? maxModuleIdUtf8Bytes = 512;

    /// <summary>Gets the finite default module policy.</summary>
    public static LuauModuleLimits Default { get; } = new();

    /// <summary>Gets an explicitly unsafe, unbounded module policy.</summary>
    public static LuauModuleLimits UnsafeUnbounded { get; } = new()
    {
        MaxModuleCount = null,
        MaxTotalSourceBytes = null,
        MaxBytecodeBytesPerModule = null,
        MaxTotalBytecodeBytes = null,
        MaxModuleIdUtf8Bytes = null,
    };

    /// <summary>
    /// Gets the maximum modules in one map or bundle and, independently, the
    /// maximum aliases in one map.
    /// </summary>
    public int? MaxModuleCount
    {
        get => maxModuleCount;
        init => maxModuleCount = Validate(value, nameof(MaxModuleCount));
    }

    /// <summary>Gets the aggregate admitted source-byte limit.</summary>
    public long? MaxTotalSourceBytes
    {
        get => maxTotalSourceBytes;
        init => maxTotalSourceBytes = Validate(value, nameof(MaxTotalSourceBytes));
    }

    /// <summary>Gets the per-module compiled bytecode limit.</summary>
    public int? MaxBytecodeBytesPerModule
    {
        get => maxBytecodeBytesPerModule;
        init => maxBytecodeBytesPerModule = Validate(value, nameof(MaxBytecodeBytesPerModule));
    }

    /// <summary>Gets the aggregate compiled bytecode limit.</summary>
    public long? MaxTotalBytecodeBytes
    {
        get => maxTotalBytecodeBytes;
        init => maxTotalBytecodeBytes = Validate(value, nameof(MaxTotalBytecodeBytes));
    }

    /// <summary>Gets the UTF-8 byte limit for a canonical module ID.</summary>
    public int? MaxModuleIdUtf8Bytes
    {
        get => maxModuleIdUtf8Bytes;
        init => maxModuleIdUtf8Bytes = Validate(value, nameof(MaxModuleIdUtf8Bytes));
    }

    static int? Validate(int? value, string name)
    {
        if (value.HasValue && value.Value <= 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "A module limit must be positive.");
        }
        return value;
    }

    static long? Validate(long? value, string name)
    {
        if (value.HasValue && (value.Value <= 0 || value.Value == long.MaxValue))
        {
            throw new ArgumentOutOfRangeException(
                name,
                value,
                $"A module limit must be between 1 and {long.MaxValue - 1}.");
        }
        return value;
    }
}

/// <summary>Identifies a module resource dimension.</summary>
public enum LuauModuleLimitKind
{
    /// <summary>Modules in a map, bundle, or root cache.</summary>
    ModuleCount = 0,
    /// <summary>Aggregate admitted module source bytes.</summary>
    SourceBytes = 1,
    /// <summary>Bytecode bytes for one module.</summary>
    BytecodeBytesPerModule = 2,
    /// <summary>Aggregate bundle bytecode bytes.</summary>
    BundleBytecodeBytes = 3,
    /// <summary>UTF-8 bytes in one canonical module ID.</summary>
    ModuleIdBytes = 4,
    /// <summary>Nested require dependency depth.</summary>
    DependencyDepth = 5,
    /// <summary>Retained module results in one root VM cache.</summary>
    CachedResultCount = 6,
    /// <summary>Retained UTF-16 payload bytes of root-cache keys and string results.</summary>
    CachedResultBytes = 7,
}

/// <summary>Reports a finite module quota failure.</summary>
public sealed class LuauModuleLimitException : LuauException
{
    /// <summary>Initializes a module quota failure.</summary>
    public LuauModuleLimitException(LuauModuleLimitKind limitKind, long actual, long limit)
        : base(CreateMessage(limitKind, actual, limit))
    {
        if (limit <= 0) throw new ArgumentOutOfRangeException(nameof(limit));
        if (actual <= limit) throw new ArgumentOutOfRangeException(nameof(actual));
        LimitKind = limitKind;
        Actual = actual;
        Limit = limit;
    }

    /// <summary>Gets the exhausted quota.</summary>
    public LuauModuleLimitKind LimitKind { get; }
    /// <summary>Gets the observed count or byte size.</summary>
    public long Actual { get; }
    /// <summary>Gets the configured maximum.</summary>
    public long Limit { get; }

    static string CreateMessage(LuauModuleLimitKind kind, long actual, long limit) =>
        $"Module {kind} value {actual.ToString(CultureInfo.InvariantCulture)} exceeds the configured " +
        $"limit of {limit.ToString(CultureInfo.InvariantCulture)}.";
}

/// <summary>Reports the first deterministic module-bundle compilation failure.</summary>
public sealed class LuauModuleBundleCompilationException : LuauException
{
    internal LuauModuleBundleCompilationException(string moduleId, Exception innerException)
        : base($"Module '{moduleId}' could not be compiled: {innerException.Message}", null, innerException)
    {
        ModuleId = moduleId;
    }

    /// <summary>Gets the canonical module ID that failed.</summary>
    public string ModuleId { get; }
}
