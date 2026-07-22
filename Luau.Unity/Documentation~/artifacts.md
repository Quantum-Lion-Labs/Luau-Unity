# Precompiled bytecode (artifacts)

An artifact is Luau bytecode you compiled ahead of time and saved, wrapped in a
versioned envelope. Use it to skip compilation at load time for your own shipped
content.

The envelope records the format version, the compiler and native ABI it was
built with, the source's identity and hash, the payload's length and hash, and
any provenance data you attach. Unity precompilation uses
`unity-asset-guid:<guid>` as the source identity; a publisher ID you configure is
stored alongside it but is just a claim, not proof of anything.

## Integrity is not trust

This is the part that matters, so it gets its own section.

When the codec parses an artifact successfully, it has proven the file is
well-formed and internally consistent — nothing truncated, no trailing garbage,
no integer overflow, no oversized fields, hashes match. It has **not** proven who
made the file, or that the code inside is appropriate for your build.

Bytecode bypasses the compiler. Everything the compiler would normally verify
about a script simply doesn't happen. So a validly-formed artifact from an
attacker is a validly-formed artifact that runs their code. Hashes prove the file
wasn't corrupted in transit, not that you wanted it in the first place.

**Never accept bytecode from a player or a mod.** Ship source for that, and let
the compiler do its job. See [execution and trust](execution-and-trust.md).

## Generated first-party manifest (recommended)

For Unity assets shipped by your project, the package can generate the allowlist
and install its validator for you:

1. In **Project Settings > Luau.Unity**, select **First-party precompile with
   generated manifest** and configure a stable provenance ID.
2. Select each trusted `.luau` asset that should ship as bytecode and enable its
   **Precompile** importer option. Assets without that opt-in remain source, so
   first-party and mod content can coexist.
3. Create the state with
   `new LuauUnityOptions { UseFirstPartyBytecode = true }`.
4. Build normally. The build refreshes imports, verifies every opted-in asset
   under `Assets`, and regenerates the manifest before collecting player content.

The generated asset is owned by Luau.Unity at
`Assets/Generated/Luau.Unity/Resources/Luau.Unity/FirstPartyBytecodeManifest.asset`.
Do not edit it or place project content under that package-owned folder. It is
safe to ignore the folder in source control: Editor startup and every player
build regenerate the asset deterministically. Project Settings reports whether
the manifest is current and provides **Reimport Luau Assets** and **Refresh
Manifest** recovery actions.

`UseFirstPartyBytecode` is disabled by default. When enabled, state creation
keeps the caller's resource, execution, and scheduler settings but replaces the
default bytecode rejection policy with the generated-manifest validator. It
fails before allocating a native state if the manifest cannot be loaded or if
the caller also supplied a custom bytecode policy or validator.

The manifest authenticates one project snapshot: every opted-in asset under
`Assets` at the time of the player build, including assets not referenced by its
scenes. Addressables and AssetBundles built from that same snapshot can use the
embedded entries. Changed bytecode, newly compiled remote bytecode, and content
added after the player build are rejected until a new player with a regenerated
manifest is shipped. Manifest signing and post-build remote-content updates are
not part of this workflow yet.

The provenance ID remains a public publisher or scheme label. It is recorded
once as the manifest's common provenance ID and checked for every artifact, but
the embedded manifest entry—not the label—establishes trust.

## Advanced custom validators

Keep `UseFirstPartyBytecode` disabled when your host already authenticates
artifacts. The existing custom-validator flow is unchanged:

1. Compile trusted source with the reviewed toolchain.
2. Write the artifact with a stable source identity and your provenance claims.
3. Authenticate it against a signed manifest or an allowlist controlled by your
   host.
4. Set `LuauBytecodePolicy.RequireValidator` on the root, with that validator.
5. Load through the verified-artifact API.

In code, building one:

