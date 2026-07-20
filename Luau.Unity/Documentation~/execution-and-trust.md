# Running scripts you didn't write

Read this before you let players load their own content.

The package treats three things as separate, even though all three are "a script"
in casual conversation:

1. **Source** — Luau text, from an asset or a download.
2. **Compiler output** — the in-memory result of compiling source, valid only in
   the process that produced it.
3. **Persistent bytecode** — a saved binary artifact.

Converting one into a byte array does not turn it into another. In particular,
grabbing the bytes out of compiler output does not give you something you can
save and load later — that's what [artifacts](artifacts.md) are for, and they
carry an authentication requirement precisely because bytecode skips the
compiler's safety work.

## Your own scripts, imported as assets

Use `state.ExecuteAsync(asset, cancellationToken)`. Source compiles on the
package's background queue, off the VM thread, and execution resumes on the
state's scheduler — the main thread, by default. This is the path you want
essentially always.

`state.Execute(asset)` is the synchronous version. It compiles on the calling
thread, which will hitch if the script is large, and it bypasses the shared
queue's limits. It's there for editor tooling and small trusted workflows. It is
not the path for player content.

Assets containing verified bytecode skip compilation but still have to satisfy
the state's bytecode policy and validator.

## Player-supplied source

The flow is:

1. Download or unpack the text under a size cap **you** enforce, before any of
   this package sees it.
2. Validate it as UTF-8.
3. Compile with `LuauUnity.CompileAsync`.
4. Execute the resulting `LuauCompilerOutput`, in this same process.

```csharp
var compilation = await LuauUnity.CompileAsync(
    downloadedUtf8Source,
    cancellationToken: cancellationToken);

switch (compilation.Kind)
{
    case LuauCompileResultKind.Success:
        using (var values = await state.ExecuteCompilerOutputAsync(
            compilation.Output!,
            "@mods/example/main.luau".AsMemory(),
            cancellationToken))
        {
            // resumed on the Unity context captured at CreateState
        }
        break;

    case LuauCompileResultKind.Diagnostic:
        ShowModAuthorTheirSyntaxError(compilation.CompilationDiagnostic!.Message);
        break;

    case LuauCompileResultKind.Canceled:
        break;

    case LuauCompileResultKind.InfrastructureFailure:
        throw compilation.InfrastructureException!;
}
```

The four result kinds are separate on purpose: a mod author's typo, a
cancellation, and the compiler service itself failing all need different
handling, and none of them should look like the others. A fifth case, hitting a
queue limit, throws `LuauCompilationLimitException`.

The shared compile queue bounds request size, how many requests can be queued,
total queued bytes, output size, worker count, and shutdown progress. It runs one
worker with 32 queue slots on Windows and the Editor, 16 on Android, and the
package drains it before Editor assembly reload and on player exit — you never
dispose it yourself.

What it cannot bound is described in [compiler security](compiler-security.md) —
worth reading if you're accepting arbitrary remote content.

If you need an isolated queue, a custom resource policy, or an independent
lifetime, construct a `LuauThreadedCompilationService` yourself (starting from
`LuauUnity.GetRecommendedCompilationOptions()`) and pass it to
`ExecuteWithCompilationServiceAsync`. That service is yours: Unity doesn't track
it, and you must dispose it before its owning subsystem goes away.

**Give each untrusted mod its own root.** This is the single most important rule
on this page. A root is the isolation boundary: everything inside one shares a
memory budget, a set of host functions, a module cache, and a lifetime. Two mods
in one root can reach each other. Two mods in two roots cannot.

Raw `LuauCompiler.Compile` is a synchronous expert API for trusted tooling. It
doesn't use the shared queue and can't preempt a native compile in progress.

## Persistent bytecode

Bytecode you saved earlier and load later is a genuinely different trust level,
because loading it skips the compiler entirely. Never accept it from a mod.

The artifact format records compiler version, native ABI, source identity,
payload hashes, and whatever provenance you attach. Parsing checks that the file
is well-formed and internally consistent — and that's *all* it checks. It does
not tell you who made the file. Loading additionally requires setting
`LuauBytecodePolicy.RequireValidator` and supplying a validator that
authenticates the artifact against build data you trust.

Don't trust a label, asset GUID, or hash just because the artifact contains it.
An attacker who wrote the file also wrote those. See [artifacts](artifacts.md).

## When things go wrong

**Cancelling** a queued compile releases its slot. Once a native compile has
actually started it can't be safely interrupted, so cancellation lets it finish
and throws the result away. Execution cancellation is cooperative: it takes
effect at VM interrupt points.

**Failures** resolve in a fixed order — hard stop first, then managed callback
failure, then allocator or native failure. Recoverable failures restore the
shared stack boundary and leave the VM usable. If the terminal reset itself
fails, the root is poisoned and closed rather than left in an unknown state.
