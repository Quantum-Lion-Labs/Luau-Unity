namespace Luau;

/// <summary>
/// Provides callback-scoped access to Luau arguments and return values.
/// </summary>
/// <remarks>
/// A context is valid only while its callback is running. Retaining and using
/// it after the callback completes throws <see cref="InvalidOperationException"/>.
/// </remarks>
public readonly struct LuauCallContext
{
    readonly LuauCallFrame? frame;

    internal LuauCallContext(LuauCallFrame frame)
    {
        this.frame = frame;
    }

    /// <summary>Gets the number of arguments supplied by Luau.</summary>
    public int ArgumentCount => GetFrame().ArgumentCount;

    /// <summary>Gets the managed state that owns this callback.</summary>
    public LuauState State => GetFrame().State;

    /// <summary>Gets the cancellation token for the current operation.</summary>
    public CancellationToken CancellationToken => GetFrame().CancellationToken;

    /// <summary>Gets the diagnostic callback name, when one was supplied.</summary>
    public string? CallbackName => GetFrame().CallbackName;

    /// <summary>Reads a zero-based callback argument using managed conversion rules.</summary>
    public T Read<T>(int index)
    {
        return GetFrame().Read<T>(index);
    }

    /// <summary>
    /// Adds one managed value to the callback's ordered return values. Pushing
    /// a reference wrapper does not transfer or dispose its managed ownership;
    /// the callback or library must keep persistent wrappers live and dispose
    /// owned wrappers when they are no longer needed.
    /// </summary>
    public void Return<T>(T? value)
    {
        GetFrame().Return(value);
    }

    /// <summary>
    /// Formats a zero-based callback argument without allowing unbounded output.
    /// </summary>
    public string ToDisplayString(int index, int maxUtf8Bytes, out bool truncated)
    {
        return GetFrame().ToDisplayString(index, maxUtf8Bytes, out truncated);
    }

    LuauCallFrame GetFrame()
    {
        return frame ?? throw LuauCallFrame.CreateExpiredException();
    }
}

internal sealed class LuauCallFrame
{
    readonly string? callbackName;
    readonly LuauOperationCancellation cancellation;
    readonly int argumentCount;
    LuauState? state;
    int active = 1;
    int returnCount;
    List<ILuauCallbackBorrowedReference>? borrowedReferences;

    internal LuauCallFrame(
        LuauState state,
        string? callbackName,
        int argumentCount,
        LuauOperationCancellation cancellation)
    {
        this.state = state;
        this.callbackName = callbackName;
        this.argumentCount = argumentCount;
        this.cancellation = cancellation;
    }

    internal int ArgumentCount
    {
        get
        {
            EnsureActive();
            return argumentCount;
        }
    }

    internal LuauState State
    {
        get
        {
            EnsureActive();
            return state!;
        }
    }

    internal CancellationToken CancellationToken
    {
        get
        {
            EnsureActive();
            // Created on first use for the whole operation; while this frame
            // is active its operation is too.
            return cancellation.Token;
        }
    }

    internal string? CallbackName
    {
        get
        {
            EnsureActive();
            return callbackName;
        }
    }

    internal T Read<T>(int index)
    {
        EnsureArgumentIndex(index);
        try
        {
            return state!.ToValue(index + 1, this).Read<T>();
        }
        catch (Exception exception) when (exception is not ArgumentOutOfRangeException)
        {
            var callback = string.IsNullOrEmpty(callbackName)
                ? "managed callback"
                : $"managed callback '{callbackName}'";
            throw new InvalidOperationException(
                $"Argument {index} of {callback} cannot be read as {typeof(T).Name}.",
                exception);
        }
    }

    internal void Return<T>(T? value)
    {
        EnsureActive();
        state!.Push(LuauValue.CreateFrom(value));
        returnCount++;
    }

    internal string ToDisplayString(int index, int maxUtf8Bytes, out bool truncated)
    {
        EnsureArgumentIndex(index);
        return state!.ToDisplayString(index + 1, maxUtf8Bytes, out truncated);
    }

    internal int Complete()
    {
        EnsureActive();
        return returnCount;
    }

    internal void Invalidate()
    {
        if (Interlocked.Exchange(ref active, 0) == 0)
        {
            return;
        }

        var borrowed = borrowedReferences;
        borrowedReferences = null;
        if (borrowed != null)
        {
            for (var index = borrowed.Count - 1; index >= 0; index--)
            {
                try
                {
                    borrowed[index].InvalidateBorrowed();
                }
                catch
                {
                    // Callback invalidation must release every remaining
                    // borrowed reference even when one cleanup path fails.
                }
            }
        }

        state = null;
    }

    internal void RegisterBorrowed(ILuauCallbackBorrowedReference reference)
    {
        EnsureActive();
        (borrowedReferences ??= []).Add(reference);
    }

    internal void EnsureBorrowedActive() => EnsureActive();

    void EnsureArgumentIndex(int index)
    {
        EnsureActive();
        if ((uint)index >= (uint)ArgumentCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                index,
                $"Callback argument indexes are zero-based and must be less than {ArgumentCount}.");
        }
    }

    void EnsureActive()
    {
        if (Volatile.Read(ref active) == 0 || state == null)
        {
            throw CreateExpiredException();
        }
    }

    internal static InvalidOperationException CreateExpiredException()
    {
        return new InvalidOperationException(
            "The Luau callback context is no longer valid because its callback has completed.");
    }
}
