namespace Luau;

/// <summary>
/// Configures resource limits and loading policy for a root Luau state.
/// Options are validated when initialized so native callbacks can consume
/// primitive, prevalidated values without performing validation or allocating.
/// </summary>
public sealed record LuauStateOptions
{
    long? memoryLimitBytes = 64L * 1024 * 1024;
    int? maxSourceBytes = 1024 * 1024;
    int? maxBytecodeBytes = 4 * 1024 * 1024;
    int? maxManagedHandleCount = 1024;
    int? maxDecodedStringBytes = 1024 * 1024;
    long? maxDecodedBytesPerOperation = 4L * 1024 * 1024;
    int maxDiagnosticBytes = 16 * 1024;
    int? maxCachedModuleCount = 256;
    long? maxCachedModuleBytes = 16L * 1024 * 1024;
    int? maxModuleDependencyDepth = 32;
    LuauExecutionOptions defaultExecutionOptions = LuauExecutionOptions.Default;
    LuauBytecodePolicy bytecodePolicy = LuauBytecodePolicy.Reject;

    /// <summary>
    /// Gets the finite default policy for ordinary, untrusted script states.
    /// Hosts that intentionally run trusted content may explicitly construct
    /// a less restrictive options instance.
    /// </summary>
    public static LuauStateOptions Default { get; } = new();

    /// <summary>
    /// Gets an explicitly unbounded resource profile. Persistent bytecode
    /// remains rejected and still requires separate validator-gated policy.
    /// </summary>
    public static LuauStateOptions UnboundedResources { get; } = new()
    {
        MemoryLimitBytes = null,
        MaxSourceBytes = null,
        MaxBytecodeBytes = null,
        MaxManagedHandleCount = null,
        MaxDecodedStringBytes = null,
        MaxDecodedBytesPerOperation = null,
        MaxDiagnosticBytes = 16 * 1024,
        MaxCachedModuleCount = null,
        MaxCachedModuleBytes = null,
        MaxModuleDependencyDepth = null,
        DefaultExecutionOptions = LuauExecutionOptions.Unbounded,
        BytecodePolicy = LuauBytecodePolicy.Reject,
    };

