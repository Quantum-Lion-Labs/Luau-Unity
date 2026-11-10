using System;

namespace Luau.Unity.Tests
{
    /// <summary>
    /// Execution budgets and wait limits for EditMode tests that are not about
    /// the wall-clock budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="LuauExecutionOptions.Default"/> gives every operation 250 ms
    /// of wall clock. That clock keeps running while the Editor's main thread
    /// is stalled (a Mono collection, an import, a busy runner) and while an
    /// asynchronous host callback waits for the Editor loop to run its
    /// continuation. A test that loses a quarter of a second then fails with a
    /// <see cref="LuauExecutionBudgetException"/> that says nothing about what
    /// it checks.
    /// </para>
    /// <para>
    /// <see cref="StateOptions"/> keeps every other default, including the
    /// interrupt-count and result-count limits, and turns the wall clock into a
    /// safety net that only a hung operation reaches. Tests about the default
    /// options themselves keep creating their roots from the defaults.
    /// </para>
    /// <para>
    /// The thread pool of the Editor's Mono starts work late when it is busy,
    /// so a test waiting for work it started on another thread waits for
    /// <see cref="WaitTimeout"/>, which only a hang reaches.
    /// </para>
    /// </remarks>
    static class TestBudgets
    {
        /// <summary>
        /// The wall-clock limit of an operation on a root built from
        /// <see cref="StateOptions"/>. Shorter than <see cref="WaitTimeout"/>.
        /// </summary>
        public static readonly TimeSpan WallClockSafetyNet = TimeSpan.FromSeconds(20);

        /// <summary>
        /// How long a test waits for work it started (a thread, a task, a
        /// callback or a posted continuation) before it fails.
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
        /// <see cref="LuauStateOptions.Default"/> with <see cref="Execution"/>
        /// as its default execution options.
        /// </summary>
        public static readonly LuauStateOptions StateOptions = LuauStateOptions.Default with
        {
            DefaultExecutionOptions = Execution,
        };

        /// <summary>Creates a root with <see cref="StateOptions"/>.</summary>
        public static LuauState CreateRoot()
        {
            return LuauState.Create(StateOptions);
        }
    }
}
