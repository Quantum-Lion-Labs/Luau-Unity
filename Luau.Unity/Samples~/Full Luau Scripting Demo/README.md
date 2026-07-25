# Full Luau Scripting Demo

This sample is deliberately split into two independent folders:

- **Core** contains the reusable, sample-owned C# host, `LuauBehaviour`
  component, explicit Unity capability descriptors, and generated `Input` and
  `Quaternion` libraries.
- **Demo Game** contains the Flappy Bird scene, prefabs, replaceable geometric
  assets, and game-specific Luau scripts. It contains no C#.

Open `Demo Game/Scenes/FlappyBird.unity` and enter Play Mode. Press Space,
left-click, or tap to begin and flap. After a collision, press again to reset
the existing scene objects and start a new round.

To use the scripting foundation for another project, keep **Core** and delete
**Demo Game**. The Core README documents the behavior hooks, explicit reference
model, shared-table trust boundary, and the Unity members visible to Luau.

The three AudioSource objects in the demo intentionally have no clips. Assign
your own flap, score, and hit clips during a polish pass; the Luau scripts
already check `hasClip` and call `Play`.
