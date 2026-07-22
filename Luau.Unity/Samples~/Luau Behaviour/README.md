# Luau Behaviour sample

1. Import this sample from Package Manager.
2. Add `LuauBehaviourRuntimeSample` to one scene GameObject.
3. Add `LuauBehaviourSample` to a scene GameObject.
4. Assign the runtime host explicitly and assign `LuauBehaviour.luau`.
5. Add one **Scene Object References** entry named `movingSceneObject` and
   assign a scene GameObject.
6. Add one **Prefab References** entry named `movingPrefab` and assign a prefab
   asset.
7. Enter Play Mode. The assigned scene object moves upward, while a new prefab
   instance spawns at the behaviour's position and moves to the right.

The runtime host owns one root and one scheduler for the scene. Each scripted
component owns a sandboxed `LuauScriptInstance`, receives only its own
GameObject as `self`, and receives its explicitly assigned scene objects in the
`refs` table. Prefab assets remain private to the component: the sample-local
`spawnPrefab(name)` capability accepts only configured names and returns a
restricted handle to the new GameObject. Spawned instances belong to the
component and are destroyed with it, after its optional `destroy` hook runs.

The component registers its required `update` export after loading and its
optional `start` export complete. Disabling the component disables its
registration; destroying it calls optional `destroy` best-effort and releases
the registration and instance unconditionally.

The synchronous Update phase limits each hook to 2 ms and 10,000 interrupts,
then stops admitting hooks after a 4 ms aggregate budget. A failed hook disables
only its component. This sample uses Unity `Update` for presentation. Put
deterministic network or simulation ticks in an application-owned fixed-step
phase instead.
