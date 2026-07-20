# Resource limits

Every default in this package is finite. A script can't allocate unbounded
memory, run forever, spam the console, or hand you a hundred-megabyte string,
because there is a ceiling on each of those and the ceiling is on by default.

You will mostly interact with limits in two situations: something legitimate hit
a ceiling and you need to raise it, or you're accepting untrusted content and
want to lower one.

> **The one trap:** assigning a new options object replaces the *entire* policy,
> not just the field you care about. Start from an existing policy and change
> what you need:
>
> ```csharp
> StateOptions = LuauStateOptions.Default with { MemoryLimitBytes = 32 * 1024 * 1024 }
> ```
>
> These are records, so `with` keeps every limit you didn't mention. Nested
> policies work the same way:
>
> ```csharp
> StateOptions = LuauStateOptions.Default with
> {
>     DefaultExecutionOptions = LuauExecutionOptions.Default with
>     {
>         WallClockLimit = TimeSpan.FromMilliseconds(5),
>     },
> }
> ```
>
> Anything named "unbounded" is named that way on purpose and is for trusted
> content only.

## Per-root limits

`LuauStateOptions.Default` caps:

- native VM memory;
- the size of source and bytecode you're allowed to load;
- decoded result size, both per string and in total;
- how many object capabilities can be live at once;
- module dependency depth and how many module results stay cached;
- execution duration, interrupt count, and result count.

The VM memory cap covers allocations the VM makes. It does not cover allocations
your own callbacks make, or anything you allocate around the VM — if a script can
call a C# function that builds a big list, that list is on you.

`LuauStateOptions.UnboundedResources` deliberately removes the optional ceilings.
It still bounds diagnostic decoding, and it still leaves persistent bytecode set
to `Reject`. Per-operation options can tighten a root's limits but never loosen
them, and never swap the root's scheduler.

## Module and bundle limits

`LuauModuleLimits.Default` separately caps how many modules a map or bundle may
contain, total admitted source, module ID length, compiled bytecode per module,
and total bundle bytecode. `LuauModuleLimits.UnsafeUnbounded` is the explicit
opt-out for trusted content — the name is a warning. Cache count and dependency
depth still come from `LuauStateOptions` either way.

## Compiler queue limits

The shared compile queue caps per-request source size, per-result bytecode size,
in-flight requests, total queued source, worker count, and shutdown time.

`LuauCompilationLimitException` means the queue said no — you hit a limit. That's
distinct from a compile *error* in the script, from cancellation, and from
infrastructure failure, so you can tell "this player's file is too big" apart
from "this player's file has a typo."

## Editor import limit

`LuauAssetImportSettings.MaxImportedSourceBytes` defaults to 1 MiB and lives
under **Project Settings > Luau.Unity**. The importer checks the file length
before allocating anything, reads once, validates UTF-8, and compiles and stores
exactly the bytes it admitted.

This one protects the Editor while you're authoring. It has nothing to do with
runtime mods — those go through the download, archive, module, compiler-queue,
and state limits instead.

## Logging

The default `print` binding caps arguments per call, UTF-8 bytes per message, and
messages per second, so a script in a tight loop can't drown your Console or your
log file. Tune them on `LuauUnityOptions` (`MaxPrintArguments`,
`MaxPrintUtf8Bytes`, `MaxPrintMessagesPerSecond`).

If you replace `print` or add your own logging function, rate-limit it yourself.
Nothing does that for you.

Diagnostic decoding has its own separate budget and truncates only on valid UTF-8
boundaries, so a truncated error message is still a valid string.

## A worked example

Tightening everything at once, for a root that will run mod content:

```csharp
using var state = LuauUnity.CreateState(new LuauUnityOptions
{
    StateOptions = LuauStateOptions.Default with
    {
        MemoryLimitBytes = 16 * 1024 * 1024,
        MaxSourceBytes = 1024 * 1024,
        MaxBytecodeBytes = 1024 * 1024,
        MaxManagedHandleCount = 256,
        BytecodePolicy = LuauBytecodePolicy.Reject,
        DefaultExecutionOptions = LuauExecutionOptions.Default with
        {
            WallClockLimit = TimeSpan.FromMilliseconds(50),
            InterruptCountLimit = 10_000,
            MaxResultCount = 64,
        },
    },
    MaxPrintMessagesPerSecond = 20,
});
```

These numbers are illustrative, not recommendations — a 50 ms wall clock is
tight enough to kill legitimate work in some games and far too loose in others.
The point is the shape: start from `Default`, use `with`, and keep
`BytecodePolicy.Reject` for anything a player supplied.

## Checklist for untrusted content

- Accept source, never arbitrary bytecode.
- Set finite source, bytecode, memory, execution, and result limits.
- Expose the smallest host API that works.
- Leave OS access, the debug library, and `require()` off unless you're
  deliberately granting them.
- Register host APIs before the root is sandboxed — afterward is too late.
- Keep a cancellation path you control.
- Bound and rate-limit logging.
- Give each mod its own root.

## When something fails

Failures surface as typed managed exceptions, so you can tell a limit from a
syntax error from an infrastructure problem. The shared operation engine restores
its stack boundary and leaves the root usable whenever that's safe; when a
terminal reset itself fails, the root is poisoned and disposed rather than left
in an unknown state. Failure precedence is hard stop, then managed callback
failure, then allocator or native failure.

## Raising a limit

Measure first, with both realistic content and deliberately hostile content. A
ceiling that's comfortable for your own scripts may be exactly the thing standing
between a player and an out-of-memory crash on someone's phone.

And when content shouldn't share memory, module cache, or host APIs with other
content, the answer is a separate root — not a bigger budget.