    /// <summary>
    /// Gets the optional native VM allocation limit in bytes. Managed wrapper
    /// allocations are not included in this limit.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned limit is zero or negative.
    /// </exception>
    public long? MemoryLimitBytes
    {
        get => memoryLimitBytes;
        init
        {
            if (value.HasValue && (value.Value <= 0 || value.Value == long.MaxValue))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MemoryLimitBytes),
                    value,
                    $"A memory limit must be between 1 and {long.MaxValue - 1} bytes.");
            }

            memoryLimitBytes = value;
        }
    }

    /// <summary>
    /// Gets the maximum UTF-8 source payload size in bytes, or
    /// <see langword="null"/> when source size is unbounded.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned limit is zero or negative.
    /// </exception>
    public int? MaxSourceBytes
    {
        get => maxSourceBytes;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxSourceBytes), value, "A source-size limit must be greater than zero.");
            }

            maxSourceBytes = value;
        }
    }

    /// <summary>
    /// Gets the maximum precompiled bytecode payload size in bytes, or
    /// <see langword="null"/> when bytecode size is unbounded.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned limit is zero or negative.
    /// </exception>
    public int? MaxBytecodeBytes
    {
        get => maxBytecodeBytes;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(MaxBytecodeBytes), value, "A bytecode-size limit must be greater than zero.");
            }

            maxBytecodeBytes = value;
        }
    }

    /// <summary>
    /// Gets the maximum number of live managed object capability registrations
    /// owned by this VM, or <see langword="null"/> when explicitly unbounded.
    /// Collected userdata releases its registration back to this quota.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned limit is zero or negative.
    /// </exception>
    public int? MaxManagedHandleCount
    {
        get => maxManagedHandleCount;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxManagedHandleCount),
                    value,
                    "A managed capability limit must be greater than zero.");
            }

            maxManagedHandleCount = value;
        }
    }

    /// <summary>
    /// Gets the maximum UTF-8 byte length of one Luau string decoded into
    /// managed memory, or <see langword="null"/> when explicitly unbounded.
    /// Native lengths are checked before allocating the managed string.
    /// </summary>
    public int? MaxDecodedStringBytes
    {
        get => maxDecodedStringBytes;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxDecodedStringBytes),
                    value,
                    "A decoded-string limit must be greater than zero.");
            }

            maxDecodedStringBytes = value;
        }
    }

    /// <summary>
    /// Gets the aggregate UTF-8 byte budget for strings decoded during one
    /// execution, callback, module, or direct host operation, or
    /// <see langword="null"/> when explicitly unbounded.
    /// </summary>
    public long? MaxDecodedBytesPerOperation
    {
        get => maxDecodedBytesPerOperation;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxDecodedBytesPerOperation),
                    value,
                    "An aggregate decoded-result limit must be greater than zero.");
            }

            maxDecodedBytesPerOperation = value;
        }
    }

    /// <summary>
    /// Gets the maximum UTF-8 bytes decoded from a native diagnostic. Longer
    /// diagnostics are truncated at a valid UTF-8 boundary.
    /// </summary>
    public int MaxDiagnosticBytes
    {
        get => maxDiagnosticBytes;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxDiagnosticBytes),
                    value,
                    "A diagnostic limit must be greater than zero.");
            }

            maxDiagnosticBytes = value;
        }
    }

    /// <summary>
    /// Gets the maximum retained module results in this root VM's shared
    /// module cache, or <see langword="null"/> when explicitly unbounded.
    /// </summary>
    public int? MaxCachedModuleCount
    {
        get => maxCachedModuleCount;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxCachedModuleCount),
                    value,
                    "A module-cache limit must be positive.");
            }
            maxCachedModuleCount = value;
        }
    }

    /// <summary>
    /// Gets the maximum retained UTF-16 payload bytes of cache keys and
    /// string-valued module results in this root, or <see langword="null"/>
    /// when explicitly unbounded. The default is 16 MiB. Object and dictionary
    /// overhead is bounded separately by <see cref="MaxCachedModuleCount"/>;
    /// referenced Luau objects remain subject to the native VM memory limit.
    /// </summary>
    public long? MaxCachedModuleBytes
    {
        get => maxCachedModuleBytes;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxCachedModuleBytes),
                    value,
                    "A module-cache byte limit must be positive.");
            }
            maxCachedModuleBytes = value;
        }
    }

    /// <summary>
    /// Gets the maximum nested managed <c>require</c> dependency depth, or
    /// <see langword="null"/> when explicitly unbounded.
    /// </summary>
    public int? MaxModuleDependencyDepth
    {
        get => maxModuleDependencyDepth;
        init
        {
            if (value.HasValue && value.Value <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(MaxModuleDependencyDepth),
                    value,
                    "A module dependency-depth limit must be positive.");
            }
            maxModuleDependencyDepth = value;
        }
    }

    /// <summary>
    /// Gets the budgets used when an execution call does not supply its own
    /// <see cref="LuauExecutionOptions"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException">The assigned value is null.</exception>
    public LuauExecutionOptions DefaultExecutionOptions
    {
        get => defaultExecutionOptions;
        init => defaultExecutionOptions = value ?? throw new ArgumentNullException(nameof(DefaultExecutionOptions));
    }

    /// <summary>
    /// Gets the policy for persistent bytecode artifacts. The default is
    /// <see cref="LuauBytecodePolicy.Reject"/>. A trusted host must configure
    /// <see cref="LuauBytecodePolicy.RequireValidator"/> with a provenance validator.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The assigned enum value is not defined.
    /// </exception>
    public LuauBytecodePolicy BytecodePolicy
    {
        get => bytecodePolicy;
        init
        {
            if (value < LuauBytecodePolicy.Reject || value > LuauBytecodePolicy.RequireValidator)
            {
                throw new ArgumentOutOfRangeException(nameof(BytecodePolicy), value, "Unknown bytecode policy.");
            }

            bytecodePolicy = value;
        }
    }

    /// <summary>
    /// Gets the host validator used when <see cref="BytecodePolicy"/> is
    /// <see cref="LuauBytecodePolicy.RequireValidator"/>.
    /// </summary>
    public ILuauBytecodeValidator? BytecodeValidator { get; init; }

    /// <summary>
    /// Validates relationships between options after object initialization.
    /// State creation should call this before allocating native resources.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Bytecode validation is required but no validator is configured.
    /// </exception>
    public void Validate()
    {
        if (bytecodePolicy == LuauBytecodePolicy.RequireValidator && BytecodeValidator is null)
        {
            throw new InvalidOperationException(
                $"{nameof(BytecodeValidator)} must be provided when {nameof(BytecodePolicy)} is {LuauBytecodePolicy.RequireValidator}.");
        }
    }

    internal LuauStateOptions Snapshot()
    {
        return this with
        {
            DefaultExecutionOptions = DefaultExecutionOptions with { },
        };
    }
}
