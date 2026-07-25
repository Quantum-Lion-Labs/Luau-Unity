# Exposing C# to Luau

Luau scripts can't see anything in your game until you hand it to them. There is
no reflection and no ambient `GameObject.Find`. For types you own, two
attributes let a source generator write the binding code. For external types
you cannot annotate, an explicit descriptor provides the same reflection-free
dispatch. Both paths work under IL2CPP and AOT. An application descriptor may
deliberately add a fixed component allowlist, as Full Luau Scripting Demo does;
that still reaches only components on a `GameObject` already handed to Luau.

There are two ways to expose something, and the difference matters:

| | **Global library** | **Object capability** |
| --- | --- | --- |
| Visible to | every script in the root | the one script you give it to |
| Looks like | a global table: `game.spawn(...)` | a value you assign: `self.name = "x"` |
| Use for | your game's API surface | one specific object a script may touch |

## Global libraries

Mark a partial class `[LuauLibrary("name")]` and the members you want reachable
`[LuauMember]`:

```csharp
[LuauLibrary("clock")]
public sealed partial class ClockLibrary
{
    [LuauMember("realtime")]
    public static double Realtime() => Time.realtimeSinceStartupAsDouble;
}
```

Register it inside `LuauUnityOptions.ConfigureHostApis`:

```csharp
using var root = LuauUnity.CreateState(new LuauUnityOptions
{
    ConfigureHostApis = state => state.OpenLibrary(new ClockLibrary()),
});
```

It has to happen there because `CreateState` freezes the globals as its last
step. After that no one can add to or replace them — scripts can't, and neither
can you.

A few things worth knowing:

- **The Luau name is your C# name, verbatim.** `Realtime` would be
  `clock.Realtime`. Pass an explicit name to the attribute, as above, when you
  want camelCase.
- **Unmarked members don't exist** as far as Luau is concerned.
- **Unsupported signatures fail your build**, not your playtest. The generator
  also rejects annotating a member it can't reach.
- **Async works.** A member returning `Task`/`ValueTask` (optionally taking a
  `CancellationToken`) becomes a function Luau can yield on.
- **Need the call context?** Mark a parameter `[FromLuauState]` to receive the
  `LuauCallContext` instead of a script argument.

Properties and async members in practice:

```csharp
[LuauLibrary("ship")]
public sealed partial class ShipApi
{
    [LuauMember("fuel")]
    public int Fuel { get; private set; } = 100;

    [LuauMember("consume")]
    public bool Consume(int amount)
    {
        if (amount < 0 || amount > Fuel)
            return false;

        Fuel -= amount;
        return true;
    }

    [LuauMember("refuelAsync")]
    public async ValueTask<int> RefuelAsync(int amount, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        Fuel += amount;
        return Fuel;
    }
}
```

A private setter stays private: Luau can read `ship.fuel` but only `ship.consume`
can change it.

## Object capabilities

A capability is a single object handed to a single script. It grants exactly the
members you described and nothing else — no pointer, no registry token, no
`GCHandle`, no way to walk from the object to the rest of the scene.

Installing Luau.Unity does not define a Luau-visible surface for `GameObject`,
`Transform`, or any other Unity object. The application owns that policy and
selects it when creating each handle. There are two supported authoring paths.

### Generated capability for a type you own

Annotate an application-owned type with capability exposure:

```csharp
[LuauLibrary("Door", Exposure = LuauLibraryExposure.Capability)]
public sealed partial class DoorController : MonoBehaviour
{
    [LuauMember("isOpen")]
    public bool IsOpen { get; private set; }

    [LuauMember("open")]
    public void Open() => IsOpen = true;
}
```

```csharp
using var door = root.CreateHandle(GetComponent<DoorController>());
thread["door"] = door;
```

The generator implements the capability contract and creates its descriptor.
The generic `CreateHandle(target)` extension selects that generated descriptor.
One class is either a global library or an object capability, never implicitly
both, and unmarked members do not exist to Luau.

### Manual descriptor for a type you cannot annotate

Unity owns `GameObject`, so an application cannot add `[LuauLibrary]` to it.
Define a deliberately small descriptor instead. This example grants read-only
access to `name` and nothing else:

```csharp
public static class GameCapabilities
{
    public static readonly LuauObjectDescriptor<GameObject> ReadableName =
        new LuauObjectDescriptor<GameObject>(
            "ReadableGameObject",
            LuauUnityObjectGuard.ThrowIfDestroyed,
            new[]
            {
                LuauObjectMember<GameObject>.Property(
                    "name",
                    (target, context) => context.Return(target.name),
                    setter: null),
            });
}
```

Pass the target and selected policy explicitly:

```csharp
using var target = root.CreateHandle(
    targetGameObject,
    GameCapabilities.ReadableName);
thread["target"] = target;
```

`LuauUnityObjectGuard.ThrowIfDestroyed` rejects both managed null and Unity's
destroyed-object fake-null state before every access. `LuauUnityValue` provides
AOT-safe conversions when a descriptor exposes `UnityEngine.Vector3` values.

The **Getting Started** sample includes both a generated application-owned
capability and a minimal manual `GameObject` descriptor. Import **Full Luau
Scripting Demo** for editable, reusable policies modeled after the supported
parts of Unity's `GameObject`, `Transform`, 2D physics, rendering, audio, and
text APIs. Those policies belong to the sample, not the Luau.Unity runtime.