```csharp
var output = LuauCompiler.Compile(firstPartySource);
var artifact = LuauBytecodeArtifact.Create(
    output,
    "unity-asset-guid:" + assetGuid,
    "nervbox:first-party/v1",
    Encoding.UTF8.GetBytes(assetGuid));
```

And loading one:

```csharp
using var state = LuauUnity.CreateState(new LuauUnityOptions
{
    StateOptions = LuauStateOptions.Default with
    {
        BytecodePolicy = LuauBytecodePolicy.RequireValidator,
        BytecodeValidator = firstPartyManifestValidator,
    },
});

using var function = state.LoadVerifiedBytecode(artifact, "@bundled/example.luau");
```

Step 3 is the one people skip. Reading a GUID or a provenance string out of the
artifact and comparing it to a list *inside that same artifact* is not
authentication — the trusted data has to come from your build, not from the file
you're checking. That's what `firstPartyManifestValidator` is for, and writing it
is your job.

Before your validator even runs, the state checks that the artifact's compiler
and runtime identity match exactly. The chunk name you pass is a diagnostic label
and never participates in that decision.

## Same-process compiler output

`LuauCompiler.Compile` returns a `LuauCompilerOutput` that can't be reconstructed
from bytes, so it stays loadable even on a state with `BytecodePolicy.Reject` —
useful for editor previews and SDK tests:

```csharp
var output = LuauCompiler.Compile("return 42"u8);
using var function = state.LoadCompilerOutput(output, "@development/example.luau");
```

Copying those bytes doesn't create a second load capability. The moment output
would cross a process, build, cache, file, or asset-bundle boundary, it needs the
artifact path above instead.

Production compilation defaults to `CoverageLevel = 0`. Tooling that wants
coverage instrumentation opts in with
`LuauCompileOptions.Default with { CoverageLevel = 2 }` — note that coverage
participates in compiler identity and bytecode hashes, so it changes what
validates.

## Unity importer and packaging

**Project Settings > Luau.Unity** has two modes:

- **`SourceOnly`** (default, and what SDK and mod projects want) — the importer
  still compiles transiently to report authoring errors, but stores UTF-8 source
  and hides the precompile option.
- **First-party precompile with generated manifest** (serialized enum value
  `AllowFirstPartyPrecompile`) — exposes a per-asset precompile option once
  you've configured a public first-party provenance ID. The generated manifest
  and `LuauUnityOptions.UseFirstPartyBytecode` provide the standard runtime
  validation path.

Hiding a checkbox is not a security boundary. Source-only player builds inspect
imported content and fail the build if any `LuauAsset` contains bytecode. A
custom mod exporter should run the same check over its own asset set:

```csharp
LuauSourceOnlyAssetValidator.ValidateSourceOnly(modAssetPaths);
```

`LuauAsset.AsSpan()` and `AsMemory()` are source-only and throw on precompiled
content, so an existing source exporter can't quietly start shipping bytecode.
`LuauModuleMap` is deliberately source-only for the same reason.

The codec itself is defensive: it checks declared lengths and configured caps
before it clones or allocates anything, and its readers reject malformed input
with typed diagnostics rather than best-effort guessing. The writer emits one
deterministic representation, so the same input always produces byte-identical
output.

First-party builds also fail closed for missing provenance, opted-in assets that
fell back to source after a compilation error, stale source/import state,
noncanonical or corrupted artifacts, duplicate source identities, and another
asset claiming the generated manifest's `Resources` load key. An empty manifest
is allowed so the mode can be configured before assets opt in, but Project
Settings displays a warning.

## Notes

`LuauCompilerOutput` (in-memory, from compiling in this process) and persistent
artifacts are not interchangeable, on purpose. You can't save compiler output's
bytes and load them back later as an artifact, and you can't reconstruct compiler
output from an artifact's bytes.

Artifacts created before source identity became a required field must be
reimported. The runtime rejects those older payloads outright rather than
guessing an identity from a diagnostic path or a provenance label.
