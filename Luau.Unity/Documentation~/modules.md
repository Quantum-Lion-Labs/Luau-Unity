# Modules and `require()`

`require()` is off unless you turn it on, and when you turn it on it does not
touch the filesystem. It won't read files, hit the network, or look in
`Resources` or Addressables. Instead you give the root a fixed set of named
scripts, and `require("foo")` either finds one of those or fails.

That's a deliberate trade. It means a mod can't require its way into your
`StreamingAssets` folder, and it means you decide what a script can pull in
before it ever runs.

## Setting it up

Load and authenticate the mod package yourself, outside the VM. Then build an
immutable `LuauModuleMap` from it and hand it to the root:

```csharp
var moduleMap = new LuauModuleMap(
    new Dictionary<string, byte[]>
    {
        ["shared/math"] = sharedMathSourceUtf8,
        ["features/inventory"] = inventorySourceUtf8,
    },
    new Dictionary<string, string>
    {
        ["mod"] = "features",   // require("@mod/inventory") → features/inventory
    });

using var root = LuauUnity.CreateState(new LuauUnityOptions
{
    ModuleMap = moduleMap,
});
```

The second dictionary is optional aliases. The map copies both the sources and
the aliases, so nothing you pass in can change underneath it afterward.

IDs are canonicalized, so `foo`, `./foo`, `/foo`, and `foo.luau` all resolve to
the same module and get one cached result. Traversal to a parent directory is
rejected outright.

To compile everything up front on the background queue rather than on first
`require`, build a bundle instead:

```csharp
var bundle = await LuauUnity.CompileModuleBundleAsync(
    moduleMap,
    cancellationToken: destroyCancellationToken);

using var root = LuauUnity.CreateState(new LuauUnityOptions
{
    ConfigureHostApis = state => state.OpenRequireLibrary(bundle),
});
```

Each module runs in a fresh sandboxed thread and must return exactly one value.
Modules can't see each other's private globals.

## One root, one module namespace

Module instances and their cached results are shared according to the root's
resolver identity — not by path. Two roots with identically named modules get
genuinely separate instances, with separate state.

So mods that shouldn't be able to see each other need separate roots, even when
their module names happen to collide. Especially then.

Module policy caps module count, admitted source and bytecode, dependency depth,
diagnostics, cached result count, and retained managed cache string bytes. See
[resource limits](resource-limits.md).

## Immutability

Maps and bundles can't be modified after construction. There's no API to swap a
module in place, and that's intentional — hot-swapping a module underneath
scripts that already required it is how you get state that doesn't match code.

To change a module, build a new map or bundle, which gives you a new resolver
identity. When you need a clean slate or a full version change, use a new root.
Closing a root releases every module instance and everything the cache was
holding.

Bundle construction is all-or-nothing. A cancellation, a compile error, an
identity mismatch, or any quota failure leaves you with nothing installable — it
can't hand you a half-built bundle and it can't mutate a resolver that's already
in use.

Circular requires fail deterministically rather than hanging or recursing.

A bundle is not a persistent artifact, and building one grants no bytecode trust.
See [artifacts](artifacts.md) for that.
