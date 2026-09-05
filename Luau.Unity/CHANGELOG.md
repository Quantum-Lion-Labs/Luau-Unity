# Changelog

All notable package changes are recorded here. The package follows semantic
versioning while in preview; preview releases may make explicitly documented
API or ABI breaks.

## [0.3.1] - 2026-09-05

### Fixed

- Keep host property reads dynamic instead of caching getter values during
  bytecode loading.
- Allow unreachable VMs with managed callbacks to finalize while retaining
  callback release tokens through native cleanup.
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
