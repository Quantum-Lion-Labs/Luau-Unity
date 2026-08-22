# Managed artifact parser fuzz target

`Luau.ArtifactFuzz` is a dependency-free, process-isolated command target for
hostile `LuauBytecodeArtifactCodec` inputs. It does not invoke the native VM or
compiler. Every input is parsed through both the span API and a fragmented,
non-seekable stream. The two paths must agree.

The checked-in corpus combines structure-aware in-code seeds (valid envelope,
truncation, trailing data, length boundaries, corrupt integrity, invalid UTF-8,
compiler options, runtime identity, and bytecode hash) with reviewable `.hex`
files under `Corpus/`. Add a minimized reproducer there after review.

Every smoke pass also parses a valid artifact at the 768 KiB bytecode boundary
and a malformed input at the exact 1 MiB envelope boundary through both parser
paths. Those large boundary seeds run once instead of entering the mutation
pool; ordinary mutations periodically chain from the prior result so structural
changes can accumulate without turning the bounded smoke pass into gigabytes of
repeated large allocations. The run fails if exact-limit coverage disappears.

Run the deterministic bounded smoke pass from the repository root:

```bash
dotnet run --project fuzz/Luau.ArtifactFuzz -c Release -- --smoke --iterations 25000 --seed 0x6a09e667f3bcc909
```

The target caps each decoded input at 1 MiB, the checked-in corpus at 16 MiB,
and iterations at 1,000,000. A successful artifact or a
`LuauArtifactException` rejection is expected. Any other exception, parser-path
divergence, or rejection of the canonical valid seed fails the process and
writes the exact `.bin` input plus a report beneath
`artifacts/artifact-fuzz-reproducers` by default. CI should retain that directory
on failure. Before every evaluation the target atomically replaces
`current-input.bin` and its context report, then removes them only after a
successful run. A job timeout, native process crash, stack overflow, or OOM
therefore still leaves the last in-flight input for CI artifact retention. The
managed validation job has a finite 45-minute outer timeout.

Replay a retained input exactly:

```bash
dotnet run --project fuzz/Luau.ArtifactFuzz -c Release -- --input artifacts/artifact-fuzz-reproducers/artifact-<sha256>.bin
```

Use `--help` for corpus and reproducer directory overrides. Changing the frozen
artifact format or native build identity requires a reviewed update to the
canonical structural seed constants.
