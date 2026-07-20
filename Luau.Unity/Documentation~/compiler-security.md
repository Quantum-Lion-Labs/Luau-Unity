# Compiler security and known limits

If you're accepting scripts from players, here's what the compiler does and
doesn't protect you from.

## The short version

The Luau compiler is hardened against malicious source. Upstream treats a crash
during compilation as a security vulnerability with a bounty attached, the
compiler is continuously fuzzed, and it runs against adversarial user scripts at
Roblox scale. The usual ways to break a compiler — deeply nested source, files
engineered to emit millions of errors — are explicitly capped. This package
calls only the bytecode compiler, not Luau's type checker, which is the larger
surface.

What upstream does *not* promise is termination:

> Luau does not provide termination guarantees - some code may exhaust CPU or
> RAM resources on the system during compilation or execution.

Compilation runs in-process, so a hang or crash takes the game with it. The
execution time budget covers *running* a script, not compiling one, and
cancelling a started compile only discards the result — it doesn't stop the
work.

In practice compile time is close to linear in source size, so the reachable
version of this is "submit something huge, or a lot at once" — which the source
size cap and the bounded compile queue already cover. Don't raise those limits
for untrusted input without knowing why.

## Why not a separate process

Process isolation — a killable worker — is the real fix, and 0.2.0 knowingly
skips it. It needs a demonstrated platform requirement, a reviewed IPC and
artifact trust boundary, and its own performance and security evidence before
it's worth shipping a second native product, a desktop compiler CLI, and a
filesystem resolver.

## Further reading

- [Luau's security guarantees](https://github.com/luau-lang/luau/blob/master/SECURITY.md)
  — the scope of what upstream promises, and how to report a vulnerability.
- [execution and trust](execution-and-trust.md) — mechanics of the defenses
  listed above.
- [resource limits](resource-limits.md) — the caps this package applies and how
  to configure them.
