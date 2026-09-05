# Luau.Unity

Luau.Unity embeds the official Luau VM behind a bounded managed API for Unity.
It supports editor-authored `.luau` assets, verified first-party bytecode, and
untrusted source-based modding without exposing the native stack or VM handle.

This package is a preview. Version `0.2.0` freezes its first release candidate
API and native ABI, but later preview releases may still contain reviewed
breaking changes.

## Install

In Package Manager, choose **Add package from git URL** and use the exact tag:

```text
https://github.com/Quantum-Lion-Labs/Luau-Unity.git?path=Luau.Unity#v0.3.1
```

The package ID is `com.qll.luau.unity`. Luau.Unity is developed by
[Quantum Lion Labs](https://github.com/Quantum-Lion-Labs), the studio behind
NervBox.

## One ordinary Unity workflow

Create a `.luau` file in `Assets`, assign the imported `LuauAsset`, and execute
it through the shared bounded compiler lane. Keep the returned
`LuauResultScope` and state in `using` declarations so disposable VM-backed
results are released deterministically. A returned child thread is a shared
cached `LuauState` wrapper and is disposed separately after all holders finish.

```csharp
using Luau;
using Luau.Unity;
using UnityEngine;

public sealed class LuauExample : MonoBehaviour
{
    [SerializeField] LuauAsset script;

    async void Start()
    {
        using var state = LuauUnity.CreateState();
        using var results = await state.ExecuteAsync(script, destroyCancellationToken);
        Debug.Log(results[0].Read<long>());
    }
}
```

`LuauUnity.CreateState()` supplies finite defaults and captures Unity's current
synchronization context. Create it on the Unity thread. A root owns all child
threads, VM-backed values, capabilities, module cache entries, and operations;
dispose the root last.

The importer is source-only by default. First-party precompile is an explicit
project policy and still requires runtime artifact authentication. Import
admission is separate from runtime mod-source limits.

For package-managed authentication, select **First-party precompile with
generated manifest** in Project Settings, configure a provenance ID, opt trusted
assets into **Precompile**, and set `LuauUnityOptions.UseFirstPartyBytecode =
true`. A normal player build regenerates the package-owned manifest. Unchecked
assets remain source, so first-party scripts and mods can coexist. See
[Persistent artifacts](Documentation~/artifacts.md) for the snapshot and remote
content security boundary.

## Capability policy

There is no built-in Luau surface for `GameObject` or `Transform`. What a script
may do to a Unity object is your decision: generate a capability for a type you
annotated, or write a `LuauObjectDescriptor<T>` for one you can't and name it at
`state.CreateHandle(target, descriptor)`. See
[exposing C# to Luau](Documentation~/capability-bindings.md#object-capabilities).

## Learn more

- [Getting started](Documentation~/getting-started.md)
- [Execution and trust lanes](Documentation~/execution-and-trust.md)
- [Capability bindings](Documentation~/capability-bindings.md)
- [Resource limits](Documentation~/resource-limits.md)
- [Module trust domains](Documentation~/modules.md)
- [Script instances and host archetypes](Documentation~/script-instances.md)
- [Persistent artifacts](Documentation~/artifacts.md)
- [Compiler residual risk](Documentation~/compiler-security.md)
- [Changelog](CHANGELOG.md)
- [Third-party notices](Third%20Party%20Notices.md)

The Package Manager **Samples** tab has two importable examples: **Getting
Started** for focused API lessons, and **Full Luau Scripting Demo** for reusable
game-scripting components plus a Luau-only Flappy Bird game — keep its `Core/`
and delete `Demo Game/` to start your own.

Only Windows x64 and Android ARM64/x86_64 are maintained targets.
