# Flappy Bird demo game

Open `Scenes/FlappyBird.unity` and enter Play Mode. Press **Space**, click the
left mouse button, or tap to flap. Pass a pipe pair to score. After a collision,
wait briefly and use the same input to restart without reloading the scene.

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
| `PipeController.luau` | `100` | `Pipes` | `pipePairOne` (`Transform`), `pipePairTwo` (`Transform`), `bird` (`Transform`), `scoreAudio` (`AudioSource`) |

All three components reference the same runtime host. The controller creates
`shared.phase`, `shared.score`, and `shared.round`; the other scripts coordinate
through those values while keeping their ordinary globals sandbox-local. Each
script aliases the mutable global as `local gameState = shared`; see the core
README for why lifecycle closures should access mutable shared state through
that local.

Each pipe-pair root holds its upper and lower pipe sprites and colliders at a
fixed local gap. `PipeController.luau` moves and vertically randomizes the roots,
so it needs no hierarchy traversal. The bird prefab carries a `Rigidbody2D`,
`CircleCollider2D`, `SpriteRenderer`, and `LuauBehaviour`.

The three AudioSource objects intentionally have no clips. Assign optional flap,
score, and hit clips in the Inspector; the scripts check `hasClip` before
calling `Play()`.

## Tuning and replacement art

Gravity, flap velocity, pipe speed and spacing, gap-height range, rotation
limits, and world bounds are constants near the top of the Luau scripts. The
PNG files under `Art/` are dependency-free sample sprites and can be replaced
without changing the scripts.
