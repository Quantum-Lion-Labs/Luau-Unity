# Script instances and host archetypes

`LuauScriptInstance` is the reusable unit for a script that has behavior rather
than a one-time return value. It owns one sandboxed child thread and requires
the loaded chunk to return exactly one table. Functions in that table become
named `LuauScriptEntrypoint` values:

```luau
return {
    start = function()
        -- optional host-defined startup hook
    end,

    update = function(deltaTime)
        -- required only if the host's archetype says so
    end,
}
```

```csharp
using var instance = await root.CreateScriptInstanceAsync(
    asset,
    thread =>
    {
        using var self = root.CreateHandle(
            gameObject,
            LuauUnityCapabilities.GameObjectDescriptor);
        thread["self"] = self;
    },
    cancellationToken);

if (instance.TryGetEntrypoint("start", out var start))
    await start.InvokeVoidAsync(cancellationToken);

var update = instance.GetRequiredEntrypoint("update");
```

The asset factory compiles source through Unity's bounded shared compilation
lane. Configuration runs before the script, so the script sees `self`, while a
different instance can receive a different capability even when both share a
root. A missing optional export is `false`; a missing required export or a
non-function export is a script contract error. Repeated binding of the same
name returns the same cached entrypoint.

`LuauUnityCapabilities.GameObjectDescriptor` above is sample code, not package
code — it arrives when you import **Full Luau Scripting Demo**, and it's yours
to edit. See [object capabilities](capability-bindings.md#object-capabilities)
for the alternatives.

## Ownership

The application owns the root, scheduler, and instances separately:

1. Create and configure a trust-domain root.
2. Create a scheduler for that root and its named phases.
3. Create instances and retain their entrypoints.
4. Dispose registration tokens before their instances.
5. Dispose instances before the scheduler and root.

Disposing a scheduler releases its registrations, not the instances. Disposing
an instance invalidates all of its entrypoints and releases the export table and
child thread. The root stays caller-owned. A phase automatically prunes an
instance that was disposed before its registration token.

## Bounded synchronous phases

`LuauScriptScheduler` is lifecycle-agnostic. A host can call a phase from Unity
`Update`, a fixed-step simulation loop, a render hook, or a named application
event. Each phase requires finite per-invocation limits and a positive aggregate
wall-clock budget:

```csharp
using var scheduler = new LuauScriptScheduler(root);
var updatePhase = scheduler.CreatePhase(
    "presentation-update",
    new LuauScriptPhaseOptions
    {
        InvocationOptions = LuauExecutionOptions.Default with
        {
            WallClockLimit = TimeSpan.FromMilliseconds(2),
            InterruptCountLimit = 10_000,
            MaxResultCount = 0,
        },
        AggregateWallClockBudget = TimeSpan.FromMilliseconds(4),
        FailureMode = LuauScriptPhaseFailureMode.DisableAndContinue,
        FailureCallback = (registration, exception) =>
            Debug.LogException(exception),
    });

using var registration = updatePhase.Register(update, order: 0);
var dispatch = updatePhase.Dispatch((LuauValue)(double)Time.deltaTime);
```

Dispatch is synchronous and non-yielding. Async entrypoints remain useful for
loading and startup outside a phase. Registrations run by ascending `order` and
then registration sequence. Registering, disposing, enabling, or disabling a
token during dispatch takes effect on the next dispatch. Re-entrant or
overlapping dispatch is rejected.

The aggregate budget is an admission budget: once exhausted, the phase starts
no more calls and leaves uncalled registrations enabled for the next dispatch.
One already-admitted call can overshoot the aggregate budget, bounded by that
call's hard wall-clock limit. `LuauScriptDispatchResult` reports attempted,
succeeded, failed, skipped, elapsed, and budget-exhausted state without owning
resources. Failure-observer callback time is excluded from the aggregate budget
used for script admission, so an observer cannot consume another script's
budget or weaken the one-call VM overshoot bound. Reported `Elapsed` is actual
wall time and includes observers, making slow host callbacks visible to metrics.

`DisableAndContinue` is appropriate for independent Unity behaviours. It
disables a failed registration before notifying the failure callback and lets
later behaviours run. `StopAndThrow` leaves fail-fast policy to a gamemode or
other coordinator whose state cannot remain coherent after one hook fails.
Failure callbacks are observers: exceptions they throw are contained so they
cannot stop a continue phase or replace the original fail-fast exception.

## Composing application archetypes

The VM does not assign meaning to export names. Keep that policy in small host
wrappers:

- A gamemode can require `initialize`, `tick`, and `shutdown`, choose
  `StopAndThrow`, and expose match services as capabilities.
- A networked-behaviour wrapper can require `simulate` and optional snapshot
  hooks, pass an explicit entity capability, and register with a deterministic
  fixed-step phase.
- An ordinary Unity behaviour can use optional `start` and `destroy` plus
  required `update`, with `DisableAndContinue` isolation.

This composition keeps capability sets separate even when archetypes share a
root. Do not share a root across mutually untrusted publishers: threads isolate
writable globals, but the root still shares host APIs, memory, modules, and the
same failure domain. Use one root per trust domain, not one root per component.

Simulation ticks should not use Unity `Update`. Its cadence and `deltaTime` are
presentation-driven and are not deterministic across machines. Drive network
or authoritative simulation entrypoints from the application's fixed-step tick,
with explicit tick inputs, ordering, budgets, and rollback/snapshot policy.

Import **Full Luau Scripting Demo** for a worked version of all of this: explicit
`self` and named-reference injection, prefab spawning against a per-behaviour
cap, owned-instance cleanup, shared trust-domain state, and failure isolation
that disables one component instead of the scene. Keep its `Core/` and delete
`Demo Game/` to start another game from it.
