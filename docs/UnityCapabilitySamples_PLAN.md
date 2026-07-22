# Unity capability surfaces and two-sample strategy

## Status

Proposed implementation plan. This document does not change the runtime or
sample APIs by itself.

## Summary

Remove the package-owned `GameObject` and `Transform` capability surfaces from
`Luau.Unity`. Move their current behavior into the `Luau Behaviour` sample as
editable, reusable first-party components for the sample's eventual minigame.

After this change, installing `Luau.Unity` grants no predefined Unity object
surface. A host must define or import a capability policy and explicitly hand a
handle to a script. `Luau.Unity` continues to provide the AOT-safe primitives
needed to author that policy.

The package will expose two samples:

1. **Getting Started** teaches the individual systems in small, focused steps:
   state ownership, script execution, generated host libraries, generated
   capabilities, manual descriptors for third-party types, sandboxing, and
   result ownership.
2. **Luau Behaviour** grows into an opinionated demo minigame containing useful
   first-party scripting components that developers can copy and reuse. Its
   default Unity capability surfaces are sample policy, not package policy.

Keep the `Luau Behaviour` name and sample path during this migration. Rename it
when the minigame has enough content for its final identity; combining a rename
with the capability move would make the compatibility and review surface
unnecessarily broad.

## Goals

- Make the package's security model literal: no predefined `GameObject` or
  `Transform` authority ships in the runtime assembly.
- Preserve an easy opt-in starting point through editable sample code.
- Give consumers a documented path to expose external Unity types without
  modifying `Luau.Unity`.
- Make the distinction between generated capabilities and manual descriptors
  clear.
- Consolidate the package into exactly two samples.
- Preserve the current GameObject and Transform behavior when users opt into
  the sample defaults.
- Keep all bindings reflection-free and IL2CPP/AOT-safe.

## Non-goals

- Do not add `Renderer`, `Rigidbody`, `Collider`, `Animator`, or other Unity
  surfaces as part of this migration. The sample layout should make those easy
  to add later.
- Do not add ambient scene discovery, `GameObject.Find`, component enumeration,
  arbitrary `Resources` access, or reflection-based binding.
- Do not change `LuauScriptInstance`.
- Do not complete the minigame in the same change.
- Do not create a mutable descriptor-extension system. Capability descriptors
  remain immutable authority values; consumers compose a new surface when they
  want different authority.

## Target architecture

### `Luau` core

No change. It continues to own:

- `LuauObjectDescriptor<T>` and `LuauObjectMember<T>`.
- Explicit `LuauState.CreateHandle(target, descriptor)` creation.
- Generated `ILuauObjectCapability` bindings.
- Descriptor identity, handle lifetime, dispatch, and sandbox enforcement.

### `Luau.Unity` runtime

Retain only generally useful Unity authoring primitives:

- `LuauUnityValue.ReadVector3` and `LuauUnityValue.ReturnVector3`.
- `LuauUnityObjectGuard.ThrowIfDestroyed`.
- Unity state creation, assets, compilation, scheduling, and script-instance
  integration.

Remove from the runtime assembly:

- The private `LuauObjectDescriptor<GameObject>`.
- The private `LuauObjectDescriptor<Transform>`.
- `LuauStateExtensions.CreateHandle(GameObject)`.
- `LuauStateExtensions.CreateHandle(Transform)`.

Rename or split `LuauUnityObjectBindings.cs` so its remaining filename and XML
documentation describe authoring utilities rather than package-provided object
bindings. Preserve the existing public helper names unless a separate API
review finds a compelling reason to break them.

### Getting Started sample

This is the educational sample collection. It should teach mechanisms without
presenting a production game framework.

Absorb the current standalone **Capability Binding** sample into Getting
Started, then remove the third sample entry and its old folder. Organize the
README as short, ordered lessons even if the first implementation continues to
use only a few components and scripts:

1. Create and dispose a state.
2. Execute a `LuauAsset` and read owned results.
3. Generate and register a host library.
4. Generate a capability for an application-owned type.
5. Define a manual descriptor for an external type that cannot be annotated,
   using a deliberately small Unity example.
6. Inject only the resulting handle into a sandboxed thread.

The generated-capability lesson should use an application-owned component so
the source-generator path is obvious. The manual-descriptor lesson should
explain why Unity's `GameObject` class cannot be annotated and point to the
Luau Behaviour sample for a more complete, reusable policy.

### Luau Behaviour sample

This is the opinionated, reusable game-scripting sample. Add a sample-owned
capability module with public, clearly named descriptor values for:

- `GameObject`.
- `Transform`.

Preserve the current member surfaces initially:

- `GameObject`: `name`, `activeSelf`, `transform`, and `SetActive`.
- `Transform`: `name`, `position`, `localPosition`, `localScale`, `gameObject`,
  and `Translate`.

The two descriptors must refer to each other when returning `transform` and
`gameObject` handles, and every member access must continue to use
`LuauUnityObjectGuard.ThrowIfDestroyed`.

Expose the descriptors themselves rather than hiding them behind only a
`CreateHandle(GameObject)` convenience overload. Provide explicitly named
sample helpers if they materially improve the Behaviour code, but keep the
selected policy visible at the call site. This makes copying, narrowing, or
replacing the sample policy straightforward and avoids presenting a generic
`CreateHandle` call as authority-free.

Update all Luau Behaviour paths to use the sample-owned policy:

- `self` injection.
- Named scene-object references.
- Handles returned from controlled prefab spawning.
- Any future component that exposes GameObjects or Transforms.

