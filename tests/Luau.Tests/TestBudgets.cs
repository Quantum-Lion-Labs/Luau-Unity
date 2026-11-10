namespace Luau.Tests;

/// <summary>
/// Execution budgets and wait limits for tests that are not about the
/// wall-clock budget.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="LuauExecutionOptions.Default"/> gives every operation 250 ms of
/// wall clock. That clock keeps running while the test thread is descheduled,
/// while a garbage collection that a parallel test forced has suspended it,
/// and while the operation awaits an asynchronous host callback. A loaded
/// runner loses a quarter of a second easily, and the test then fails with a
/// <see cref="LuauExecutionBudgetException"/> that says nothing about what it
/// checks.
/// </para>
/// <para>
/// Roots from <see cref="CreateRoot"/> keep every other default, including the
/// interrupt-count and result-count limits, and only the wall clock becomes a
/// safety net that a hung operation reaches. Tests about the default options
/// themselves create their roots with <c>LuauState.Create()</c>. Tests about
/// the wall-clock budget configure the limit they test.
/// </para>
/// <para>
/// A state combines per-operation options with its own by taking the tighter
/// of each limit, and a bare <c>new LuauExecutionOptions { ... }</c> also
/// carries the default 250 ms. Per-operation options that add one limit start
/// from <see cref="LuauExecutionOptions.Unbounded"/>, so they add only that
/// limit to the state's.
/// </para>
/// </remarks>
internal static class TestBudgets
{
    /// <summary>
    /// The wall-clock limit of an operation on a root from
    /// <see cref="CreateRoot"/>. It is shorter than <see cref="WaitTimeout"/>,
    /// so a script that keeps running, or a callback that honors its
    /// cancellation token, fails with a budget exception that names the chunk
    /// before the test gives up waiting. A callback that ignores its token, or
    /// a blocked owner scheduler, is not bounded by it; those reach
    /// <see cref="WaitTimeout"/>.
    /// </summary>
    public static readonly TimeSpan WallClockSafetyNet = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long a test waits for work it started (an operation, a callback, a
    /// task, a thread or a rendezvous) before it fails. Only a hang reaches it.
    /// </summary>
    public static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <see cref="LuauExecutionOptions.Default"/> with
    /// <see cref="WallClockSafetyNet"/> as its wall-clock limit.
    /// </summary>
    public static readonly LuauExecutionOptions Execution = LuauExecutionOptions.Default with
    {
        WallClockLimit = WallClockSafetyNet,
    };

    /// <summary>
    /// <see cref="LuauStateOptions.Default"/> with <see cref="Execution"/> as
    /// its default execution options. Tests that change another state limit
    /// start from this with a <c>with</c> expression.
    /// </summary>
    public static readonly LuauStateOptions StateOptions = LuauStateOptions.Default with
    {
        DefaultExecutionOptions = Execution,
    };

    /// <summary>
    /// Creates a root with <see cref="StateOptions"/>.
    /// </summary>
    public static LuauState CreateRoot() => LuauState.Create(StateOptions);
}
