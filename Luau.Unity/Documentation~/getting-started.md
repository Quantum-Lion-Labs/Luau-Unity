# Getting started

Luau.Unity embeds the official Luau VM in your game, so you can ship behaviour as
`.luau` text files instead of compiled C#. It's useful for gameplay logic you want
to iterate on without a domain reload, for content your designers own, and for
mods your players write. All three run through the same bounded runtime; the only
thing that changes for the ones you don't trust is how tight you set the limits.

The package is a prebuilt managed runtime plus native plugins for Windows x64 and
Android ARM64/x86_64. It requires Unity 6000.3.0f1 or newer in the 6000.3 stream.
If a term on this page is unfamiliar,
[concepts and vocabulary](concepts.md) defines all of them in one place.

## Install

In Package Manager, choose **Add package from git URL** and enter:

```text
https://github.com/Quantum-Lion-Labs/Luau-Unity.git?path=Luau.Unity#v0.3.0
```

Two samples come with it. **Getting Started** is the one this page walks through.
**Full Luau Scripting Demo** is a reusable game-scripting kit plus a Flappy Bird
game whose gameplay is entirely Luau — worth importing once you've read this far.

## Hello world

Create `hello.luau` anywhere under `Assets`. Unity imports it as a `LuauAsset`,
compiling it once so syntax errors reach the Console immediately:

```luau
return 21 * 2
```

Drop this component on a GameObject and assign the asset:

```csharp
using Luau;
using Luau.Unity;
using UnityEngine;

public sealed class HelloLuau : MonoBehaviour
{
    [SerializeField] LuauAsset script;

    async void Start()
    {
        using var root = LuauUnity.CreateState();
        using var results = await root.ExecuteAsync(script, destroyCancellationToken);

        Debug.Log(results[0].Read<int>()); // 42
    }
}
```

`CreateState` spins up a Luau VM with the standard libraries open and a
rate-limited `print` wired to `Debug.Log`. `ExecuteAsync` compiles on a background
thread and resumes on the main one, so a large file won't hitch your frame.

That's the whole API for running a script. Everything below is the setup you want
once more than one script is involved.

## Six lessons

The **Getting Started** sample keeps each boundary visible, one mechanism at a
time. Read them in order the first time. In a real project you'd split them
between a runtime owner and the components that run scripts.

### 1. Create and dispose a state

A root `LuauState` is one VM and one trust domain. Create it on Unity's main
thread so it captures the synchronization context, and dispose it after every
child thread, result, and VM-backed value:

```csharp
using var root = LuauUnity.CreateState();
```

In a real game the root lives in an owning component and gets disposed in
`OnDestroy`. Create a few of them around trust boundaries, not one per GameObject:
your own scripts can share a root, but two mods that don't trust each other
belong in separate ones.

### 2. Execute a LuauAsset and own its results

The hello world above already did this. What matters is the `using`:

```csharp
using var results = await root.ExecuteAsync(script, destroyCancellationToken);
```

`LuauResultScope` owns any VM-backed value in the result, so keep it in a `using`
declaration even when you ignore what came back. Numbers and strings inside it are
copies; tables and functions are live references that die with the scope.

### 3. Generate and register a host library

A global host library is visible to every script in the root. Mark a partial class
and the members you want reachable:

```csharp
[LuauLibrary("sample")]
public sealed partial class GettingStartedLibrary
{
    [LuauMember("double")]
    public static int Double(int value) => checked(value * 2);
}
```

The explicit name matters. Without `[LuauMember("double")]` the Luau name is the
C# spelling, `sample.Double` — nothing converts it to camelCase for you.

Register global libraries while the state is being created:

```csharp
using var root = LuauUnity.CreateState(new LuauUnityOptions
{
    ConfigureHostApis = state =>
        state.OpenLibrary(new GettingStartedLibrary()),
});
```

It has to happen there, because `CreateState` freezes the globals as its last
step. After that nobody can replace your API — scripts can't, and neither can you.

### 4. Generate a capability for a type you own

A capability is a single object handed to a single script, rather than a global
every script sees. For a type you own, ask for capability exposure and mark only
the members Luau may touch:

```csharp
[LuauLibrary("GettingStartedTarget",
    Exposure = LuauLibraryExposure.Capability)]
public sealed partial class GettingStartedTarget : MonoBehaviour
{
    [LuauMember("score")]
    public int Score { get; set; }

    [LuauMember("increment")]
    public void Increment(int amount)
    {
        Score = checked(Score + amount);
    }
}
```

The generator writes an AOT-safe descriptor and implements the capability
contract, so the plain `CreateHandle(target)` overload finds it:

