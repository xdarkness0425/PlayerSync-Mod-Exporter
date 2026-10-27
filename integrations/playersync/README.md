# PlayerSync patch: source-quality export IPC

`0001-source-quality-export-ipc.patch` adds the export-scoped source-quality interface the exporter's **Download originals for this
export** button uses. It is against PlayerSync client commit `b2f8ce8` (version 1.15.5.9, MIT, `universalconquistador/MareSynchronosClient`).
Only your own PlayerSync needs it; the people you are paired with need nothing.

What it changes and why: see `PlayerSync/Services/SourceQualityExport/README.md` inside the patch and [docs/SOURCE_QUALITY.md](../../docs/SOURCE_QUALITY.md).

## Apply and build

```sh
git clone https://github.com/universalconquistador/MareSynchronosClient
cd MareSynchronosClient
git checkout b2f8ce8
git submodule update --init --recursive
git apply /path/to/0001-source-quality-export-ipc.patch
dotnet build PlayerSync/PlayerSync.csproj -c Release
dotnet test  PlayerSync.SourceQualityExportTest -c Release
```

Then load the built plugin as a dev plugin in place of the released PlayerSync.

## How it was checked (Linux container)

* `git apply --check` on a clean `b2f8ce8` checkout: applies.
* Build: 0 errors. Two local workarounds were needed that are **not** part of the patch: `DOTNET_ROLL_FORWARD=Major` (the VFXEditor
  submodule's FlatSharp compiler targets .NET 9) and an alias for `Rectangle` in `UI/EditProfileUi.cs`, which is ambiguous with the
  newer Dalamud reference assemblies used here (15.0.3.9). Neither touches the new code.
* `PlayerSync.SourceQualityExportTest`: 18 passed, 0 failed.
* Not run in game, and not checked against an installed PlayerSync DLL (none was available here).
