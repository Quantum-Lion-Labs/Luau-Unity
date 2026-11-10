# Native hostile-input corpus

The two libFuzzer targets bound each input before it reaches the host:

- `luau_host_compiler_fuzzer`: 64 KiB source cap, including malformed UTF-8,
  deep syntax/types, large constants, and valid/invalid compiler option records.
  Ordinary corpus entries are passed to the compiler unchanged. An entry that
  starts with `#` followed by five control bytes uses the explicit option/null
  envelope; the remaining bytes are source. This keeps readable hostile Luau
  seeds on the parser/compiler path instead of accidentally consuming their
  first bytes as option selectors.
- `luau_host_abi_fuzzer`: 4 KiB cap over caller sizes, nulls, statuses, opaque
  reference tokens, callback returns, interrupt actions, throttled interrupt
  budgets and attention requests, every operation that can run Luau code on a
  thread suspended by a host break, and close/GC cleanup.

Run `cmake --build --preset linux-sanitize --target luau_host_fuzz_smoke`.
Each input has a five-second libFuzzer timeout and a 2 GiB RSS ceiling; the CI
job has a 30-minute outer timeout. Crashes, timeouts, and memory-limit failures
are written below `out/build/linux-sanitize/fuzz-artifacts` and uploaded by
native CI. Minimized reproducers belong in the matching corpus directory after
review. Compiler execution remains in-process; the product accepts residual
native compiler crash/hang risk and relies on strict admission limits,
sanitizer/fuzz gates, and separate VMs for mutually untrusted mods.
