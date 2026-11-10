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
- module dependency depth, cached result count, and retained cache string bytes;
- execution duration, interrupt count, and result count.

The VM memory cap covers allocations the VM makes. It does not cover allocations
your own callbacks make, or anything you allocate around the VM — if a script can
call a C# function that builds a big list, that list is on you.

One ceiling is fixed by the VM rather than by these options: a single Luau stack
frame holds at most 8000 values. That count includes a called function, its
arguments, and one slot the runtime keeps free to read results. A script that
returns or yields that many values, a host call that passes that many
arguments, or a callback that returns that many fails with
`LuauStackLimitException`. Under the default `MaxResultCount` of 64, an
oversized return fails earlier with `LuauResultLimitException`. Either way, the
values are dropped and the root stays usable. Removing the result limit does not
remove this ceiling. The one exception is what a function returns to
`InvokeVoid`: those values are discarded without being read, so the call
succeeds however many there are.

`LuauStateOptions.UnboundedResources` deliberately removes the optional ceilings.
It still bounds diagnostic decoding, and it still leaves persistent bytecode set
to `Reject`. Per-operation options can tighten a root's limits but never loosen
them, and never swap the root's scheduler.

## Execution budgets

`LuauExecutionOptions.Default`, which `LuauStateOptions.Default` uses, gives
every operation:

| Limit | Default | When it is reached |
| --- | --- | --- |
| `WallClockLimit` | 250 ms | `LuauExecutionBudgetException`, `BudgetKind == WallClock` |
| `InterruptCountLimit` | 10,000,000 safepoints | `LuauExecutionBudgetException`, `BudgetKind == InterruptCount` |
| `MaxResultCount` | 64 values | `LuauResultLimitException` |

`MaxResultCount` bounds the values a call hands to the host. `InvokeVoid` and
`InvokeVoidAsync` hand none: they discard whatever the function returns, so a
hook may return values under any result limit, including 0. The limit still
applies to the results that work inside such a call delivers to the host, such
as the module a `require` loads.

Whichever limit an operation reaches first stops it, so handle both kinds of
`LuauExecutionBudgetException` wherever you report a script that ran too long.
On desktop hardware a tight loop of Luau code that never ends reaches the count
first, after roughly 35 to 70 ms. Loops that do more work in each iteration,
scripts that spend their time in host functions, and slower devices reach the
wall clock first.