### Descriptors are immutable authority

A `LuauObjectDescriptor<T>` is a complete immutable policy. There is no
`AddMember` API, and the runtime never mutates or silently widens an existing
descriptor. If you want the Full Luau Scripting Demo surface plus another
member, copy or adapt its core source, or construct a new descriptor in
application code, and review the whole resulting surface.

Descriptor identity is also part of the authority. Two descriptors over the
same managed object create separate capability views; creating a wider view
does not upgrade a narrower handle that Luau already holds.

### Migrating package-provided Unity handles

The package previously provided `state.CreateHandle(gameObject)` and
`state.CreateHandle(transform)`. Those overloads and their package-owned
surfaces have been removed. Choose one of these replacements:

- Import **Full Luau Scripting Demo** and explicitly use
  `LuauUnityCapabilities.GameObjectDescriptor` or another descriptor from its
  editable `Core/` policy.
- Copy those sample descriptors into application code and customize the policy
  there.
- Define a narrower descriptor or an application-owned wrapper exposing only
  what the script needs.

For example, after importing Full Luau Scripting Demo:

```csharp
using var self = root.CreateHandle(
    gameObject,
    LuauUnityCapabilities.GameObjectDescriptor);
thread["self"] = self;
```

Keeping the descriptor visible at the call site makes the granted authority
reviewable. Do not replace it with a generic application overload that hides
which policy was selected.

### Lifetime

The handle belongs to the root that created it. Access from Luau fails cleanly —
rather than calling into a dangling object — once the target is garbage
collected, once a `UnityEngine.Object` is destroyed, or once the root closes.
`LuauStateOptions.MaxManagedHandleCount` caps how many can be live at a time
(1,024 by default); collected userdata releases its slot back.

Disposing the `LuauObjectHandle` releases the *managed wrapper* only. Any value
Luau is still holding stays usable until the VM collects it, so the common
pattern of creating a handle, assigning it to a global, and disposing it at the
end of setup is correct:

```csharp
using (var handle = root.CreateHandle(gameObject, GameCapabilities.ReadableName))
{
    thread["self"] = handle;
}
// the script's `self` still works here
```

The target still comes from an explicit serialized reference or another
host-controlled source. The descriptor does not grant scene discovery.

## Manual callbacks

For a small one-off that doesn't justify a whole library type, you can register a
function directly:

```csharp
state["clamp"] = state.CreateFunction("clamp", context =>
{
    var value = context.Read<double>(0);
    var minimum = context.Read<double>(1);
    var maximum = context.Read<double>(2);
    context.Return(Math.Clamp(value, minimum, maximum));
});
```

Argument indexes are zero-based. Generated library members run under exactly the
same lifetime, cancellation, and failure rules as these — the generator is a
convenience, not a different mechanism. Prefer `[LuauLibrary]` for anything
you'd call an API.

Async callbacks work too: arguments are read and results returned only while the
VM is safely suspended, and the context stays valid across your `await` and
respects the root's continuation scheduler.

## Calling Luau from C#

The other direction. Read a script closure out of a result and invoke it with
managed values:

```csharp
using var results = state.DoString("return function(a, b) return a + b end");
using var add = results[0].Read<LuauFunction>().Retain();

using var sum = add.Invoke([20, 22]);
Debug.Log(sum[0].Read<long>()); // 42
```

Use `Invoke` when the closure can't reach an async host callback, and
`InvokeAsync` when it can.

You can only invoke *script* closures this way. A function you created with
`CreateFunction` is a host capability that Luau calls — not a C# delegate you
round-trip, so invoking it from C# fails deliberately rather than pretending to
work.

For coroutines, `Resume(arguments)` returns an owned scope and `ResumeInto`
writes into memory you own; the async forms follow the same naming. `GetStatus()`
reports `Suspended`, `Running`, or `Dead`, and rejects being called on a root.

## Ownership inside callbacks

When Luau calls into your C#, the arguments you receive are *borrowed*: valid
for that call and no longer. This covers `LuauCallContext` itself and any table,
function, buffer, userdata, or object handle you read out of it. Stash one in a
field and use it next frame and you'll get an exception, not a crash.

When you genuinely need one to outlive the call, `Retain()` gives you an owned
copy that you are then responsible for disposing.

```csharp
// borrowed — fine to use now, do not store
var config = context.Read<LuauTable>(0);

// owned — store it, and dispose it when you're done
var callback = context.Read<LuauFunction>(1).Retain();
```

Two exceptions to the borrowing rule:

- **`LuauState` thread arguments** are the VM's shared cached wrapper. They stay
  valid after the call returns. Dispose one only when every holder is finished
  with it.
- **`Return<T>` never transfers ownership.** Pushing a value to Luau doesn't
  hand off your managed wrapper or dispose it. A library returning a long-lived
  wrapper keeps owning it; a callback that creates a temporary owned wrapper
  disposes it when its own use ends.

Generated properties and methods follow the same rule.

Callbacks get zero-based `Read<T>`, `Return<T>`, cancellation, and size-limited
diagnostics. Raw native handles and direct stack access are intentionally not
public — if you're reaching for them, the capability model is the intended
answer instead.
