# Concepts and vocabulary

This package borrows vocabulary from the Luau VM and from security review. None
of it is hard, but a lot of it is unfamiliar if you have only written Unity C#.
This page explains every term the rest of the documentation uses. Skim it once
and you should not need it again.

## The runtime

**Root state (`LuauState`)** — one Luau virtual machine. Think of it as a
self-contained scripting world: its own memory budget, its own global functions,
its own loaded scripts. Nothing crosses from one root to another. Creating a
root costs real memory, so a game creates a few, not one per GameObject. You own
it and you must dispose it.

Wherever these docs say "root", they mean this object, never a scene root.

Execution on the root must complete: an ordinary `coroutine.yield()` resets
the current call and throws `LuauException`. This includes root `DoString` and
all script-function invocations. For resumable execution, run source on a child
thread and continue it with `Resume` or `ResumeAsync`. Asynchronous root calls
can still await managed callbacks.

**Sandboxed thread** — a cheap script environment running inside a root. Threads
share the root's globals and host functions, but each one gets its own writable
global table, so a variable one script declares is invisible to the next. This
is the right unit for "one running script instance", roughly what a MonoBehaviour
is to a prefab. Create one per scripted object.

**Trust domain** — everything inside one root can reach everything else inside
it: same memory pool, same host functions, same module cache. That makes the
root, not the thread, the real isolation boundary. Two mods from two different
authors belong in two roots. Your own first-party scripts can share one.

**Sandboxing / freezing** — after you finish registering your C# APIs, the
package freezes the root's globals so scripts cannot overwrite `print`, replace
a function you exposed with their own, or reach the environment-manipulation
built-ins. `LuauUnity.CreateState` does this for you at the end of setup, which
is why host registration has to happen inside `ConfigureHostApis` rather than
afterward.

## Getting C# and Luau to talk

**Host library** — a C# class you expose to Luau as a global table. You mark the
class `[LuauLibrary("name")]` and the members you want reachable `[LuauMember]`,
and a source generator writes the binding code at compile time. Nothing uses
reflection, so it survives IL2CPP and AOT.

**Capability / object handle** — a host library is global: every script in the
root sees it. A capability is the opposite, a single object handed to a single
script. For a type you own, a generated capability does the work; for anything
else, `root.CreateHandle(target, descriptor)` says which policy applies. Either
way the resulting `LuauObjectHandle` reaches only the members that policy lists.
There is no `GameObject.Find` from Luau, by design — if a script can reach an
object, it's because you passed it in.

