# Exposing C# to Luau

Luau scripts can't see anything in your game until you hand it to them. There is
no reflection, no `GameObject.Find`, no component lookup by name. For a type you
own, two attributes let a source generator write the binding code. For a type you
can't annotate — anything Unity owns — you write a small descriptor by hand that
dispatches the same way. Neither path uses reflection, which is also why both
work under IL2CPP and AOT.

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

Luau.Unity ships no member surface for `GameObject`, `Transform`, or any other
Unity type. That is deliberate: what a script may do to a Unity object is a
decision about your game, not a default the package should make for you. You
write that policy, and you name it explicitly every time you create a handle.

You can author it two ways.

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

The generator implements the capability contract and builds the descriptor for
you; the plain `CreateHandle(target)` overload picks it up. A class is either a
global library or an object capability, never quietly both, and members you
didn't mark don't exist as far as Luau is concerned.

### Manual descriptor for a type you cannot annotate

Unity owns `GameObject`, so you can't put `[LuauLibrary]` on it. Write a small
descriptor instead. This one grants read-only access to `name`, and nothing
else:

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

Then name the target and the policy together:

```csharp
using var target = root.CreateHandle(
    targetGameObject,
    GameCapabilities.ReadableName);
thread["target"] = target;
```

`LuauUnityObjectGuard.ThrowIfDestroyed` runs before every access and rejects
both a plain null and Unity's destroyed-object fake-null. Reach for
`LuauUnityValue` when a descriptor needs to move `UnityEngine.Vector3` values
across the boundary; it does the conversion AOT-safely.

**Getting Started** has one of each: a generated capability for a component it
owns, and a minimal hand-written `GameObject` descriptor. **Full Luau Scripting
Demo** carries larger reusable policies covering the parts of `GameObject`,
`Transform`, 2D physics, rendering, audio, and text its game actually needs.
Importing a sample copies that code into your project where you can edit it. You
are not switching on a package default — there isn't one.

A descriptor can offer a fixed component allowlist, the way that sample's
`GameObject:GetComponent` does. That stays bounded: the script reaches only the
component types you listed, and only on a `GameObject` you already handed it.

### Descriptors are immutable authority

A `LuauObjectDescriptor<T>` is the whole policy. There is no `AddMember`, and
nothing widens a descriptor behind your back. To expose one more member, build a
new descriptor and read the resulting surface top to bottom — that review is the
point.

Identity counts too. Two descriptors over the same object are two different
capabilities, so handing out a wider view later never upgrades the narrow handle
a script is already holding.

### Migrating package-provided Unity handles

`state.CreateHandle(gameObject)` and `state.CreateHandle(transform)` used to
exist, backed by surfaces the package chose. Both are gone. Pick a replacement:

- Import **Full Luau Scripting Demo** and name
  `LuauUnityCapabilities.GameObjectDescriptor`, or another descriptor from its
  `Core/` policy, at the call site.
- Copy those descriptors into your own code and cut them down to taste.
- Write a narrower descriptor, or a wrapper type you own that exposes only what
  the script needs.

After importing the sample, the first option reads like this:

```csharp
using var self = root.CreateHandle(
    gameObject,
    LuauUnityCapabilities.GameObjectDescriptor);
thread["self"] = self;
```

Keep the descriptor visible there. It's the one line that tells a reviewer what
this script was granted — don't hide it behind a helper overload of your own.

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

Note where the target came from: a serialized inspector reference, or something
else your code chose. A descriptor says what a script may do to an object you
hand it; it never helps a script go find one.

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