```csharp
using var targetHandle = root.CreateHandle(target);
```

Unmarked members simply aren't there. The script gets no managed pointer, can't
reflect over the target, and can't discover any other component.

### 5. Define a manual descriptor for an external type

You can't annotate Unity's `GameObject`, so when you need to expose one you write
the descriptor yourself. The sample grants read/write access to one object's
`name` and nothing more:

```csharp
public static class GettingStartedUnityCapabilities
{
    public static readonly LuauObjectDescriptor<GameObject>
        GameObjectNameDescriptor = new LuauObjectDescriptor<GameObject>(
            "NamedGameObject",
            LuauUnityObjectGuard.ThrowIfDestroyed,
            new[]
            {
                LuauObjectMember<GameObject>.Property(
                    "name",
                    (target, context) => context.Return(target.name),
                    (target, context) =>
                        target.name = context.Read<string>(2)),
            });
}
```

The Unity guard runs before every access and rejects a destroyed target. When a
descriptor needs to pass vectors, `LuauUnityValue.ReadVector3` and `ReturnVector3`
handle the conversion AOT-safely.

Descriptors are complete, immutable policies — there's no `AddMember`, and nothing
widens one behind your back. To expose another member, write a new descriptor and
review the whole surface.

### 6. Inject only the handles you chose

Create a child environment, pick the policies, and hand over exactly those values:

```csharp
using var thread = root.CreateSandboxedThread();
using var generatedHandle = root.CreateHandle(generatedTarget);
using var namedHandle = root.CreateHandle(
    namedTarget,
    GettingStartedUnityCapabilities.GameObjectNameDescriptor);

thread["generatedTarget"] = generatedHandle;
thread["namedTarget"] = namedHandle;

using var results = await thread.ExecuteAsync(
    script,
    destroyCancellationToken);
```

The script can use precisely those two surfaces:

```luau
generatedTarget.score = sample.double(20)
generatedTarget:increment(2)
namedTarget.name = "Named by a narrow Luau capability"

return generatedTarget.score, namedTarget.name
```

The first value comes back `42`, the second confirms the rename. The script can't
search the scene, enumerate components, or reach any other `GameObject`.

Disposing each handle after you assign it releases the managed wrapper only; the
value stored in the thread stays valid until Luau lets go of it. Dispose the
thread before the root.

## Moving to a reusable behaviour

Getting Started teaches mechanisms; it isn't a framework. Import **Full Luau
Scripting Demo** for a real composition: a shared trust-domain root, one sandboxed
thread per component, bounded `Update`, `FixedUpdate`, and `LateUpdate` phases,
failure isolation that disables one component instead of the scene, explicit Unity
object references, and prefab spawning against a per-behaviour cap.

Its `Core/` holds the reusable host and the `LuauUnityCapabilities` descriptors.
`Demo Game/` is the Flappy Bird example, and you can delete it when starting
something else.

Migrating off the removed `state.CreateHandle(gameObject)` or
`state.CreateHandle(transform)` overloads? The replacements are in
[capability bindings](capability-bindings.md#migrating-package-provided-unity-handles).

## Optional: ship trusted scripts as bytecode

Source is the safe default and stays the right format for mods. To skip runtime
compilation for selected first-party assets:

1. Open **Project Settings > Luau.Unity**, choose **First-party precompile with
   generated manifest**, and enter your public provenance ID.
2. Select trusted `.luau` assets and enable **Precompile** in each importer. Leave
   mod or source-only assets unchecked.
3. Set `UseFirstPartyBytecode = true` in `LuauUnityOptions`.
4. Build normally. Luau.Unity regenerates and embeds the manifest approving the
   current project snapshot.

The generated manifest folder at `Assets/Generated/Luau.Unity` is package-owned
and can be ignored by source control.

Bytecode rebuilt or added after the player build needs a newly built player
manifest. That includes remote Addressables or AssetBundle updates. See
[precompiled bytecode](artifacts.md) for the security boundary and the custom
validator path.

## Next steps

- [Concepts and vocabulary](concepts.md) — every term in one page.
- [Exposing C# to Luau](capability-bindings.md) — generated and manual object
  policies, callbacks, and ownership.
- [Script instances](script-instances.md) — reusable exports and schedulers.
- [Execution and trust](execution-and-trust.md) — read this before running a
  script a player wrote.
- [Resource limits](resource-limits.md) — memory, time, and size ceilings.
- [Modules](modules.md) — managed `require()` over immutable module maps.
- [Precompiled bytecode](artifacts.md) — authentication for trusted artifacts.
