# Flappy Bird demo game

Open `Scenes/FlappyBird.unity` and enter Play Mode. Press **Space**, click the
left mouse button, or tap to flap. Pass a pipe pair to score. After a collision,
wait about half a second and use the same input to restart without reloading the
scene.

Everything game-specific in this directory is a Luau script or a Unity asset.
The reusable runtime and Unity-facing capability policy live in `../Core/`.
Delete this `Demo Game/` directory when starting a different project from the
core components.

## Scene bindings

The scene uses one `LuauBehaviourRuntime` and three `LuauBehaviour` components:

| Behaviour | Execution order | `self` | Named object references |
| --- | ---: | --- | --- |
| `GameController.luau` | `-100` | `GameController` | `scoreText` (`TextMesh`), `messageText` (`TextMesh`) |
| `PlayerController.luau` | `0` | `Bird` | `flapAudio` (`AudioSource`), `hitAudio` (`AudioSource`) |
| `PipeController.luau` | `100` | `Pipes` | `bird` (`Transform`), `scoreAudio` (`AudioSource`) |

All three components reference the same runtime host. The controller creates
`shared.phase`, `shared.score`, and `shared.round`; the other scripts coordinate
through those values while keeping their ordinary globals sandbox-local. Each
script aliases the mutable global as `local gameState = shared`; see the core
README for why lifecycle closures should access mutable shared state through
that local.

## Spawned pipes

The pipe pairs are not in the scene. `PipeController.luau` calls
`spawnPrefab("pipePair")` twice in `start`, and the `Pipes` behaviour grants
exactly that: one prefab named `pipePair`, and **Max Spawned Objects** of `2`.
Asking for a third pair, or for a prefab name that is not in the list, is an
error rather than a silent extra object — raise the cap deliberately if you want
more. The behaviour destroys both instances when it shuts down, so leaving Play
Mode leaves no orphans behind.

Each pipe-pair root holds its upper and lower pipe sprites and colliders at a
fixed local gap. The script moves and vertically randomizes the roots it spawned
and never looks inside them, so it needs no hierarchy traversal. The bird prefab
carries a `Rigidbody2D`, `CircleCollider2D`, `SpriteRenderer`, and
`LuauBehaviour`.

## Input

The scene reads keyboard, mouse, and touch through the sample's `Input` library,
which is built on the Input System package. Set **Active Input Handling** to
*Input System Package (New)* or *Both* in Player Settings; the runtime warns in
the Console if no input devices are visible.

## Audio

The three AudioSource objects intentionally have no clips. Assign optional flap,
score, and hit clips in the Inspector; the scripts check `hasClip` before
calling `Play()`.

## Tuning and replacement art

Gravity, flap velocity, pipe speed and spacing, gap-height range, rotation
limits, and world bounds are constants near the top of the Luau scripts. The
PNG files under `Art/` are dependency-free sample sprites and can be replaced
without changing the scripts.
