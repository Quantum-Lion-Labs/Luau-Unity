namespace Luau;

/// <summary>
/// Dispatches asynchronous Luau execution continuations to a host-owned
/// execution context.
/// </summary>
/// <remarks>
/// <para>
/// Unity hosts should normally use <see cref="LuauSynchronizationContextScheduler"/>
/// captured on the Unity main thread. Implementations must deliver each
/// successfully posted continuation exactly once.
/// </para>
/// <para>
/// A state with a scheduler accepts VM access only where
/// <see cref="CheckAccess"/> returns <see langword="true"/>, and runs its
/// own continuations there. While an asynchronous operation waits for an
/// asynchronous host callback, it also accepts an access made there by a
/// caller whose <see cref="ExecutionContext"/> did not flow from the
/// callback: that is how code after an await in an async method that does
/// not carry the context (an <c>async UniTask</c> method, for example) keeps
/// working. A caller whose context belongs to another operation, of this
/// state's root or another, or to a finished operation, is still refused.
/// Code on the scheduler that is not part of the callback can therefore use
/// the root's tables and globals while a callback is pending.
/// </para>
/// <para>
/// A scheduler must run one piece of work at a time, as a single-threaded
/// <see cref="SynchronizationContext"/> such as Unity's main thread does:
/// the operation stops accepting such callers in a continuation that runs
/// on the scheduler, between accesses, and relies on no other access running
/// there at the same moment. A scheduler whose <see cref="CheckAccess"/>
/// returns <see langword="true"/> on more than one thread at once is not
/// supported.
/// </para>
/// </remarks>
public interface ILuauContinuationScheduler
{
    /// <summary>
    /// Gets whether the caller is already running on this scheduler's target
    /// execution context.
    /// </summary>
    bool CheckAccess();

    /// <summary>
    /// Posts a continuation to the target execution context.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="continuation"/> is <see langword="null"/>.
    /// </exception>
    void Post(Action continuation);
}
