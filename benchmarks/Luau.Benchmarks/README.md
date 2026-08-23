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