**Capability descriptor (`LuauObjectDescriptor<T>`)** — one immutable,
reflection-free list of what a managed type exposes. The source generator writes
one for a type you annotated; you write one by hand for a type you can't, like
anything Unity owns. Identity is part of the authority, so a narrow and a wide
descriptor over the same object stay separate capabilities and never upgrade
each other. Note that there is no built-in `GameObject` or `Transform` surface —
see [exposing C# to Luau](capability-bindings.md#object-capabilities) for why,
and where to get a ready-made one.

**Result scope (`LuauResultScope`)** — what you get back from executing a script.
Numbers, strings, and booleans inside it are plain copies. Tables and functions
are live references into the VM, and the scope owns them, so they die when you
dispose the scope. That is the whole reason `using` shows up on every execution
call in these docs.

**Owned vs borrowed** — an *owned* reference is one you are responsible for
disposing. A *borrowed* one is valid only for the moment it was handed to you,
typically inside a callback, and throws if you stash it and use it later. If you
need a borrowed reference to survive, call `Retain()` to get an owned copy. The
practical rule: values you pull out of a table getter are yours to dispose;
values sitting directly in a result scope belong to the scope.

**Script instance (`LuauScriptInstance`)** — one sandboxed thread plus one
export table returned by its script. The instance caches named function
entrypoints from that table and owns them until it is disposed. It does not
decide that a function named `update` is a Unity lifecycle hook; hosts compose
entrypoints into their own archetypes and schedules.

**Script phase (`LuauScriptPhase`)** — a named, synchronous host dispatch list
with per-call limits, an aggregate wall-clock budget, and an explicit failure
policy. A `LuauScriptScheduler` groups phases for one root but does not own the
instances registered with them.

### What values look like on each side

Every value crossing the boundary is a `LuauValue`, which you unpack with
`Read<T>()`:

| Luau | Managed |
| --- | --- |
| `nil` | `LuauValue.Nil` |
| `boolean` | `bool` |
| integer | `long`, and range-checked smaller integer types |
| number | `double` or `float` |
| vector | `System.Numerics.Vector3` |
| string | `string` |
| table | `LuauTable` |
| function | `LuauFunction` |
| managed object capability | `LuauObjectHandle` |
| other VM-created userdata | `LuauUserData` (inspect only) |
| thread | `LuauState` |
| buffer | `LuauBuffer` |

The first six copy. Everything from `LuauTable` down is a live reference subject
to the ownership rules above.

Two things that catch people out: `LuauTable.Length` is Luau's raw `#` sequence
length, not a count of key/value pairs. And `LuauBuffer.ToArray()`, `Read(...)`,
and `Write(...)` all copy — the package never hands you a borrowed view into
native buffer memory, because that view could outlive the memory.

In Unity, `LuauUnityValue.ReadVector3` and `ReturnVector3` convert between Luau's
vector and `UnityEngine.Vector3`.

## Scripts as assets

**`LuauAsset`** — the imported asset for a `.luau` file, the thing you drag into
an inspector slot. The importer checks the file is valid UTF-8 and within the
size limit, and compiles it once so you get syntax errors in the Console at
import time instead of at runtime.

**Source vs bytecode** — a script can be shipped as text or as precompiled
bytecode. These are deliberately different trust levels. Text gets compiled and
sandboxed as usual; bytecode skips the compiler entirely, which means malicious
bytecode is not something the VM can defend against. Never load bytecode from a
player. See [persistent artifacts](artifacts.md).

**Module map** — Luau's `require()` doesn't touch the filesystem here. Instead
you hand the root a `LuauModuleMap`: a fixed, immutable set of named scripts you
have already loaded and vetted. `require("foo")` finds what's in the map or
fails. See [modules](modules.md).

**Artifact** — a precompiled bytecode file with a signed-ish envelope around it
(compiler version, source identity, hashes). Parsing one proves it isn't
corrupt; it does *not* prove who made it. See [persistent artifacts](artifacts.md).

## Safety vocabulary

**Options / limits / policy** — every constructor here starts from finite
defaults: how much memory the VM may allocate, how long a script may run, how
many objects you may hand out. These live on `LuauStateOptions` and friends.
Watch out for one thing: assigning a new options object replaces the *whole*
policy, so copy the limits you still want rather than starting from blank.

**Bounded** — has a hard ceiling that is enforced, rather than growing until
something falls over. When these docs call a queue or a buffer bounded, that's
what they mean.

**Admission** — the check that happens before content is accepted and memory is
allocated for it: is this file small enough, is it valid UTF-8, is there room in
the queue. Content that fails admission is rejected before it costs anything.

**The compiler lane** — the official Luau compiler is native code, and compiling
a big script is slow enough to drop frames. So the package runs compilation on a
shared background queue with limits on request size, queue depth, and worker
count. `ExecuteAsync` uses it; the synchronous `Execute` does not, which is why
`Execute` is for editor tooling rather than for player-supplied content.

## Where to go next

- [Getting started](getting-started.md) puts most of the above into one working
  scene.
- [Capability bindings](capability-bindings.md) covers exposing C# in depth.
- [Script instances](script-instances.md) covers reusable exports, schedulers,
  and application archetypes.
- [Execution and trust](execution-and-trust.md) covers running content you did
  not write.
