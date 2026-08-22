# Getting Started sample

1. Import this sample from Package Manager.
2. Add `GettingStartedSample` and `GettingStartedTarget` to a GameObject.
3. Assign `GettingStarted.luau`, the `GettingStartedTarget` component, and any
   GameObject to the sample component's three fields.
4. Enter Play Mode. The Console reports the returned score `42` and the renamed
   target.

## What to read

[Getting started](../../Documentation~/getting-started.md) walks through this
code as six lessons. The short version of what each file is for:

- **`GettingStartedSample.cs`** creates and disposes the root state, registers
  the `sample` host library before the VM freezes its globals, builds a sandboxed
  thread, and assigns exactly two handles to it. It also defines
  `GameObjectNameDescriptor`, the hand-written policy that grants `name` on one
  `GameObject` and nothing else — Unity's `GameObject` can't be annotated, so a
  manual descriptor is the way in.
- **`GettingStartedTarget.cs`** is a component the sample owns, so it can use
  `LuauLibraryExposure.Capability` and let the generator write its descriptor.
  Only `score` and `increment` are marked; the rest of `MonoBehaviour` stays
  invisible.
- **`GettingStarted.luau`** uses those two surfaces and nothing else. There is no
  scene lookup available to it.

The sample creates and destroys a VM inside one method, which is fine here and
wrong in a game. For the real shape — one VM shared across your game, one
sandboxed thread per scripted object — see the doc above, then import **Full Luau
Scripting Demo** for a working version of it.
