# Linux development

Linux x64 is supported far enough to run the complete local development test
surface. It is not a shipping target: `Luau.Unity` continues to contain only the
reviewed Windows x64 and Android ARM64/x64 plugins. The tooling stages
`libluau_host.so` and Linux-only importer metadata into disposable projects under
`native/luau-host/out`.

## Prerequisites

- .NET SDK 9.0.306 (the exact SDK is selected by `global.json`)
- CMake 3.25 or newer and Ninja
- Clang/Clang++ 18 available as `clang-18` and `clang++-18`
- Git with the `native/luau` submodule initialized
- a licensed Unity 6000.3 editor with Linux IL2CPP build support

The UPM package floor is 6000.3.0f1. Local tooling accepts any patch in the
6000.3 stream and writes that selected version only into the disposable project;
it does not modify the canonical integration project's 6000.3.19f1 pin.

Initialize and inspect the checkout:

```bash
git submodule update --init --recursive
dotnet restore Luau.slnx
dotnet run --project tools/Luau.Tooling -- doctor \
  --unity /path/to/6000.3.xf1/Editor/Unity \
  --unity-version 6000.3.xf1
```

On distributions that no longer provide `libxml2.so.2`, Unity's bundled Linux
IL2CPP linker may also require legacy libxml2 and ICU 70 runtime libraries. Put
those libraries under
`~/.local/opt/unity-linux-compat/usr/lib/x86_64-linux-gnu`; the tooling scopes
that directory to Unity subprocesses through `LD_LIBRARY_PATH`. `doctor` reports
this condition before a long player build begins.

## Full acceptance

Run the full local gate from the repository root:

```bash
dotnet run --project tools/Luau.Tooling -- validate-linux \
  --unity /path/to/6000.3.xf1/Editor/Unity \
  --unity-version 6000.3.xf1
```

This configures, builds, tests, and installs the non-sanitized `linux-x64` native
preset; runs the complete .NET solution and ABI-fixture rejection; exercises the
host soak and platform-native harness selection; checks package artifacts and a
deterministic release archive; generates a minimal Unity consumer; runs all
EditMode tests; and builds and executes a Linux x64 IL2CPP player smoke.

Use `--skip-unity` only when diagnosing the native, managed, or package layers
on a machine without a licensed editor. Use `--soak-iterations N` to adjust the
local soak count.

Individual useful commands are listed by:

```bash
dotnet run --project tools/Luau.Tooling -- --help
```

Generated Unity projects, player builds, manifests, archives, and native build
outputs remain beneath ignored output directories. No Linux binary is copied to
the package or included in release archives.
