# Full Luau Scripting Demo

This sample is two independent halves:

- **Core** is the reusable, sample-owned C# host: a `LuauBehaviour` component,
  its runtime, explicit Unity capability descriptors, and generated `Input` and
  `Quaternion` libraries.
- **Demo Game** is the Flappy Bird scene, prefabs, replaceable geometric art,
  and the game's Luau scripts. It contains no C# at all.

Open `Demo Game/Scenes/FlappyBird.unity` and enter Play Mode. Press Space,
left-click, or tap to begin and flap. After a collision, wait about half a
second and press again to start a new round on the same scene objects.

To build something else on this foundation, keep **Core** and delete
**Demo Game**. The Core README covers the behaviour hooks, the explicit
reference model, the shared-table trust boundary, and exactly which Unity
members Luau can see.

## Before you press Play

This sample reads input through the Input System package. Set **Active Input
Handling** to *Input System Package (New)* or *Both* in Player Settings — with
the legacy setting alone the scene gets no input, and the runtime says so in the
Console.

The three AudioSource objects deliberately ship without clips. Assign your own
flap, score, and hit clips whenever you want sound; the Luau scripts already
check `hasClip` before calling `Play`.
