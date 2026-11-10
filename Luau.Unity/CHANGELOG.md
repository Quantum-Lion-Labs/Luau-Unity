# Changelog

All notable package changes are recorded here. The package follows semantic
versioning while in preview; preview releases may make explicitly documented
API or ABI breaks.

## [Unreleased]

### Added

- A Linux x64 native plugin for the Linux editor and Linux x64 players,
  including dedicated servers. It requires glibc 2.35 or newer (Ubuntu 22.04,
  Debian 12, or later) and has no other dynamic dependencies.

### Changed

- `InvokeVoid` and `InvokeVoidAsync`, on `LuauFunction` and
  `LuauScriptEntrypoint`, now discard the values the function returns, and
  the call succeeds. They used to run the function to completion, drop its
  results, and then throw `LuauResultLimitException` with a limit of 0.
  `LuauScriptScheduler` calls every hook this way, so a hook whose last
  statement was `return other()`, or that returned `true` out of habit, ran
  once and was then disabled by the default `DisableAndContinue` mode, with an
  error about a result limit that the script author could not relate to
  anything. In Luau, as in Lua, a caller that wants no results ignores them.
  `MaxResultCount` does not apply to these calls, because no value reaches
  the host: a function may return values under any limit, including the
  `MaxResultCount = 0` the samples' hook options set. The limit keeps its
  meaning for every call that delivers results (`Invoke`, `InvokeInto`,
  `DoString`, `Resume`, and the module a `require` loads, also during a void
  call). A module first required during a void call used to fail the same
  way, because the call's forced limit of 0 applied to it; it now gets the
  limit of the call's own options. The 8000-value frame ceiling does not
  apply to the discarded values either: it exists so the host can read
  results, and these calls read none. Errors, budgets, cancellation, and the
  rejection of a yield on the root are unchanged, the stack is left as it
  was, and a warmed synchronous call whose function returns values allocates
  nothing. To keep treating returned values as an error,
  call `InvokeInto` with an empty destination (`ArgumentException`) or
  `Invoke` with `MaxResultCount = 0` (`LuauResultLimitException`).