`InterruptCountLimit` counts interrupt safepoints. Luau code reaches one when
it calls a function, when a Luau function returns, on each loop iteration, at
each step of a Lua pattern match, and periodically inside the few library
calls that can run long ([below](#long-library-calls)). A return from a host function is not a
safepoint, and neither is a call to a built-in function that the VM runs
inline. A limit of N lets exactly N safepoints run and stops the script at
safepoint N + 1, reported as
`LuauExecutionBudgetException` with `ObservedInterruptCount == N + 1`. Modules
loaded by `require()` count toward the operation that required them.

The native host keeps that count and checks the wall clock without calling
back into managed code, so budgets cost a few nanoseconds per safepoint. The
clock is read every few safepoints, adapting so reads stay about
20 microseconds apart: a `WallClockLimit` is detected within about
20 microseconds of the deadline while the script's pace is steady. If the
script abruptly slows down, for example from a tight loop into calls of an
expensive host function, detection can trail by up to 128 of the new, slower
safepoint gaps once, and never later than a shared deadline watchdog notices
the deadline and asks for a poll at the next safepoint. The watchdog is one
timer for the whole process, armed for the earliest running operation's
deadline; it fires within the system timer's resolution of that deadline
(typically about 1 ms, and up to about 16 ms with Windows' default timer
resolution). Cancellation, disposal, and managed callback failures are
observed at the very next safepoint.

### Long library calls

A few standard library functions can do a great deal of work in one call.
Those reach safepoints of their own while they work, so the wall clock, the
count limit, cancellation, and disposal stop them inside the call:

- the plain-text search of `string.find` (a true fourth argument, or a
  pattern without special characters) and `string.split`. Their time grows
  with the length of the string searched, not with the product of the two
  lengths: a search that compared a 200,000-byte needle at every position of a
  400,000-byte string took a second, and now takes under a millisecond.
  `string.find` also reaches safepoints while it checks a long pattern for
  special characters, which it does once, after it has checked `init`;
- `table.sort` with the default comparison or a built-in or C# comparison
  function. Sorting strings that share a long prefix compares that prefix
  every time, which made one call run for minutes;
- `table.concat`, which spends about a fifth of a second turning a million
  numbers into strings, and `utf8.len`.

Pattern matching (`string.find` with a pattern, `string.match`,
`string.gmatch`, and `string.gsub`) reaches a safepoint at each matching step,
as it always has. A comparison function, `__lt` metamethod, or replacement
function written in Luau reaches safepoints of its own.

Each of these functions reaches a safepoint whenever its work since the last
one adds up to a fixed budget: 16,384 bytes examined one at a time (a
pattern checked for special characters, UTF-8 decoded, a needle compared),
262,144 bytes scanned, compared, or copied in bulk, or 1,024 steps (a search
candidate, a sort comparison, a concatenated value or separator, a split
piece), in any mix. Between two safepoints it does at most about twice that
budget, plus at most one operation of the VM's own that it cannot divide:
comparing two strings in a sort, creating one string (a piece of
`string.split` or the result of `table.concat`), one insertion that grows the
table `string.split` returns, or one append to `table.concat`'s buffer,
including the buffer's growth. A sort comparison that calls a built-in or C#
comparison function, or a `__lt` metamethod that is not a Luau function, is
followed by a safepoint of its own, because the sort cannot tell how long the
function ran.

These safepoints count toward `InterruptCountLimit` like pattern-matching
steps do: one for each budget of work, and one after each call of a built-in
or C# comparison function or of a `__lt` that is not a Luau function, just as
a Luau comparison function's return counts one. A long call therefore uses
only a few thousand of the default 10,000,000, and a limit you set for a
phase still stops it; a sort with a comparison function uses at least one per
comparison, as it always did with a Luau comparison function.

Libraries are usually opened before any script runs. Opening the string,
table, or UTF-8 library after scripts have run throws `LuauException`, and changes nothing, when
registering its functions could run a script's `__newindex` metamethod: when
the library table has a metatable with `__newindex`, or when a new key would
be stored in a globals table whose metatable has `__newindex` (the library
table itself, if a script removed it, or the global `unpack` that the table
library also sets). Such a metamethod would otherwise be handed the library's
original functions, which reach no safepoints. A metatable without
`__newindex` does not prevent the open.

Any other call is observed only when it returns. The longest single calls left,
measured on a desktop x64 machine with inputs that fit the default 64 MB
memory limit, are:

| Call | Longest time between safepoints |
| --- | --- |
| One pattern-matching step over a 20 MB run of literal characters (`string.match`, `string.gmatch`, `string.gsub`, or `string.find` with a pattern) | 80 ms |
| `string.reverse` of a 30 MB string | 57 ms |
| `string.format("%q", s)` of a 20 MB string | 41 ms |
| `string.pack("c31457280", s)` | 41 ms |
| `utf8.offset` across a 60 MB string | 40 ms |
| `string.rep` producing 60 MB | 37 ms |
| `string.lower` or `string.upper` of a 30 MB string | 34 ms |
| `table.create(4000000, v)`, `buffer.create` of 60 MB | 26 ms |
| `tonumber` of a 30 MB string | 22 ms |
| `string.split` creating one 30 MB piece | 18 ms |
| `buffer.tostring` of a 30 MB buffer | 18 ms |
| `table.find` across 4 million entries | 15 ms |
| `table.clone` of 2 million entries | 14 ms |
| `string.split` growing its result to 2 million pieces | 13 ms |
| `table.concat` appending one 15 MB string, or creating a 15 MB result | 9 ms |
| `string.find` with a 60 MB pattern whose only special character is its last: Luau checks the pattern again itself before it matches | 6 ms |

Every other library call takes under 10 ms. A string comparison in a sort
takes about 1 ms per 12 MB the two strings share. Expect several times these
figures on a standalone VR headset or a phone, and less with a lower
`MemoryLimitBytes`, since every one of them grows with the size of its
inputs. Any allocation can also run a step of the garbage collector or grow
the VM's table of strings, whether a library call or Luau code makes it; in a
Luau loop that created a million strings, one such step took 26 ms.

The VM cannot stop your own C# functions while they run, and the count limit
does not count the work they do. A callback that can run long must stop
itself: check `LuauCallContext.CancellationToken` (or a generated member's
`CancellationToken` parameter) in its loop and return when it is canceled.
Caller cancellation, the wall-clock deadline, and disposal all cancel that
token. Otherwise bound the work by the input size, or split it across calls.

The count limit is the bound that still works when time is not a useful
measure: a host that removes the wall clock, for example while a debugger is
attached so that a breakpoint does not use up the 250 ms, or a host that wants
the same verdict on every device. The default of 10,000,000 leaves
room for ordinary one-time work, such as a setup loop, filling a 400 x 400
grid, or sorting a few thousand entries with a comparison function. With no
wall clock, it stops a runaway loop of Luau code in about 35 to 70 ms on a
desktop x64 machine, and proportionally longer on slower hardware such as a
standalone VR headset. It cannot bound host code: a loop that calls a C#
function spends about 80 ns per safepoint on the same desktop, so it runs for
most of a second before the count stops it, and a slower host function
stretches that further.

Set your own count limit when:

- you remove or lengthen the wall-clock limit, because the count is then the
  only bound on a runaway script; size it to the work you expect, and lower it
  further if scripts call host functions in loops;
- a call runs every frame, such as a lifecycle hook: give it small wall-clock
  and count limits of its own, as the
  [script instance](script-instances.md#bounded-synchronous-phases) example
  does. The defaults are sized for one-time work, and
  `LuauScriptPhaseOptions.InvocationOptions` starts from them;
- you need a limit that does not depend on how fast the device is, for example
  to give mod content the same budget on every platform.

## Module and bundle limits

`LuauModuleLimits.Default` separately caps how many modules a map or bundle may
contain, total admitted source, module ID length, compiled bytecode per module,
and total bundle bytecode. `LuauModuleLimits.UnsafeUnbounded` is the explicit
opt-out for trusted content — the name is a warning. Cache count and dependency
depth still come from `LuauStateOptions` either way.

`LuauStateOptions.MaxCachedModuleBytes` also limits retained cache keys and
string-valued module results to **16 MiB per root** by default. It counts their
UTF-16 character payload (two bytes per character), including resolver-qualified
keys, across all maps, bundles, and execution operations on that root. Cache hits
do not consume additional budget. A publication that would exceed the limit fails
with `LuauModuleLimitException` and `LimitKind == CachedResultBytes`; the result
is not cached, and previously cached modules remain usable.

This managed budget is separate from `MemoryLimitBytes`. Tables, functions, and
other referenced Luau values keep their contents in native VM memory. Managed
object headers and dictionary overhead are bounded by the cache count limit,
rather than included in the byte calculation. Transient decoding still uses the
per-string and per-operation limits. Keep both cache limits finite for untrusted
modules; `UnboundedResources` explicitly removes both.

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
The byte cap includes UTF-8 replacement characters produced by malformed input
and the truncation marker. Later arguments are skipped once a value is truncated.

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
