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
- Disposing a root from another thread just as a host operation began (for
  example a global or table access) could strand that operation, so the
  native VM was never closed and one callback registration leaked. The
  operation now ends even when its setup fails.

### Compatibility

- Update the managed DLL and all bundled native plugins together. The host
  fingerprint changed with the Luau update, so a mismatched pair fails the
  startup ABI check.
- The native host ABI adds `LUAU_HOST_STATUS_STACK_LIMIT`,
  `LUAU_HOST_FRAME_SLOT_LIMIT`, and the required
  `LUAU_HOST_FEATURE_STACK_LIMIT` capability. The ABI version stays 2.0; the
  host fingerprint and feature check reject a mismatched plugin.

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