- `LuauExecutionOptions.Default`, and with it `LuauStateOptions.Default` and
  every state created without its own execution options, now allows
  10,000,000 interrupt safepoints per operation instead of 100,000. A
  function call, a return from a Luau function, and a loop iteration are each
  a safepoint, so the old limit stopped ordinary one-time work after a
  millisecond or two, long before the 250 ms
  wall clock: `local n = 0 for i = 1, 150000 do n = n + i end return n`,
  filling a 400 x 400 grid, and sorting 3,000 entities by distance with
  `table.sort` and a comparison function all failed with an `InterruptCount`
  `LuauExecutionBudgetException`. Under the defaults, whichever limit comes
  first now stops a script that runs too long. Measured on desktop x64, a
  tight loop of Luau code that never ends reaches the count after about 35 to
  70 ms, before the wall clock; loops that do more work in each iteration,
  slower devices, and a loop that calls a C# function (about 0.8 s to reach
  the count) reach the 250 ms wall clock first. A host that removes the wall
  clock keeps the count as its bound on a runaway script. The wall-clock and
  result-count defaults are unchanged. `LuauScriptPhaseOptions` starts from
  these defaults, so a phase that keeps its default `InvocationOptions` now
  allows each call the same 10,000,000 safepoints; give per-frame hooks their
  own limits, as the samples do. See
  [Resource limits](Documentation~/resource-limits.md#execution-budgets).
- Calling into the VM is about three times cheaper, and a steady stream of
  synchronous calls, such as per-frame script hooks, allocates no managed
  memory. Each call used to create an operation object, a timer, several
  cancellation token sources, a merged options record, and two execution
  context entries. A root now reuses one operation object for consecutive
  synchronous calls; a callback's `CancellationToken` is created only when the
  callback reads it; wall-clock limits share one watchdog timer for the whole
  process; merged execution options and repeated chunk names are reused; and
  arguments, results, and resume values cross with fewer native calls and
  locks. Measured on desktop x64 (.NET 9), per call of a function taking one
  number:

  | Call | Before | After |
  | --- | --- | --- |
  | `InvokeVoid`, `Default` options | 1,433 ns, 928 B | 459 ns, 0 B |
  | `InvokeVoid`, 2 ms wall-clock limit only | 1,489 ns, 928 B | 479 ns, 0 B |
  | `InvokeVoid`, `Unbounded` | 1,071 ns, 568 B | 385 ns, 0 B |
  | Script entrypoint `InvokeVoid`, reused phase options | 1,441 ns, 992 B | 465 ns, 0 B |
  | `InvokeInto`, one result | 1,885 ns, 864 B | 612 ns, 0 B |
  | `ResumeInto`, one value | 1,527 ns, 864 B | 581 ns, 0 B |
  | `ExecuteCompilerOutputInto`, named chunk | 1,661 ns, 1,008 B | 876 ns, 0 B |
  | Table read outside an operation | 1,607 ns, 864 B | 998 ns, 0 B |
  | `InvokeVoidAsync`, completing synchronously | 1,565 ns, 1,456 B | 825 ns, 936 B |

  Execution budgets, cancellation, disposal, and failure precedence behave as
  before, including for tokens a callback keeps after its call. The wall-clock
  backstop after an abrupt slowdown keeps its bound: the shared watchdog fires
  within the system timer's resolution of the earliest running deadline. One
  check is stricter: code forked from inside a synchronous callback, for
  example with `Task.Run`, can no longer use the root while that call runs. It
  used to pass the access check and then wait for the VM, which deadlocked if
  the callback waited for it.
- Script loops and calls run 6 to 13 times faster. Every call, return, and loop
  iteration is an interrupt safepoint, and each one used to call back into
  managed code even with no limit configured. The native plugin now keeps the
  interrupt count and checks the wall clock itself, and calls managed code
  only when a limit is reached or a cancellation, disposal, or callback result
  is pending. Measured on desktop x64 (.NET 9), per loop iteration, against
  about 4 ns for a plain loop and 14.9 ns for a loop of Luau calls in the
  stock Luau interpreter:

  | Execution options | Plain loop | Loop of calls |
  | --- | --- | --- |
  | `Unbounded` | 50.0 → 5.3 ns | 131.9 → 20.7 ns |
  | Wall-clock limit only | 68.5 → 5.4 ns | 226.8 → 21.6 ns |
  | Interrupt-count limit only | 45.5 → 5.2 ns | 129.5 → 20.2 ns |
  | `Default` (250 ms, 100,000) | 65.9 → 5.3 ns | 202.8 → 20.7 ns |

  Interrupt-count limits still stop at exactly safepoint N + 1 and count the
  same safepoints as before. Wall-clock limits are now detected within about
  20 microseconds of the deadline while a script's pace is steady, and at worst
  128 safepoints late after an abrupt slowdown; see
  [Resource limits](Documentation~/resource-limits.md#execution-budgets).
  Cancellation, disposal, and callback failures are still observed at the next
  safepoint. The ABI stays 2.0 and adds the required
  `LUAU_HOST_FEATURE_INTERRUPT_THROTTLE` capability with
  `luau_host_interrupt_configure`, `luau_host_interrupt_request`, and
  `luau_host_interrupt_count`, so this `Luau.dll` requires the matching
  rebuilt native plugins.
- **Breaking:** `int`, `uint`, `short`, `ushort`, `byte`, and `sbyte` values now
  cross into Luau as ordinary numbers instead of the distinct `integer` type.
  This applies to generated library and capability members, callback
  `Return`, implicit `LuauValue` conversions, and `LuauValue.CreateFrom`.
  Scripts can now compare these values with numeric literals, do arithmetic
  with them, and use them as table keys and array indices. `long` and `ulong`
  remain lossless Luau integers. Reading values into managed integral types is
  unchanged and accepts either kind, but `Read<object>()` on such a value now
  returns a boxed `double` instead of a boxed `long`.
- Update the embedded Luau VM and compiler from 0.729 to 0.745. The compiler now
  targets bytecode version 13. Persisted bytecode artifacts carry the host
  fingerprint, so artifacts built by earlier versions are rejected and must be
  rebuilt; Unity reimports `.luau` assets and regenerates first-party manifests
  automatically because the compiler identity changed.

### Fixed

- Starting a new call on a child thread that a script had left suspended ran
  the script's pending code instead. After
  `thread.DoString("coroutine.yield(1) return 'A'")`, a later
  `thread.DoString("return 'B'")` returned `'A'`, never ran `return 'B'`, and
  raised no error, so a script that chose when to yield chose what the host's
  next call on that thread did. On a coroutine created with `coroutine.create`
  that had not started, the new chunk ran above the pending function, and a
  failure discarded that function. `DoString`, `ExecuteCompilerOutput`,
  `ExecuteVerifiedBytecode` (synchronous, asynchronous, and `Into` forms),
  the `LuauAsset` execution helpers, and script-instance loaders now throw
  `InvalidOperationException` on such a thread and leave it unchanged, so
  `Resume` continues it as before. `SandboxThread` and module execution
  reject a suspended thread the same way; `SandboxThread` used to install the
  thread's sandbox and then fail with a generic native error. A finished or
  failed thread still accepts new calls, and a thread waiting on an
  asynchronous callback still reports that the VM is executing.
- The Windows plugin no longer depends on the Visual C++ runtime. It imported
  `MSVCP140.dll`, `VCRUNTIME140.dll`, `VCRUNTIME140_1.dll`, and the Universal
  CRT's `api-ms-win-crt-*.dll` libraries, which a Unity Windows player does not
  ship, so on a machine without the Visual C++ redistributable the plugin
  failed to load and every Luau call threw `DllNotFoundException`. The plugin
  now links the C and C++ runtimes statically and imports only `KERNEL32.dll`,
  so it needs no redistributable. The package's validation rejects a Windows
  plugin that imports, delay-loads, or forwards an export to any other DLL.
  The Windows plugin is about 600 KB larger.
- The Android plugins no longer depend on `libc++_shared.so`. A Unity Android
  player contains that library only when something else ships it, in practice
  the GameActivity application entry point, so a project that used the classic
  Activity entry point built successfully and then failed with
  `DllNotFoundException` at the first Luau call. The plugins now link the C++
  runtime statically and need only the Android system libraries `libc.so`,
  `libm.so`, and `libdl.so`, so they work with both the Activity and the
  GameActivity entry points. Each Android plugin is about 200 KB larger.
- A script that returned more values than one Luau stack frame holds (8000)
  could corrupt native memory and crash the process. For example,
  `local t = table.create(7999, 1) return 1, 2, 3, 4, table.unpack(t)` made every
  later call on that root fail with `OutOfMemoryException` and, after about 40
  calls, aborted the process. The native host now reserves the stack slots each
  operation and its error object need before it runs, and refuses the operation
  without touching the stack when it cannot. Returning, yielding, or passing
  that many values now fails with the new `LuauStackLimitException` (or
  `LuauResultLimitException` under the default `MaxResultCount`), the values are
  dropped, and the root stays usable. A managed callback that fails reports its
  own failure, which Luau `pcall` can catch even when the callback's arguments
  or results filled its frame.
- A script can no longer hold the host thread far past its wall-clock limit
  with `table.move` over a huge, mostly empty range. On 0.729,
  `table.move({}, 1, 0x7FFFFFFE, 2, {})` ran for about 30 seconds under a
  250 ms limit; Luau 0.743 and later move only the elements that exist.
- No budget, cancellation, or disposal could stop a single standard library
  call that did a great deal of work, because the call reached no interrupt
  safepoint until it returned. The plain-text search behind `string.find`
  (a true fourth argument, or a pattern without special characters) and
  `string.split` compared the needle at every position, so
  `string.find(string.rep("a", 400000), string.rep("a", 199999) .. "b", 1, true)`
  ran for a second under a 250 ms limit, and strings that fit the default
  64 MB memory limit could keep one call running for hours. `table.sort` of
  4 million numbers took half a second, and of strings that share a long
  prefix as long as minutes; `table.concat` of a million numbers and
  `utf8.len` of a 60 MB string took 110 to 180 ms. The native host now replaces
  these functions with versions that reach a safepoint whenever their work
  since the last one adds up to a fixed budget (16 KB examined byte by byte,
  256 KB scanned or copied in bulk, or 1,024 comparisons, values, or pieces),
  so between two safepoints they do at most twice that budget plus one
  indivisible operation of the VM, such as creating one string or comparing
  two. The wall-clock and count limits, cancellation, and disposal therefore
  stop them inside the call, on any thread, inside `pcall`, and in a module or
  metamethod a host callback runs. The plain-text search is now linear in the
  length of the string searched: the search above takes under a millisecond.
  `string.find` also checks a long pattern for special characters with
  safepoints, once, and only after it has checked `init`. Results, errors, and
  the order of sort comparisons are exactly upstream's, and ordinary calls
  take the same time as before. The safepoints count toward
  `InterruptCountLimit` like pattern-matching steps: one per budget of work,
  and one after each call of a built-in or C# sort comparison function or of
  a `__lt` metamethod that is not a Luau function, as a Luau comparison
  function's return always counted one. Opening the string, table, or UTF-8
  library after scripts have run throws `LuauException`, and changes nothing,
  when registering it could run a script's `__newindex` metamethod, which
  would otherwise be handed the original functions. See
  [Resource limits](Documentation~/resource-limits.md#long-library-calls) for
  the exact budget, the longest single calls that remain (up to 80 ms for one
  pattern-matching step over a 20 MB literal run on desktop), and what a long
  C# callback must do.
- Disposing a root from another thread just as a host operation began (for
  example a global or table access) could strand that operation, so the
  native VM was never closed and one callback registration leaked. The
  operation now ends even when its setup fails.
- An ordinary host call on a thread a script had left suspended could crash
  the process. After `thread.DoString("coroutine.yield(1) return 'A'")` on a
  sandboxed thread, `thread["x"] = 5` threw `InvalidOperationException` ("did
  not consume its managed stack boundary") and the next `thread.Resume()`
  crashed with a segmentation fault: the sandbox's write guard is Luau code,
  and Luau cannot run code on a thread that has yielded, is suspended by a
  pending async callback, or ended with an error. The same happened when a
  global read reached a script's `__index` function, when an async host
  function formatted an argument with a `__tostring` metamethod
  (`LuauCallContext.ToDisplayString`), when an async callback on a root call
  indexed a table argument with `__index` or `__newindex`, and when
  `LoadCompilerOutput` or `LoadVerifiedBytecode` resolved an import such as
  `a.b` through an `__index` function on such a thread. These operations now
  run that code on another thread of the same root and work whatever the
  script is doing: globals of a yielded or suspended thread can be read and
  written, async callbacks can format and index their arguments, and bytecode
  loads on a suspended thread with that thread's globals. The suspended
  thread's continuation is untouched, so `Resume` returns what it would have
  returned. A metamethod run this way sees the host's thread in
  `coroutine.running()` and cannot yield, as for any direct host operation.
- Luau code that an async callback's own host calls ran, for example a
  `__tostring` or `__index` reached while the callback formatted or indexed an
  argument, failed with "not enough memory" at its first interrupt check,
  even on a thread that could run it. The interrupt poll still reported the
  callback's own pending suspension as new work.
- `SandboxThread` on a coroutine that had ended with an error replaced the
  thread's globals and then failed with a generic native error, leaving a
  half-installed sandbox without its write guard. It now throws
  `InvalidOperationException` before changing anything.
- The Full Luau Scripting Demo `Core/` granted scripts more than its README
  said, in three places:
  - A physics callback handed the other object over with the full
    `GameObject` capability, so a script could rename, move, reconfigure
    through `GetComponent`, or deactivate any object it collided with or
    whose trigger it entered, even one it was never given. The other object
    is now a full capability only when it is the behaviour's own GameObject,
    the GameObject of one of its Object References, or an object it spawned.
    Anything else arrives as a read-only `GameObjectView` (`name`, `tag`,
    `layer`, `activeSelf`, `activeInHierarchy`, `CompareTag`, and a
    `transform` with `position` and `rotation`), and setting any of its
    properties fails with `Cannot set 'name': GameObject 'Pipe' was not
    granted to this script. ...`. Both views are in `LuauUnityCapabilities`.
  - Nothing bounded spawning across a runtime. Each behaviour's
    **Max Spawned Objects** is set by whoever authors the scene or prefab, and
    a spawned object can carry a behaviour of its own: with every behaviour
    limited to two, a scene object listed as its own prefab produced 14 live
    copies. `LuauBehaviourRuntime` now has its own **Max Spawned Objects**
    (64 by default), which counts every live object spawned by any behaviour
    on the runtime, including behaviours on spawned objects; a spawn must pass
    both limits. A prefab that carries its own `LuauBehaviourRuntime` started
    a new runtime with fresh limits for each copy (one admitted spawn produced
    7 runtimes), so a behaviour now refuses to start with such a prefab
    reference.
  - The `Input` library read any key, every mouse button, and touch, so any
    script could poll the whole keyboard every frame. Each runtime now opens
    `Input` with its own serialized **Input Policy**: the key names, mouse
    buttons, and touch its scripts may read. The default is what the demo game
    reads: `Space`, the left mouse button, and touch. Reading anything else
    fails with, for example, `Input key 'A' is not enabled for this Luau
    runtime. ...`, instead of returning `false`.

### Compatibility

- Update the managed DLL and all bundled native plugins together. The host
  fingerprint changed with the Luau update, so a mismatched pair fails the
  startup ABI check.
- The native host ABI adds `LUAU_HOST_STATUS_STACK_LIMIT`,
  `LUAU_HOST_FRAME_SLOT_LIMIT`, and the required
  `LUAU_HOST_FEATURE_STACK_LIMIT` capability. The ABI version stays 2.0; the
  host fingerprint and feature check reject a mismatched plugin.
- The native host ABI adds `LUAU_HOST_STATUS_THREAD_NOT_RUNNABLE` and the
  required `LUAU_HOST_FEATURE_RUNNABLE_THREAD_GUARD` capability. Every host
  operation that can run Luau code refuses a thread that cannot run it, and
  `luau_host_pcall` reports that case with the new status instead of
  `LUAU_HOST_STATUS_INVALID_ARGUMENT`. The ABI version stays 2.0 and the host
  fingerprint changes, so this `Luau.dll` requires the matching rebuilt native
  plugins.
- The native host changed without an ABI change: the ABI version, exports,
  and feature flags are unchanged, and the host fingerprint changes, so this
  `Luau.dll` requires the matching rebuilt native plugins.
- Full Luau Scripting Demo: re-import the sample to pick up the changes
  above. `LuauInputLibrary`'s members are now instance members and its
  constructor takes a `LuauInputLibrary.Policy`. A script that changes an
  object it only touched, or reads a key, mouse button, or touch the runtime's
  policy does not enable, now fails and disables its behaviour; add the object
  to the behaviour's Object References, or the input to the runtime's Input
  Policy. A scene that spawns more than 64 live objects on one runtime needs a
  higher runtime **Max Spawned Objects**.

## [0.3.1] - 2026-09-05

### Fixed

- Keep host property reads dynamic instead of caching getter values during
  bytecode loading.
- Allow unreachable VMs with managed callbacks to finalize, using weak handles
  supported by IL2CPP and releasing all callback tokens after native cleanup.
- Resume asynchronous managed callbacks on the correct coroutine, preserve
  callback arguments and result ownership, and reject continuations across
  non-yieldable parent coroutines.
- Reject ordinary script yields from root execution and function invocations;
  explicit coroutine APIs retain their yield behavior.
- Restore the coroutine stack when resume arguments fail validation so a
  subsequent resume remains usable.
- Bound decoded UTF-8 print output even when malformed input expands into
  replacement characters.
- Bound retained module-cache key and diagnostic-string payloads per root with
  `MaxCachedModuleBytes` (16 MiB by default), including Unity state configuration.

### Compatibility

- Update the managed DLL and bundled native plugins together. The managed
  runtime now requires the native coroutine-suspension capability.

## [0.3.0] - 2026-08-22

### Added

- A generated first-party bytecode manifest workflow, enabled per project and
  per asset, with fail-closed build validation and one runtime option that
  preserves existing state limits.
- Sandboxed `LuauScriptInstance` asset loading and lifecycle-agnostic,
  per-root script phases with deterministic ordering, aggregate budgets, and
  configurable failure isolation.
- An importable Full Luau Scripting Demo split into reusable `Core/` components
  and a one-scene Flappy Bird game whose gameplay scripts are entirely Luau.
  The core demonstrates explicit `self` and scene-object capabilities, a shared
  trust-domain table, and bounded Unity lifecycle phases; the game spawns its
  pipe pairs through `spawnPrefab` under a per-behaviour cap of two, with owned
  cleanup on shutdown.

### Changed

- Unity object capability policy is now consumer-defined. Generated
  capabilities remain the preferred path for application-owned annotated
  types; external types use explicit immutable `LuauObjectDescriptor<T>`
  values.
- Full Luau Scripting Demo owns editable descriptors for the supported
  `GameObject`, `Transform`, 2D physics, rendering, audio, and text surfaces
  used by its `self`, named-reference, and spawned-prefab handles.
- Capability Binding has been folded into Getting Started. Package Manager now
  presents two samples: Getting Started and Full Luau Scripting Demo.
- The Full Luau Scripting Demo `Input` library polls the Input System package
  instead of the legacy Input Manager, and its `Core` assembly now references
  `Unity.InputSystem`. The sample scene needs Active Input Handling set to
  *Input System Package (New)* or *Both*; the runtime warns when it sees no
  input devices. The Luau-visible member names are unchanged.

### Removed

- **Breaking:** removed the package-owned `GameObject` and `Transform`
  descriptors and the `state.CreateHandle(gameObject)` /
  `state.CreateHandle(transform)` convenience overloads. Import and explicitly
  select the Full Luau Scripting Demo core policy, copy and customize it in
  application code, or define a narrower descriptor or wrapper for the
  application's needs.

## [0.2.0] - 2026-07-19

### Added

- Finite state, execution, decoded-value, module, compiler-queue, importer, and
  capability budgets, with visibly named unbounded opt-ins for trusted work.
- A versioned, bounded persistent-artifact encoding plus bounded, immutable
  managed module maps and bundles.
- Package-owned object capability surfaces for `GameObject` and `Transform`.
- Package-local documentation, legal notices, XML IntelliSense, and two
  importable samples.
- Deterministic package archive/content validation and stripped Android
  shipping-plugin validation with separately retained symbols.

### Changed

- The UPM package ID is now `com.qll.luau.unity`, published by Quantum Lion
  Labs from the canonical `Quantum-Lion-Labs/Luau-Unity` repository.
- Allocating execution, invocation, and resume APIs return a disposable
  `LuauResultScope`; `*Into` APIs retain caller-owned destination semantics.
- Callback reference arguments are borrowed and callback-scoped. Call
  `Retain()` to create an owned reference that may outlive the callback.
- Module maps and trust-domain policy are managed-runtime contracts; Unity
  supplies asset adapters only.
- The native host ABI is revision 2 and uses stale-safe, monotonic non-reused
  opaque references with O(1) validation and explicit callback registration
  identity.
- `.luau` import is length-first, bounded, a single admitted-byte pass, and
  strict UTF-8. First-party artifacts persist stable source identity separately
  from provenance claims.

### Removed

- Implicit ownership through result arrays and legacy duplicate execution
  paths.
- Verification-only smoke types from the product API inventory.

[0.3.1]: https://github.com/Quantum-Lion-Labs/Luau-Unity/releases/tag/v0.3.1
[0.3.0]: https://github.com/Quantum-Lion-Labs/Luau-Unity/releases/tag/v0.3.0
[0.2.0]: https://github.com/Quantum-Lion-Labs/Luau-Unity/releases/tag/v0.2.0
