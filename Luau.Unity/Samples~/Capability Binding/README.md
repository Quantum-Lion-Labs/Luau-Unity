# Capability Binding sample

1. Import this sample from Package Manager.
2. Add `CapabilityBindingSample` to a GameObject.
3. Assign `CapabilityBinding.luau` and a target GameObject.
4. Enter Play Mode.

## What it shows

The script renames and moves one GameObject — the one assigned in the inspector,
and nothing else. There is no `GameObject.Find` from Luau: the C# side creates a
handle for that specific object and assigns it to the script's `target` global.

This is the pattern for giving a script access to part of your scene without
giving it access to all of it. See
[Exposing C# to Luau](../../Documentation~/capability-bindings.md), including how
to expose your own components this way rather than just `GameObject` and
`Transform`.
