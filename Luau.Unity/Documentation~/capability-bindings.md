# Exposing C# to Luau

Luau scripts can't see anything in your game until you hand it to them. There is
no reflection, no `GameObject.Find`, no component lookup by name. You choose what
to expose with two attributes, and a source generator writes the binding code at
compile time — which is also why this works under IL2CPP and AOT.

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

The package ships descriptors for `GameObject` and `Transform`:

```csharp
using var handle = root.CreateHandle(targetGameObject);
thread["target"] = handle;
```

`GameObject` exposes `name`, `activeSelf`, `transform`, and `SetActive`.
`Transform` exposes `name`, `position`, `localPosition`, `localScale`,
`gameObject`, and `Translate`. That's the whole surface — deliberately small.

To expose your own type, use the same attributes with capability exposure:

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

One class is either a global library or a capability, never implicitly both.

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
using (var handle = root.CreateHandle(gameObject))
{
    thread["self"] = handle;
}
// the script's `self` still works here
```

Import the **Capability Binding** sample for a running version. Note that it
binds a serialized `GameObject` from the inspector — it never searches the scene,
and neither should you.

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