Place the capability code beside the reusable Behaviour components, not inside
one monolithic `MonoBehaviour`, so future Renderer and Rigidbody policies can
be added without turning the host component into a binding registry.

## Implementation phases

### Phase 1: Establish consumer-defined descriptors in the sample

1. Add the sample-owned GameObject and Transform descriptors.
2. Preserve exact Luau member names, read/write behavior, vector conversion,
   destroyed-object behavior, and cross-object returns.
3. Switch the Luau Behaviour component's existing bindings and prefab-spawn
   results to the sample descriptors.
4. Verify the sample no longer relies on package-provided Unity object
   overloads.

Do this before deleting the runtime surfaces so the sample remains buildable
throughout the migration.

### Phase 2: Remove package-owned surfaces

1. Remove the two descriptors and two Unity-specific `CreateHandle` overloads
   from the `Luau.Unity` runtime assembly.
2. Retain and document the Unity value and liveness helpers.
3. Confirm that `LuauState.CreateHandle(target, descriptor)` remains the public
   direct-descriptor path for external types.
4. Regenerate or update checked-in API documentation artifacts as required by
   the repository build.

This is a source-breaking API change for consumers calling
`state.CreateHandle(gameObject)` or `state.CreateHandle(transform)`. Record it
prominently in the changelog. If the package has consumers relying on the 0.2
API, either make the removal in the next designated breaking release or provide
one release of obsolete forwarding methods; forwarding methods are not
compatible with the strict goal that the runtime ship no predefined surface.

### Phase 3: Consolidate Getting Started

1. Move the useful educational content from **Capability Binding** into
   **Getting Started**.
2. Replace its dependency on the removed default GameObject handle with either
   a generated application-owned capability or its own minimal manual
   descriptor lesson.
3. Remove `Samples~/Capability Binding` and its package sample entry only after
   the equivalent lesson exists in Getting Started.
4. Update the Getting Started README so users can follow lessons independently
   and understand which code is instructional versus reusable game scaffolding.
5. Leave exactly two entries in `package.json`: Getting Started and Luau
   Behaviour.

### Phase 4: Documentation and migration guidance

Update every document that currently states or implies that `Luau.Unity`
ships GameObject and Transform descriptors, including:

- `Documentation~/capability-bindings.md`.
- `Documentation~/concepts.md`.
- `Documentation~/getting-started.md`.
- `Documentation~/script-instances.md`.
- The repository and package READMEs where applicable.
- The changelog and public API documentation.

The capability documentation must show both supported authoring paths:

1. Annotate an application-owned type and use source generation.
2. Construct a manual `LuauObjectDescriptor<T>` for a type the application
   cannot annotate, such as a Unity type.

State explicitly that descriptors are immutable policies. A developer who
wants the sample surface plus one more member copies/composes a new descriptor;
the runtime does not mutate or silently widen an existing capability.

Add a migration note mapping old calls to the new choices:

- Import/use the Luau Behaviour sample's default policy.
- Copy and customize that policy into application code.
- Define a narrower descriptor or wrapper for the application's own needs.

### Phase 5: Tests and package validation

Refactor tests according to the layer they protect:

- Core capability tests continue to validate explicit descriptor identity,
  narrower views, lifetime, and generated capabilities without Unity defaults.
- Unity runtime tests continue to validate vector conversion and destroyed
  Unity-object guarding.
- Remove or rewrite the EditMode test that claims built-in GameObject and
  Transform bindings exist.
- Scheduler tests that merely need a handle use a test-local descriptor or
  generated test capability instead of a package default.
- Add validation for the sample-owned descriptors after importing the sample:
  exact member surface, property mutation, `SetActive`, `Translate`, cross-links,
  and failure after target destruction.
- Extend package CI/validation to import and compile both samples. Code under
  `Samples~` must not escape compilation coverage merely because Unity excludes
  unimported samples from the package runtime assembly.
- Run managed tests, Unity EditMode tests, package validation, consumer-contract
  compilation, and the repository's existing release/static checks.

## Minigame evolution after the migration

Treat the migrated descriptors as the first reusable subsystem of the future
minigame sample. Subsequent additions should follow the same pattern:

- Add focused, sample-owned policies for common types only when the game uses
  them.
- Prefer application-facing facades over mirroring entire Unity APIs.
- Keep scene references and prefab catalogs explicit in the Inspector.
- Keep spawning, destruction, pooling, and ownership behind host capabilities.
- Document why each exposed operation is present and what authority it grants.

Potential later policies include Transform rotation/parenting, Rigidbody
movement, Renderer material controls, Animator parameters, and controlled audio
playback. They are intentionally outside this migration.

## Acceptance criteria

- `Luau.Unity.dll` contains no GameObject or Transform descriptor and no
  Unity-specific `CreateHandle` convenience overload.
- Installing the package alone exposes no predefined Unity object member
  surface to Luau.
- The public explicit-descriptor API can bind `GameObject`, `Transform`, or any
  other external reference type without modifying `Luau.Unity`.
- Importing Luau Behaviour provides editable default GameObject and Transform
  policies with behavior equivalent to the former runtime defaults.
- Luau Behaviour's `self`, scene references, and spawned prefab results all use
  those sample policies.
- Getting Started teaches generated libraries, generated capabilities, and a
  manual descriptor without depending on Luau Behaviour.
- Package Manager presents exactly two samples.
- Documentation never describes the sample policy as ambient, exhaustive, or
  package-owned authority.
- Imported samples compile under the supported Unity and IL2CPP/AOT targets.
- All managed, Unity, package, and consumer-contract validation passes.

