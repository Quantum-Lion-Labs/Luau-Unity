# Changelog

All notable package changes are recorded here. The package follows semantic
versioning while in preview; preview releases may make explicitly documented
API or ABI breaks.

## [Unreleased]

### Added

- Sandboxed `LuauScriptInstance` asset loading and lifecycle-agnostic,
  per-root script phases with deterministic ordering, aggregate budgets, and
  configurable failure isolation.
- An importable Luau Behaviour sample demonstrating explicit `self` and scene
  object capabilities, controlled prefab spawning with owned cleanup, and a
  shared bounded Unity Update phase.

## [0.2.0] - 2026-07-19

### Added

- Finite state, execution, decoded-value, module, compiler-queue, importer, and
  capability budgets, with visibly named unbounded opt-ins for trusted work.
- A versioned, bounded persistent-artifact encoding plus bounded, immutable
  managed module maps and bundles.
- Explicit generated object capabilities for `GameObject` and `Transform`.
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

[0.2.0]: https://github.com/Quantum-Lion-Labs/Luau-Unity/releases/tag/v0.2.0
