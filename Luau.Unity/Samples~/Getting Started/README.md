# Getting Started sample

1. Import this sample from Package Manager.
2. Add `GettingStartedSample` and `GettingStartedTarget` to a GameObject.
3. Assign `GettingStarted.luau`, the `GettingStartedTarget` component, and an
   explicit GameObject to the sample component's three fields.
4. Enter Play Mode and observe the returned score `42` and the renamed target.

## Lessons

### 1. Create and dispose a state

`GettingStartedSample.Start` creates and deterministically disposes a root
`LuauState`. A real game normally keeps a root alive across many scripts, but
the owner must still dispose it after every child thread and VM-backed value.

### 2. Execute an asset and own its results

The sample executes a `LuauAsset` and reads values only while the returned
`LuauResultScope` is alive. Result values are references into the VM, so retain
or copy what you need before disposing that scope.

### 3. Generate and register a host library

`GettingStartedLibrary` is annotated with `[LuauLibrary("sample")]` and is
registered inside `ConfigureHostApis`, before the VM freezes its globals. The
managed `Double` method has the explicit `[LuauMember("double")]` name override,
so the script calls `sample.double(20)`.

### 4. Generate a capability for an application type

`GettingStartedTarget` is application-owned, so it can use
`LuauLibraryExposure.Capability`. Only its annotated `score` property and
`increment` method enter the generated, reflection-free descriptor. Creating a
handle for that component does not expose the rest of `MonoBehaviour`.

### 5. Describe an external type manually

Unity's `GameObject` cannot be annotated by your application.
`GettingStartedUnityCapabilities.GameObjectNameDescriptor` therefore constructs
a manual `LuauObjectDescriptor<GameObject>` with only a `name` member and the
Unity destroyed-object guard. Descriptors are immutable authority values: make
a different descriptor when a script should receive a narrower or wider view.
The **Full Luau Scripting Demo** sample contains broader, reusable policies
modeled after the supported parts of Unity's GameObject, Transform, 2D physics,
rendering, audio, and text APIs.

### 6. Inject only explicit authority

The component creates a sandboxed thread, assigns only the generated target and
name-only GameObject handles, and executes the script there. The script has no
ambient scene lookup and cannot reach Unity members that those two descriptors
did not grant.

See [Getting started](../../Documentation~/getting-started.md) for the setup you
want in a real project: one VM shared across your game, and one sandboxed thread
per scripted object.
