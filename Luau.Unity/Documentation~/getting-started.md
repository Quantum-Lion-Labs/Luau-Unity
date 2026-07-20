# Getting started

Luau.Unity embeds the official Luau VM in your game, so you can ship behaviour
as `.luau` text files instead of compiled C#. It's useful for gameplay logic you
want to iterate on without a domain reload, for content your designers own, and
for user-made mods.

The package is a prebuilt managed runtime plus native plugins for Windows x64
and Android ARM64/x86_64. Unity 6000.3.19f1 is the tested minimum.

If a term in these docs is unfamiliar, [concepts and vocabulary](concepts.md)
defines all of them in one page.

## Install

In Package Manager, choose **Add package from git URL** and enter:

```text
https://github.com/Quantum-Lion-Labs/Luau-Unity.git?path=Luau.Unity#v0.2.0
```

Then import **Getting Started** from the package's Samples tab.

## Hello world

Create `hello.luau` anywhere under `Assets`. Unity imports it as a `LuauAsset`,
compiling it once so syntax errors show up in the Console immediately.

```luau
return 21 * 2
```

Drop this on a GameObject and assign the asset:

```csharp
using Luau;
using Luau.Unity;
using UnityEngine;

public sealed class HelloLuau : MonoBehaviour
{
    [SerializeField] LuauAsset script;

    async void Start()
    {
        using var root = LuauUnity.CreateState();
        using var results = await root.ExecuteAsync(script, destroyCancellationToken);

        Debug.Log(results[0].Read<int>()); // 42
    }
}
```

`CreateState` spins up a Luau VM with the standard libraries open and a rate-
limited `print` wired to `Debug.Log`. `ExecuteAsync` compiles the script on a
background thread and resumes on the main thread, so it won't hitch on a large
file.

That's the whole API surface for running a script. The rest of this page is
about the setup you actually want in a real project.

## A practical setup

The hello world above creates and destroys a VM inside one method, which is fine
for a demo and wrong for a game. Two things change at real scale:

- **Create the VM once, not per object.** A root state owns a memory budget and
  a set of host functions. You want one for all your first-party scripts, and
  one more per mod you don't trust.
- **Give each scripted object its own thread.** Threads are cheap and keep
  scripts from stomping on each other's globals.

So: one component owns the runtime, and a second component runs one script per
object.

### 1. Expose your game's API

A `[LuauLibrary]` class becomes a global table in Luau. A source generator
writes the binding code at compile time — no reflection, so this works under
IL2CPP.

```csharp
using Luau;
using Luau.Unity;
using UnityEngine;

[LuauLibrary("game")]
public sealed partial class GameLibrary
{
    [LuauMember("log")]
    public static void Log(string message) => Debug.Log($"[luau] {message}");

    [LuauMember("time")]
    public static double Time() => UnityEngine.Time.timeAsDouble;

    [LuauMember("spawn")]
    public static void Spawn(string prefabId, System.Numerics.Vector3 position)
    {
        // your own vetted lookup — never a raw Resources.Load of a script string
    }
}
```

The class must be `partial` (the generator fills in the other half). Members
without `[LuauMember]` are invisible to Luau, and the generator will fail your
build with a clear error if a signature isn't supported rather than doing
something surprising at runtime.

Note the explicit names. Without one, the Luau-visible name is the C# member
name exactly as written — `public static void Log` becomes `game.Log`, not
`game.log`. Since Luau code usually reads camelCase, pass the name you want.

### 2. Own the runtime in one place

```csharp
using Luau;
using Luau.Unity;
using UnityEngine;

public sealed class LuauRuntime : MonoBehaviour
{
    public static LuauRuntime Instance { get; private set; }

    public LuauState Root { get; private set; }

    void Awake()
    {
        Instance = this;
        Root = LuauUnity.CreateState(new LuauUnityOptions
        {
            ConfigureHostApis = state => state.OpenLibrary(new GameLibrary()),
        });
    }

    void OnDestroy()
    {
        Root?.Dispose();
        Root = null;
        Instance = null;
    }
}
```

