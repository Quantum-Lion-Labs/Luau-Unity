# Reusable Luau scripting core

This directory is the reusable half of the **Full Luau Scripting Demo**. You
can delete the sibling `Demo Game` directory and keep everything here as a
starter kit for another project.

## Scene setup

1. Add one `LuauBehaviourRuntime` to the scene. It owns one Luau root, one
   mutable `shared` table, bounded `Update`, `FixedUpdate`, and
   `LateUpdate` phases, and the host's limits under **Host Limits**: the
   runtime's **Max Spawned Objects** and its **Input Policy** (see
   [Trust and limits](#trust-and-limits)).
2. Add `LuauBehaviour` to each scripted GameObject.
3. Assign the runtime, a `.luau` asset, and an execution order.
4. Add only the Unity objects the script needs to **Object References**. They
   appear by name in `refs`.
5. Optionally add prefab assets to **Prefab References**. The script can call
   `spawnPrefab(name)` only for those entries, and only while both the
   behaviour's own **Max Spawned Objects** and the runtime's are below their
   limits; the behaviour destroys what it spawned when it shuts down. Leave
   prefab references empty for scripts that do not need instantiation
   authority. A prefab that contains a `LuauBehaviourRuntime` is refused when
   the behaviour starts. `Demo Game` uses this for its pipe pairs: one named
   prefab, a cap of two, and no pipes in the scene at all.

The runtime initializes attached behaviours sequentially. Lower execution
orders initialize and run first; ties use a stable scene-hierarchy order.
Behaviours attached later are initialized by the same serialized pump, and
phase dispatch pauses until the pump finishes.

Each script must return a table. Every lifecycle export is optional:

```luau
return {
    start = function() end,
    update = function(deltaTime) end,
    fixedUpdate = function(fixedDeltaTime) end,
    lateUpdate = function(deltaTime) end,
    collisionEnter2D = function(other, point, normal) end,
    collisionExit2D = function(other) end,
    triggerEnter2D = function(other) end,
    triggerExit2D = function(other) end,
    destroy = function() end,
}
```

`self` is the behaviour's `GameObject`. Physics callbacks receive the other
object; collision enter also receives contact-point and normal vectors.
Touching an object grants nothing. The other object arrives as a full
`GameObject` capability only when the behaviour already has authority over it:
it is the behaviour's own GameObject, the GameObject of one of its Object
References, or an object it spawned. Anything else arrives as a read-only
`GameObjectView` (see below).

## Unity-like capability API

The editable `LuauUnityCapabilities` class is the complete authority policy.
It exposes explicit descriptors for `GameObject`, `Transform`, `Rigidbody2D`,
`Collider2D`, `SpriteRenderer`, `AudioSource`, and `TextMesh`, and two
read-only views for objects a physics callback hands over without authority:

- `GameObjectView`: `name`, `tag`, `layer`, `activeSelf`, `activeInHierarchy`,
  `CompareTag(tag)`, and `transform`, which is a `TransformView` with
  `position` and `rotation`.
- Neither view has any other member: no `SetActive`, no `GetComponent`, and no
  way to reach a component. Calling either method on a view is an ordinary Luau
  error (`attempt to call missing method 'SetActive' of userdata`). Setting
  any of their properties fails the hook with, for example,
  `Cannot set 'name': GameObject 'Pipe' was not granted to this script. A script
  can change only its own GameObject, its Object References, and objects it
  spawned.`

To let a script change an object it touches, add that object (or one of its
components) to the behaviour's Object References.

`GameObject:GetComponent(typeName)` is case-sensitive and accepts only:

- `Transform`
- `Rigidbody2D`
- `Collider2D`
- `SpriteRenderer`
- `AudioSource`
- `TextMesh`

A supported component that is absent returns `nil`; any other type name is an
error. There is no reflection, scene search, component enumeration, Resources
access, hierarchy traversal, or arbitrary instantiation.

Unity vectors use Luau's `vector` value. A 2D vector uses `z = 0`. Quaternions
are copied `{ x, y, z, w }` tables, and colors are copied
`{ r, g, b, a }` tables. The generated `Quaternion` global provides `Euler`,
`AngleAxis`, `Inverse`, `Lerp`, `Slerp`, `Multiply`, and `ToEulerAngles`.

The generated `Input` global provides `GetKeyDown`, `GetKey`,
`GetMouseButtonDown`, `GetMouseButton`, `touchCount`, and `GetTouchPhase`. It
polls the Input System package directly: key names are `UnityEngine.InputSystem.Key`
names, mouse indexes `0`–`4` map to left, right, middle, back, and forward, and
touch phases come back as `Began`, `Moved`, `Stationary`, `Ended`, or `Canceled`.

Each runtime opens its own `Input` with the runtime's **Input Policy**: the key
names its scripts may read, the mouse buttons they may read, and whether they
may read touch. The default is what `Demo Game` reads and nothing more: `Space`,
the left mouse button, and touch. Anything else is a script error rather than a
quiet `false`, for example
`Input key 'A' is not enabled for this Luau runtime. Add it to the runtime's
Input Policy keys to let scripts read it.` The policy is read once when the
runtime wakes; an unknown key name in it stops the runtime from starting. To
change the default for every new runtime, edit `LuauInputLibrary.Policy`.

Set **Active Input Handling** to *Input System Package (New)* or *Both* in
Player Settings. A device the player does not have reports no activity rather
than failing, so a missing keyboard is not an error; the runtime warns once if
the Input System sees no devices at all. If your game binds input through
actions instead of polled devices, replace this editable library — the scripts
only depend on the six names above.

## Trust and limits

Every script under one runtime receives the same mutable `shared` table. This
is convenient for cooperating game scripts, but it also lets scripts exchange
state and capabilities. Put mutually untrusted publishers in different
runtime roots and never share this table across trust domains.

Alias `shared` once at script scope and use that local in lifecycle closures:

```luau
local gameState = shared

return {
    update = function(_deltaTime)
        gameState.score += 1
    end,
}
```

Luau can optimize a direct global access such as `shared.phase` into an import.
That optimization is appropriate for immutable library globals, but it can
cache a nested value that cooperating behaviours intend to mutate.

Two different people set the spawn limits. A behaviour's **Max Spawned
Objects** belongs to whoever authors that behaviour's scene or prefab, so a
prefab's own limit says nothing about how many objects its copies may make
between them. The runtime's **Max Spawned Objects** (64 by default) belongs to
the host: it counts every live object spawned by any behaviour on that runtime,
including behaviours on spawned objects, and a spawn must pass both limits.
Destroyed objects stop counting. Because a spawned `LuauBehaviourRuntime` would
start a new root with limits of its own, a behaviour refuses to start with a
prefab reference that contains one.

The runtime's **Input Policy** also belongs to the host. It is fixed while the
runtime runs and applies whenever one of its scripts runs; the kit cannot tell
when the player is typing into a text field. If scripts must not observe that
input, leave those keys out of the policy, or disable the runtime's behaviours
while the field has focus.

Each per-frame lifecycle or physics hook has a 2 ms wall-clock limit and a
10,000-interrupt limit; anything a hook returns is ignored. Each phase has a
4 ms aggregate admission budget. One-time host-library, script, and `start`
initialization uses a separate bounded 50 ms / 100,000-interrupt lane; the
script chunk accepts only its single export table, and anything `start`
returns is ignored. A failing hook disables only its owning `LuauBehaviour`.

The first-party-bytecode runtime option is off by default. Enable it only for
bytecode produced and admitted by the package's generated first-party
manifest; downloaded or otherwise untrusted scripts should remain source.
