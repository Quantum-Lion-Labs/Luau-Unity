# Getting Started sample

1. Import this sample from Package Manager.
2. Add `GettingStartedSample` to a GameObject.
3. Assign `GettingStarted.luau` to the component's **Script** field.
4. Enter Play Mode and observe `Luau returned 42`.

## What it shows

The component creates a Luau VM, exposes one C# method to it as `sample.double`,
runs a script that calls that method, and disposes everything on the way out.

Two details worth copying into your own code:

- Host APIs are registered inside `ConfigureHostApis`. That's required — the VM
  freezes its globals immediately afterward, so anything registered later can't
  get in.
- The result scope is disposed with `using`. Values in it are live references
  into the VM and don't outlive the scope unless you retain them.

See [Getting started](../../Documentation~/getting-started.md) for the setup you
want in a real project: one VM shared across your game, and one sandboxed thread
per scripted object.