Registration has to happen inside `ConfigureHostApis`, because `CreateState`
freezes the globals immediately afterward. Once frozen, scripts can't replace
`game.Spawn` with their own function — but neither can you, so anything a script
needs has to be registered here.

Create the state on the main thread. It captures Unity's synchronization context
so `await` lands back on the main thread where it's safe to touch the scene.

### 3. Run one script per object

Have scripts return a table of functions, the same shape as a Lua module. That
gives you named entry points to call as the game runs, rather than one
fire-and-forget execution.

```luau
-- floater.luau
local transform = self.transform
local riseSpeed = 1.5

local floater = {}

function floater.update(deltaTime)
    transform:Translate(vector.create(0, riseSpeed * deltaTime, 0))
end

game.log("floater ready at " .. game.time())

return floater
```

Everything at the top level runs once, when the host executes the script; only
`floater.update` runs per frame. `self` is the GameObject the host handed in,
`game` is the library registered above, and `vector` is one of the standard
libraries `CreateState` opens.

```csharp
using Luau;
using Luau.Unity;
using UnityEngine;

public sealed class LuauBehaviour : MonoBehaviour
{
    [SerializeField] LuauAsset script;

    LuauState thread;
    LuauFunction update;

    async void Start()
    {
        var root = LuauRuntime.Instance.Root;
        thread = root.CreateSandboxedThread();

        // Hand this script exactly one object: its own GameObject.
        using var handle = root.CreateHandle(gameObject);
        thread["self"] = handle;

        using var results = await thread.ExecuteAsync(script, destroyCancellationToken);

        // The scope owns the returned table, but a value pulled out of a table
        // is ours — so `update` stays valid after the scope is disposed.
        var module = results[0].Read<LuauTable>();
        update = module["update"].Read<LuauFunction>();
    }

    void Update()
    {
        if (update == null) return;

        // Dispose the scope even when ignoring the return value.
        using var results = update.Invoke(new LuauValue[] { (double)Time.deltaTime });
    }

    void OnDestroy()
    {
        update?.Dispose();
        thread?.Dispose();
    }
}
```

Disposing the `handle` at the end of `Start` is correct and not a bug: it
releases the managed wrapper, while the value Luau is holding in `self` stays
valid for as long as the script does.

Now every GameObject with this component runs its own script instance, sees only
its own GameObject, and shares one VM and one host API with the rest.

## Lifetimes, in one place

The disposal rules are the only part of this API that will bite you, so they're
worth reading once:

- **Dispose result scopes before the state they came from.** Not after.
- **Numbers, strings, and booleans are copies.** Read them and forget about them.
- **Tables, functions, buffers, userdata, and object handles are live VM
  references.** A result scope owns the ones sitting directly in it; when you
  need one to outlive the scope, either call `Retain()` or pull it out through a
  table getter, which hands you an owned reference either way.
- **Thread results are shared.** If a script returns a coroutine, you get the
  VM's cached wrapper, not a private one. Dispose it only once everything using
  it is finished.
- **`ExecuteInto` and the other `*Into` methods skip the allocation** and make
  you responsible for every wrapper written into your destination span. Reach for
  them when you're executing on a hot path and have measured a reason to.

## Next steps

- [Concepts and vocabulary](concepts.md) — every term in one page.
- [Capability bindings](capability-bindings.md) — exposing C# objects safely,
  including your own components rather than just `GameObject`.
- [Execution and trust](execution-and-trust.md) — read this before you run a
  script a player wrote.
- [Resource limits](resource-limits.md) — the memory, time, and size ceilings,
  and how to change them without accidentally removing the rest.
- [Modules](modules.md) — `require()` across a set of scripts.
- [Persistent artifacts](artifacts.md) — precompiled bytecode, and why it needs
  authentication.
