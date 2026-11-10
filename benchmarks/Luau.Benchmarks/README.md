# Stage 6 managed benchmark harness

This net9 console harness measures the same `netstandard2.1` managed assembly
consumed by Unity. It has no benchmark-framework or product dependency beyond
the repository projects, and the harness interop project copies the explicitly
selected platform-native host.

Run a quick validation pass from the repository root:

```bash
dotnet run --project benchmarks/Luau.Benchmarks -c Release -- --quick
```

Run the release evidence pass with an explicit sample count and output:

```bash
dotnet run --project benchmarks/Luau.Benchmarks -c Release -- --warmup 250 --iterations 5000 --output artifacts/stage-6-benchmarks/release.json
```

Run a subset by name prefix with `--filter`; for example, the interrupt
safepoint cases report nanoseconds per loop iteration under each execution
budget profile:

```bash
dotnet run --project benchmarks/Luau.Benchmarks -c Release -- --filter interrupt- --warmup 20 --iterations 500
```

The `entry-` cases measure the fixed cost of entering and leaving the VM for
the calls a per-frame script hook makes: `InvokeVoid` under each execution
budget profile, invocation with results, coroutine resume, execution of
cached compiler output, a script entrypoint called with reused options as a
Unity script phase does, and direct table access outside an operation. Each
sample makes 64 calls and reports nanoseconds and managed bytes per call. Use a
long warm-up so tiered compilation has settled before the first case:

```bash
dotnet run --project benchmarks/Luau.Benchmarks -c Release -- --filter entry- --warmup 3000 --iterations 5000
```

The compiler section retains the production-default baselines and also measures
the full supported optimization (0-2) and type-information (0-1) matrix for both
representative first-party and untrusted-mod scripts. Debug information remains
at level 1 and coverage remains disabled so each labeled case changes only the
two levels under test.

The JSON report records the source commit, committed tree hash, clean/dirty tree
state, native/compiler identity, mean and tail latency, and managed allocation
per operation. Compare reports on the same machine, power profile, runtime,
native plugin, and clean source commit. Do not treat results from different
environments as optimization evidence.
