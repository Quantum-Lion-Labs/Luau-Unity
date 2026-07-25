# Getting started

Luau.Unity embeds the official Luau VM in your game, so you can ship behaviour
as `.luau` text files instead of compiled C#. It supports first-party gameplay
logic and untrusted source-based mods through the same bounded managed runtime.

The package is a prebuilt managed runtime plus native plugins for Windows x64
and Android ARM64/x86_64. Unity 6000.3.19f1 is the tested minimum. If a term on
this page is unfamiliar, see [concepts and vocabulary](concepts.md).

## Install

In Package Manager, choose **Add package from git URL** and enter:

```text
https://github.com/Quantum-Lion-Labs/Luau-Unity.git?path=Luau.Unity#v0.2.0
```

Then import **Getting Started** from the package's Samples tab. The package has
exactly two samples: Getting Started teaches individual mechanisms, while
**Full Luau Scripting Demo** contains reusable game-scripting components,
editable Unity capability policies, and a Luau-only Flappy Bird game.

## The six lessons

The Getting Started sample keeps each boundary visible. Follow these lessons in
order the first time; in an application you can split them across your own
runtime owner and scripted components.

### 1. Create and dispose a state

A root `LuauState` is one VM and one trust domain. Create it on Unity's main
thread so it captures the synchronization context, and dispose it after all
child threads, results, and VM-backed values:

```csharp
using var root = LuauUnity.CreateState();
```

For a long-lived game runtime, store the root in an owning component and dispose
it in `OnDestroy`. Create a few roots around trust boundaries, not one per
GameObject: first-party scripts can share a root, while mutually untrusted mods
belong in separate roots.

### 2. Execute a LuauAsset and own its results

Create `answer.luau` anywhere under `Assets`. Unity imports it as a `LuauAsset`
and reports syntax errors in the Console:

```luau
return 21 * 2
```

Assign the asset to a serialized field and execute it through the bounded
background compiler lane:

```csharp
[SerializeField] LuauAsset script;

async void Start()
{
    using var root = LuauUnity.CreateState();
    using var results = await root.ExecuteAsync(
        script,
        destroyCancellationToken);

    Debug.Log(results[0].Read<int>()); // 42
}
```

`ExecuteAsync` compiles away from the Unity main thread and resumes on it. The
`LuauResultScope` owns any VM-backed values directly in the result, so keep the
scope in a `using` declaration even when you ignore the return values.

### 3. Generate and register a host library

A global host library is visible to every script in the root. Mark a partial
application type and its allowed members:

```csharp
[LuauLibrary("sample")]
public sealed partial class GettingStartedLibrary
{
    [LuauMember("double")]
    public static int Double(int value) => checked(value * 2);
}
```

The explicit member name matters. Without `[LuauMember("double")]`, the
generated Luau name is the C# spelling `sample.Double`; it is not automatically
converted to `sample.double`.

Register global libraries while the state is being created:

```csharp
using var root = LuauUnity.CreateState(new LuauUnityOptions
{
    ConfigureHostApis = state =>
        state.OpenLibrary(new GettingStartedLibrary()),
});
```

`CreateState` freezes globals after `ConfigureHostApis` returns, so scripts
cannot replace the registered API and the host cannot widen it later.

### 4. Generate a capability for a type you own

An object capability is a per-object value rather than a root-wide global
library. For an application-owned type, request capability exposure and mark
only the members Luau may reach:

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

The source generator creates an AOT-safe descriptor and implements the
capability contract for this type. The generic `CreateHandle(target)` extension
selects that generated descriptor:

```csharp
using var targetHandle = root.CreateHandle(target);
```

Unmarked members are absent. The script receives no managed pointer and cannot
reflect over the target or discover other components.

### 5. Define a manual descriptor for an external type

You cannot annotate Unity's `GameObject` class. Define a manual descriptor when
you need to expose an external type. Getting Started grants only read/write
access to one object's `name`:

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

The Unity guard runs before every access and rejects a destroyed target.
`LuauUnityValue.ReadVector3` and `ReturnVector3` are available when an
application descriptor needs vector conversion.

Descriptors are complete immutable policies. The runtime has no `AddMember`
operation and never silently widens a descriptor. To expose another member,
construct and review a new descriptor. Its identity remains separate from any
narrower handle over the same managed object.

### 6. Inject only selected handles into a sandboxed thread

Create a child environment, select the generated and manual policies, and hand
only those values to the script:

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

The sample script can use precisely those surfaces:

```luau
generatedTarget.score = sample.double(20)
generatedTarget:increment(2)
namedTarget.name = "Named by a narrow Luau capability"

return generatedTarget.score, namedTarget.name
```

The first returned value is `42`; the second confirms the new name. The script
cannot search the scene, enumerate components, or reach any other `GameObject`.
Installing Luau.Unity alone grants no predefined `GameObject` or `Transform`
members.

Disposing each managed handle after assignment releases that wrapper; the value
stored in the thread remains valid until Luau releases it. Dispose the thread
before the root.

## Moving to a reusable behaviour

Getting Started is intentionally instructional, not a production framework.
Import **Full Luau Scripting Demo** for a complete `LuauScriptInstance`
composition with a shared trust-domain root, one sandboxed thread per
component, bounded `Update`, `FixedUpdate`, and `LateUpdate` phases, failure
isolation, explicit Unity object references, and controlled prefab spawning
with owned cleanup.

Its `Core/` directory contains the reusable host and editable
`LuauUnityCapabilities` descriptors. `Demo Game/` is a Luau-only Flappy Bird
example that can be deleted when starting another game. The descriptors remain
application-owned policy, not package runtime defaults.

If migrating from the removed `state.CreateHandle(gameObject)` or
`state.CreateHandle(transform)` overloads, see the options and direct mappings
in [capability bindings](capability-bindings.md#migrating-package-provided-unity-handles).

## Optional: ship trusted scripts as bytecode

Source is the safe default and remains the right format for mods. To skip
runtime compilation for selected first-party assets:

1. Open **Project Settings > Luau.Unity**, choose **First-party precompile with
   generated manifest**, and enter your public provenance ID.
2. Select trusted `.luau` assets and enable **Precompile** in each importer.
   Leave mod or source-only assets unchecked.
3. Set `UseFirstPartyBytecode = true` in `LuauUnityOptions`.
4. Build normally. Luau.Unity regenerates and embeds the manifest approving the
   current project snapshot.

The generated manifest folder at `Assets/Generated/Luau.Unity` is package-owned
and may be ignored by source control.

Bytecode rebuilt or added after the player build, including
remote Addressables or AssetBundle updates, needs a newly built player manifest. See
[precompiled bytecode](artifacts.md) for the security boundary and custom
validator path.

## Next steps

- [Concepts and vocabulary](concepts.md) — every term in one page.
- [Capability bindings](capability-bindings.md) — generated and manual object
  policies, callbacks, and ownership.
- [Script instances](script-instances.md) — reusable exports and schedulers.
- [Execution and trust](execution-and-trust.md) — read this before running a
  script a player wrote.
- [Resource limits](resource-limits.md) — memory, time, and size ceilings.
- [Modules](modules.md) — managed `require()` over immutable module maps.
- [Precompiled bytecode](artifacts.md) — authentication for trusted artifacts.
