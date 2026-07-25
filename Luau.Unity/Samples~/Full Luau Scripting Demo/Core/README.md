# Reusable Luau scripting core

This directory is the reusable half of the **Full Luau Scripting Demo**. You
can delete the sibling `Demo Game` directory and keep everything here as a
starter kit for another project.

## Scene setup

1. Add one `LuauBehaviourRuntime` to the scene. It owns one Luau root, one
   mutable `shared` table, and bounded `Update`, `FixedUpdate`, and
   `LateUpdate` phases.
2. Add `LuauBehaviour` to each scripted GameObject.
3. Assign the runtime, a `.luau` asset, and an execution order.
4. Add only the Unity objects the script needs to **Object References**. They
   appear by name in `refs`.
5. Optionally add prefab assets to **Prefab References**. The script can call
   `spawnPrefab(name)` only for those entries and only up to that behaviour's
   finite **Max Spawned Objects** limit. Leave prefab references empty for
   scripts that do not need instantiation authority.

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
`GameObject`; collision enter also receives contact-point and normal vectors.

## Unity-like capability API

The editable `LuauUnityCapabilities` class is the complete authority policy.
It exposes explicit descriptors for `GameObject`, `Transform`, `Rigidbody2D`,
`Collider2D`, `SpriteRenderer`, `AudioSource`, and `TextMesh`.

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
`GetMouseButtonDown`, `GetMouseButton`, `touchCount`, and `GetTouchPhase`.
It intentionally uses Unity's built-in input API. A project configured for
only the new Input System should replace this editable library or enable
legacy/both input handling.

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

Each per-frame lifecycle or physics hook has a 2 ms wall-clock limit, a
10,000-interrupt limit, and may return no results. Each phase has a 4 ms
aggregate admission budget. One-time host-library, script, and `start`
initialization uses a separate bounded 50 ms / 100,000-interrupt lane; the
script chunk accepts only its single export table and `start` accepts no
results. A failing hook disables only its owning `LuauBehaviour`.

The first-party-bytecode runtime option is off by default. Enable it only for
bytecode produced and admitted by the package's generated first-party
manifest; downloaded or otherwise untrusted scripts should remain source.
