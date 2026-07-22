using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.CompilerServices;
using System.Threading;
using Unity.Profiling;

namespace Luau.Unity
{
    /// <summary>Controls how a scheduled phase responds to an entrypoint failure.</summary>
    public enum LuauScriptPhaseFailureMode
    {
        /// <summary>Disable the failed registration and continue dispatching the phase.</summary>
        DisableAndContinue = 0,

        /// <summary>Stop dispatching and rethrow the entrypoint failure.</summary>
        StopAndThrow = 1,
    }

    /// <summary>Defines the bounded execution and failure policy for one script phase.</summary>
    public sealed record LuauScriptPhaseOptions
    {
        /// <summary>
        /// Gets the finite policy applied to every entrypoint invocation in the phase.
        /// A wall-clock limit and an interrupt-count limit are required.
        /// </summary>
        public LuauExecutionOptions InvocationOptions { get; init; } = LuauExecutionOptions.Default;

        /// <summary>
        /// Gets the aggregate wall-clock budget after which the phase stops admitting calls.
        /// Failure-observer time is excluded from this script-execution budget.
        /// </summary>
        public TimeSpan AggregateWallClockBudget { get; init; }

        /// <summary>Gets the phase's entrypoint-failure behavior.</summary>
        public LuauScriptPhaseFailureMode FailureMode { get; init; } =
            LuauScriptPhaseFailureMode.DisableAndContinue;

        /// <summary>
        /// Gets an optional observer called for each attempted entrypoint that fails.
        /// Disable-and-continue registrations are disabled before this callback runs.
        /// Observer exceptions are contained so they cannot replace the phase's
        /// configured failure behavior.
        /// </summary>
        public Action<LuauScriptRegistration, Exception> FailureCallback { get; init; }
    }

    /// <summary>Reports one completed bounded phase dispatch without owning any resources.</summary>
    public readonly struct LuauScriptDispatchResult
    {
        internal LuauScriptDispatchResult(
            int attemptedCount,
            int succeededCount,
            int failedCount,
            int skippedCount,
            TimeSpan elapsed,
            bool budgetExhausted)
        {
            AttemptedCount = attemptedCount;
            SucceededCount = succeededCount;
            FailedCount = failedCount;
            SkippedCount = skippedCount;
            Elapsed = elapsed;
            BudgetExhausted = budgetExhausted;
        }

        /// <summary>Gets the number of entrypoint calls admitted by the phase.</summary>
        public int AttemptedCount { get; }

        /// <summary>Gets the number of entrypoint calls that completed successfully.</summary>
        public int SucceededCount { get; }

        /// <summary>Gets the number of admitted entrypoint calls that threw.</summary>
        public int FailedCount { get; }

        /// <summary>
        /// Gets the number of registrations not called because they were disabled,
        /// disposed, or reached after aggregate budget exhaustion.
        /// </summary>
        public int SkippedCount { get; }

        /// <summary>
        /// Gets actual aggregate wall-clock time, including failure-observer callbacks.
        /// </summary>
        public TimeSpan Elapsed { get; }

        /// <summary>Gets whether the aggregate phase budget was exhausted.</summary>
        public bool BudgetExhausted { get; }
    }

    /// <summary>
    /// Owns named, bounded script phases associated with one Luau root. Script
    /// instances registered with those phases remain caller-owned.
    /// </summary>
    public sealed class LuauScriptScheduler : IDisposable
    {
        static readonly ConditionalWeakTable<LuauState, RootDispatchCoordinator>
            RootDispatchCoordinators =
                new ConditionalWeakTable<LuauState, RootDispatchCoordinator>();

        readonly object gate = new object();
        readonly Dictionary<string, LuauScriptPhase> phases =
            new Dictionary<string, LuauScriptPhase>(StringComparer.Ordinal);
        readonly ILuauScriptSchedulerClock clock;
        readonly RootDispatchCoordinator dispatchCoordinator;
        int disposeState;

        /// <summary>Creates a scheduler for one caller-owned Luau root.</summary>
        public LuauScriptScheduler(LuauState root)
            : this(root, StopwatchLuauScriptSchedulerClock.Instance)
        {
        }

