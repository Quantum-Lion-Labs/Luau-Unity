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

## Loading your own artifacts

1. Compile trusted source with the reviewed toolchain.
2. Write the artifact with a stable source identity and your provenance claims.
3. Authenticate it against a signed manifest or an allowlist compiled into your
   game build.
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
- **`AllowFirstPartyPrecompile`** — exposes a per-asset precompile option once
  you've configured a public first-party provenance ID. Execution still requires
  the state's validator.

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

## Notes

`LuauCompilerOutput` (in-memory, from compiling in this process) and persistent
artifacts are not interchangeable, on purpose. You can't save compiler output's
bytes and load them back later as an artifact, and you can't reconstruct compiler
output from an artifact's bytes.

Artifacts created before source identity became a required field must be
reimported. The runtime rejects those older payloads outright rather than
guessing an identity from a diagnostic path or a provenance label.
