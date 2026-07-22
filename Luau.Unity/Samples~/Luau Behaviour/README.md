# Luau Behaviour sample

1. Import this sample from Package Manager.
2. Add `LuauBehaviourRuntimeSample` to one scene GameObject.
3. Add `LuauBehaviourSample` to the GameObject you want the script to move.
4. Assign the runtime host explicitly and assign `LuauBehaviour.luau`.
5. Enter Play Mode.

The runtime host owns one root and one scheduler for the scene. Each scripted
component owns a sandboxed `LuauScriptInstance`, receives only its own
GameObject as `self`, and registers its required `update` export after loading
and its optional `start` export complete. Disabling the component disables its
registration; destroying it calls optional `destroy` best-effort and releases
the registration and instance unconditionally.

The synchronous Update phase limits each hook to 2 ms and 10,000 interrupts,
then stops admitting hooks after a 4 ms aggregate budget. A failed hook disables
only its component. This sample uses Unity `Update` for presentation. Put
deterministic network or simulation ticks in an application-owned fixed-step
phase instead.