        internal LuauScriptScheduler(LuauState root, ILuauScriptSchedulerClock clock)
        {
            if (root == null)
            {
                throw new ArgumentNullException(nameof(root));
            }
            if (root.IsDisposed)
            {
                throw new ObjectDisposedException(nameof(root));
            }
            if (!root.IsMainThread)
            {
                throw new ArgumentException(
                    "A Luau script scheduler must be associated with a root state.",
                    nameof(root));
            }

            Root = root;
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            dispatchCoordinator = RootDispatchCoordinators.GetValue(
                root,
                _ => new RootDispatchCoordinator());
        }

        /// <summary>Gets the caller-owned root accepted by this scheduler.</summary>
        public LuauState Root { get; }

        /// <summary>Gets whether the scheduler and all its registrations were disposed.</summary>
        public bool IsDisposed => Volatile.Read(ref disposeState) != 0;

        /// <summary>Creates a uniquely named phase with an explicit bounded policy.</summary>
        public LuauScriptPhase CreatePhase(string name, LuauScriptPhaseOptions options)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A script phase name is required.", nameof(name));
            }
            if (options == null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var snapshot = ValidateAndSnapshot(options, Root);
            lock (gate)
            {
                ThrowIfDisposed();
                if (Root.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(Root));
                }
                if (phases.ContainsKey(name))
                {
                    throw new ArgumentException(
                        "A Luau script phase named '" + name + "' already exists.",
                        nameof(name));
                }

                var phase = new LuauScriptPhase(this, name, snapshot, clock);
                phases.Add(name, phase);
                return phase;
            }
        }

        /// <summary>Looks up a previously created phase by its case-sensitive name.</summary>
        public bool TryGetPhase(string name, out LuauScriptPhase phase)
        {
            if (name == null)
            {
                throw new ArgumentNullException(nameof(name));
            }

            lock (gate)
            {
                ThrowIfDisposed();
                return phases.TryGetValue(name, out phase);
            }
        }

        /// <summary>
        /// Disposes all phase registrations. The root and registered script
        /// instances remain caller-owned.
        /// </summary>
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposeState, 1) != 0)
            {
                return;
            }

            LuauScriptPhase[] snapshot;
            lock (gate)
            {
                snapshot = new LuauScriptPhase[phases.Count];
                phases.Values.CopyTo(snapshot, 0);
                phases.Clear();
            }

            for (var index = 0; index < snapshot.Length; index++)
            {
                snapshot[index].DisposeFromScheduler();
            }
        }

        internal void ThrowIfDisposed()
        {
            if (IsDisposed)
            {
                throw new ObjectDisposedException(nameof(LuauScriptScheduler));
            }
        }

        internal void BeginDispatch(LuauScriptPhase phase)
        {
            lock (gate)
            {
                ThrowIfDisposed();
                if (Root.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(Root));
                }

                var ownerScheduler = Root.Options.DefaultExecutionOptions.ContinuationScheduler;
                if (ownerScheduler != null && !ownerScheduler.CheckAccess())
                {
                    throw new InvalidOperationException(
                        "Luau script phases must be dispatched from the root state's owner scheduler.");
                }

            }

            dispatchCoordinator.BeginDispatch(Root, phase);
        }

        internal void EndDispatch(LuauScriptPhase phase)
        {
            dispatchCoordinator.EndDispatch(phase);
        }

        static LuauScriptPhaseOptions ValidateAndSnapshot(
            LuauScriptPhaseOptions options,
            LuauState root)
        {
            var invocationOptions = options.InvocationOptions;
            if (invocationOptions == null)
            {
                throw new ArgumentException(
                    "Phase invocation options are required.",
                    nameof(options));
            }
            if (!invocationOptions.WallClockLimit.HasValue ||
                !invocationOptions.InterruptCountLimit.HasValue)
            {
                throw new ArgumentException(
                    "Phase invocation options require finite wall-clock and interrupt-count limits.",
                    nameof(options));
            }
            var ownerScheduler = root.Options.DefaultExecutionOptions.ContinuationScheduler;
            if (invocationOptions.ContinuationScheduler != null &&
                !ReferenceEquals(invocationOptions.ContinuationScheduler, ownerScheduler))
            {
                throw new ArgumentException(
                    "Phase invocation options cannot replace the Luau root's continuation scheduler.",
                    nameof(options));
            }
            if (options.AggregateWallClockBudget <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.AggregateWallClockBudget,
                    "The aggregate phase wall-clock budget must be positive.");
            }
            if (!Enum.IsDefined(typeof(LuauScriptPhaseFailureMode), options.FailureMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    options.FailureMode,
                    "The script phase failure mode is not defined.");
            }

            return options with
            {
                InvocationOptions = invocationOptions with { },
            };
        }

        sealed class RootDispatchCoordinator
        {
            readonly object gate = new object();
            LuauScriptPhase activePhase;

            internal void BeginDispatch(LuauState root, LuauScriptPhase phase)
            {
                lock (gate)
                {
                    if (activePhase != null)
                    {
                        throw new InvalidOperationException(
                            "Luau script scheduler phase '" + activePhase.Name +
                            "' is already dispatching on this root. Overlapping and " +
                            "re-entrant dispatch are not supported (attempted phase '" +
                            phase.Name + "').");
                    }
                    if (root.Context.GetActiveOperation() != null)
                    {
                        throw new InvalidOperationException(
                            "The Luau root is already executing another operation. " +
                            "A script phase cannot be dispatched re-entrantly.");
                    }

                    activePhase = phase;
                }
            }

            internal void EndDispatch(LuauScriptPhase phase)
            {
                lock (gate)
                {
                    if (ReferenceEquals(activePhase, phase))
                    {
                        activePhase = null;
                    }
                }
            }
        }
    }

    /// <summary>One named, synchronously dispatched script phase.</summary>
    public sealed class LuauScriptPhase
    {
        readonly object gate = new object();
        readonly List<LuauScriptRegistration> registrations =
            new List<LuauScriptRegistration>();
        readonly ILuauScriptSchedulerClock clock;
        LuauScriptRegistration[] orderedRegistrations =
            Array.Empty<LuauScriptRegistration>();
        long nextSequence;
        bool orderDirty;
        bool dispatching;
        bool disposeRequested;
        bool disposed;

        internal LuauScriptPhase(
            LuauScriptScheduler scheduler,
            string name,
            LuauScriptPhaseOptions options,
            ILuauScriptSchedulerClock clock)
        {
            Scheduler = scheduler;
            Name = name;
            Options = options;
            this.clock = clock;
        }

        /// <summary>Gets the scheduler that owns this phase.</summary>
        public LuauScriptScheduler Scheduler { get; }

        /// <summary>Gets the unique case-sensitive phase name.</summary>
        public string Name { get; }

        /// <summary>Gets the immutable phase policy snapshot.</summary>
        public LuauScriptPhaseOptions Options { get; }

        /// <summary>
        /// Registers an entrypoint. Lower order values dispatch first and ties
        /// retain registration order.
        /// </summary>
        public LuauScriptRegistration Register(
            LuauScriptEntrypoint entrypoint,
            int order = 0)
        {
            if (entrypoint == null)
            {
                throw new ArgumentNullException(nameof(entrypoint));
            }

            lock (gate)
            {
                ThrowIfUnavailable();
                if (entrypoint.IsDisposed)
                {
                    throw new ObjectDisposedException(nameof(entrypoint));
                }
                if (!ReferenceEquals(entrypoint.Root, Scheduler.Root))
                {
                    throw new ArgumentException(
                        "The entrypoint belongs to a different Luau root.",
                        nameof(entrypoint));
                }

                var registration = new LuauScriptRegistration(
                    this,
                    entrypoint,
                    order,
                    nextSequence++);
                registrations.Add(registration);
                orderDirty = true;
                return registration;
            }
        }

        /// <summary>Dispatches every enabled registration with no arguments.</summary>
        public LuauScriptDispatchResult Dispatch()
        {
            return DispatchCore(default, DispatchArgumentKind.None, default);
        }

        /// <summary>Dispatches every enabled registration with one argument.</summary>
        public LuauScriptDispatchResult Dispatch(LuauValue argument)
        {
            return DispatchCore(default, DispatchArgumentKind.One, argument);
        }

        /// <summary>Dispatches every enabled registration with a borrowed argument span.</summary>
        public LuauScriptDispatchResult Dispatch(ReadOnlySpan<LuauValue> arguments)
        {
            return DispatchCore(arguments, DispatchArgumentKind.Span, default);
        }

        internal bool GetEnabled(LuauScriptRegistration registration)
        {
            lock (gate)
            {
                return registration.GetRequestedEnabled();
            }
        }

        internal bool GetDisposed(LuauScriptRegistration registration)
        {
            lock (gate)
            {
                return registration.IsDisposeRequested;
            }
        }

        internal int RetainedRegistrationCount
        {
            get
            {
                lock (gate)
                {
                    return registrations.Count + orderedRegistrations.Length;
                }
            }
        }

        internal void SetEnabled(LuauScriptRegistration registration, bool enabled)
        {
            lock (gate)
            {
                ThrowIfUnavailable();
                registration.ThrowIfDisposeRequested();
                if (dispatching)
                {
                    registration.SetPendingEnabled(enabled);
                }
                else
                {
                    registration.SetEnabledImmediately(enabled);
                }
            }
        }

        internal void DisposeRegistration(LuauScriptRegistration registration)
        {
            lock (gate)
            {
                if (registration.IsDisposeRequested)
                {
                    return;
                }

                if (dispatching)
                {
                    registration.RequestPendingDispose();
                }
                else
                {
                    registration.DisposeImmediately();
                    registrations.Remove(registration);
                    InvalidateOrderedSnapshot();
                }
            }
        }

        internal void DisposeFromScheduler()
        {
            lock (gate)
            {
                if (disposed || disposeRequested)
                {
                    return;
                }

                disposeRequested = true;
                if (dispatching)
                {
                    for (var index = 0; index < registrations.Count; index++)
                    {
                        registrations[index].RequestPendingDispose();
                    }
                    return;
                }

                DisposeAllImmediately();
            }
        }

        LuauScriptDispatchResult DispatchCore(
            ReadOnlySpan<LuauValue> arguments,
            DispatchArgumentKind argumentKind,
            LuauValue argument)
        {
            LuauScriptRegistration[] snapshot;
            Scheduler.BeginDispatch(this);
            try
            {
                lock (gate)
                {
                    ThrowIfUnavailable();
                    if (dispatching)
                    {
                        throw new InvalidOperationException(
                            "Luau script phase '" + Name + "' is already dispatching. " +
                            "Overlapping and re-entrant dispatch are not supported.");
                    }

                    EnsureOrderedSnapshot();
                    snapshot = orderedRegistrations;
                    dispatching = true;
                }
            }
            catch
            {
                Scheduler.EndDispatch(this);
                throw;
            }

            var started = clock.GetTimestamp();
            var excludedObserverElapsed = TimeSpan.Zero;
            var attempted = 0;
            var succeeded = 0;
            var failed = 0;
            var skipped = 0;
            var budgetExhausted = false;

            try
            {
                for (var index = 0; index < snapshot.Length; index++)
                {
                    var registration = snapshot[index];
                    if (registration.Entrypoint.IsDisposed)
                    {
                        PruneDisposedRegistration(registration);
                        skipped++;
                        continue;
                    }
                    if (budgetExhausted ||
                        ElapsedSince(started, excludedObserverElapsed) >=
                            Options.AggregateWallClockBudget)
                    {
                        budgetExhausted = true;
                        skipped++;
                        continue;
                    }
                    if (!IsEnabledForCurrentDispatch(registration))
                    {
                        skipped++;
                        continue;
                    }

                    attempted++;
                    try
                    {
                        using (registration.ProfilerMarker.Auto())
                        {
                            switch (argumentKind)
                            {
                                case DispatchArgumentKind.None:
                                    registration.Entrypoint.InvokeVoid(Options.InvocationOptions);
                                    break;
                                case DispatchArgumentKind.One:
                                    registration.Entrypoint.InvokeVoid(argument, Options.InvocationOptions);
                                    break;
                                default:
                                    registration.Entrypoint.InvokeVoid(arguments, Options.InvocationOptions);
                                    break;
                            }
                        }

                        succeeded++;
                    }
                    catch (Exception exception)
                    {
                        failed++;
                        if (Options.FailureMode == LuauScriptPhaseFailureMode.DisableAndContinue)
                        {
                            DisableBeforeFailureCallback(registration);
                        }

                        var observerStarted = clock.GetTimestamp();
                        NotifyFailure(registration, exception);
                        excludedObserverElapsed += clock.GetElapsedTime(
                            observerStarted,
                            clock.GetTimestamp());
                        if (Options.FailureMode == LuauScriptPhaseFailureMode.StopAndThrow)
                        {
                            ExceptionDispatchInfo.Capture(exception).Throw();
                        }
                    }
                }
            }
            finally
            {
                try
                {
                    lock (gate)
                    {
                        dispatching = false;
                        ApplyDeferredMutations();
                        CompactDisposedRegistrations();
                        if (disposeRequested)
                        {
                            DisposeAllImmediately();
                        }
                    }
                }
                finally
                {
                    Scheduler.EndDispatch(this);
                }
            }

            var elapsed = ElapsedSince(started, TimeSpan.Zero);
            budgetExhausted |= ElapsedSince(started, excludedObserverElapsed) >=
                Options.AggregateWallClockBudget;
            return new LuauScriptDispatchResult(
                attempted,
                succeeded,
                failed,
                skipped,
                elapsed,
                budgetExhausted);
        }

        bool IsEnabledForCurrentDispatch(LuauScriptRegistration registration)
        {
            lock (gate)
            {
                return !registration.IsDisposedApplied && registration.EnabledApplied;
            }
        }

        void DisableBeforeFailureCallback(LuauScriptRegistration registration)
        {
            lock (gate)
            {
                registration.SetEnabledImmediately(false);
                registration.ClearPendingEnabled();
            }
        }

        void NotifyFailure(
            LuauScriptRegistration registration,
            Exception exception)
        {
            try
            {
                Options.FailureCallback?.Invoke(registration, exception);
            }
            catch
            {
                // Failure observers must not weaken DisableAndContinue isolation
                // or replace the original exception selected by StopAndThrow.
            }
        }

        void PruneDisposedRegistration(LuauScriptRegistration registration)
        {
            lock (gate)
            {
                registration.DisposeImmediately();
                orderDirty = true;
            }
        }

        TimeSpan ElapsedSince(long started, TimeSpan excludedElapsed)
        {
            var elapsed = clock.GetElapsedTime(started, clock.GetTimestamp()) - excludedElapsed;
            return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
        }

        void EnsureOrderedSnapshot()
        {
            if (!orderDirty)
            {
                return;
            }

            registrations.RemoveAll(registration => registration.IsDisposedApplied);
            orderedRegistrations = registrations.ToArray();
            Array.Sort(orderedRegistrations, CompareRegistrations);
            orderDirty = false;
        }

        static int CompareRegistrations(
            LuauScriptRegistration left,
            LuauScriptRegistration right)
        {
            var orderComparison = left.Order.CompareTo(right.Order);
            return orderComparison != 0
                ? orderComparison
                : left.Sequence.CompareTo(right.Sequence);
        }

        void ApplyDeferredMutations()
        {
            for (var index = 0; index < registrations.Count; index++)
            {
                if (registrations[index].ApplyPendingMutation())
                {
                    orderDirty = true;
                }
            }
        }

        void CompactDisposedRegistrations()
        {
            if (!orderDirty)
            {
                return;
            }

            registrations.RemoveAll(registration => registration.IsDisposedApplied);
            InvalidateOrderedSnapshot();
        }

        void InvalidateOrderedSnapshot()
        {
            orderedRegistrations = Array.Empty<LuauScriptRegistration>();
            orderDirty = true;
        }

        void DisposeAllImmediately()
        {
            for (var index = 0; index < registrations.Count; index++)
            {
                registrations[index].DisposeImmediately();
            }

            registrations.Clear();
            orderedRegistrations = Array.Empty<LuauScriptRegistration>();
            orderDirty = false;
            disposed = true;
        }

        void ThrowIfUnavailable()
        {
            Scheduler.ThrowIfDisposed();
            if (disposed || disposeRequested)
            {
                throw new ObjectDisposedException(nameof(LuauScriptPhase));
            }
        }

        enum DispatchArgumentKind
        {
            None,
            One,
            Span,
        }
    }

    /// <summary>
    /// A disposable registration token controlling whether one entrypoint
    /// participates in its phase.
    /// </summary>
    public sealed class LuauScriptRegistration : IDisposable
    {
        const int NoPendingEnabledValue = -1;

        readonly LuauScriptPhase phase;
        int enabledState = 1;
        int pendingEnabledState = NoPendingEnabledValue;
        bool disposeRequested;
        bool disposed;

        internal LuauScriptRegistration(
            LuauScriptPhase phase,
            LuauScriptEntrypoint entrypoint,
            int order,
            long sequence)
        {
            this.phase = phase;
            Entrypoint = entrypoint;
            Order = order;
            Sequence = sequence;
            ProfilerMarker = new ProfilerMarker(entrypoint.OperationLabel);
        }

        /// <summary>Gets the registered caller-owned entrypoint.</summary>
        public LuauScriptEntrypoint Entrypoint { get; }

        /// <summary>Gets the stable dispatch order supplied at registration.</summary>
        public int Order { get; }

        /// <summary>Gets or sets whether this registration is admitted by later dispatches.</summary>
        public bool IsEnabled
        {
            get => phase.GetEnabled(this);
            set => phase.SetEnabled(this, value);
        }

        /// <summary>Gets whether this registration token has been disposed.</summary>
        public bool IsDisposed => phase.GetDisposed(this);

        internal long Sequence { get; }
        internal ProfilerMarker ProfilerMarker { get; }
        internal bool EnabledApplied => enabledState != 0;
        internal bool IsDisposedApplied => disposed;
        internal bool IsDisposeRequested => disposeRequested || disposed;

        /// <summary>Removes this registration without disposing its entrypoint.</summary>
        public void Dispose()
        {
            phase.DisposeRegistration(this);
        }

        internal bool GetRequestedEnabled()
        {
            if (disposeRequested || disposed)
            {
                return false;
            }

            return pendingEnabledState == NoPendingEnabledValue
                ? enabledState != 0
                : pendingEnabledState != 0;
        }

        internal void SetPendingEnabled(bool enabled)
        {
            pendingEnabledState = enabled ? 1 : 0;
        }

        internal void SetEnabledImmediately(bool enabled)
        {
            enabledState = enabled ? 1 : 0;
        }

        internal void ClearPendingEnabled()
        {
            pendingEnabledState = NoPendingEnabledValue;
        }

        internal void RequestPendingDispose()
        {
            disposeRequested = true;
            pendingEnabledState = NoPendingEnabledValue;
        }

        internal void DisposeImmediately()
        {
            disposeRequested = true;
            disposed = true;
            enabledState = 0;
            pendingEnabledState = NoPendingEnabledValue;
        }

        internal bool ApplyPendingMutation()
        {
            if (disposeRequested)
            {
                var changed = !disposed;
                DisposeImmediately();
                return changed;
            }

            if (pendingEnabledState != NoPendingEnabledValue)
            {
                enabledState = pendingEnabledState;
                pendingEnabledState = NoPendingEnabledValue;
            }

            return false;
        }

        internal void ThrowIfDisposeRequested()
        {
            if (disposeRequested || disposed)
            {
                throw new ObjectDisposedException(nameof(LuauScriptRegistration));
            }
        }
    }

    internal interface ILuauScriptSchedulerClock
    {
        long GetTimestamp();

        TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp);
    }

    internal sealed class StopwatchLuauScriptSchedulerClock : ILuauScriptSchedulerClock
    {
        internal static StopwatchLuauScriptSchedulerClock Instance { get; } =
            new StopwatchLuauScriptSchedulerClock();

        StopwatchLuauScriptSchedulerClock()
        {
        }

        public long GetTimestamp()
        {
            return Stopwatch.GetTimestamp();
        }

        public TimeSpan GetElapsedTime(long startTimestamp, long endTimestamp)
        {
            var timestampDelta = Math.Max(0L, endTimestamp - startTimestamp);
            return TimeSpan.FromSeconds((double)timestampDelta / Stopwatch.Frequency);
        }
    }
}
