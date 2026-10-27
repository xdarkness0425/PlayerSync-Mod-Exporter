# PlayerSync Mod Exporter

A Dalamud plugin for Final Fantasy XIV that exports the mod files behind a **PlayerSync** pair's synchronized appearance, from your local
PlayerSync cache, into a Penumbra mod pack (`.pmp`) and a PlayerSync character file (`.mcdf`), with a manifest that lists every file,
its hash and whether the package is complete.

It is meant for backups and for sharing looks **with permission**: the owner of the character must agree, and the mods remain the work
of their creators.

> **Status:** builds and passes 132 automated tests. It has **not yet been tested in a live game session**. See [docs/TESTING.md](docs/TESTING.md).

## What it does and does not do

* Lists visible PlayerSync pairs. Nothing is captured until you select a character and press **Capture and analyze**. There is no
  background or bulk harvesting.
* Uses only what PlayerSync has downloaded under its normal pairing and permission rules. It never connects to PlayerSync's
  servers, never downloads anything itself ("Download originals for this export" asks PlayerSync to, through its own pipeline), and never bypasses pauses, pair permissions, your sound/animation/VFX filters, or the server's
  forbidden-file list. Files PlayerSync filtered out are shown as "Not synced" and cannot be exported.
* Verifies every file by SHA-1 before and while exporting. Missing, corrupt, forbidden and filtered files are listed and are never
  silently dropped; the package is then marked **Partial** with the reasons.
* Tells you when a texture is only available as PlayerSync's automatically compressed version (not the creator's original).
* Only reads PlayerSync's cache and Penumbra. It does not change collections, mods, your appearance, PlayerSync's settings or its index.
* Produces a **reconstructed** mod. PlayerSync transfers no mod names or options, so where you have the same mods installed, the
  exporter matches files byte-for-byte to them and reproduces their option groups (only the options the character uses) with
  their original names. Everything else goes in the default option. Each recovered or inferred value is labeled in the
  manifest; see [docs/RECOVERY.md](docs/RECOVERY.md).
* Uses the original texture instead of PlayerSync's compressed substitute whenever the original is anywhere on your computer,
  including your installed mods. A substitute is only exported if it is a valid texture, and it is flagged.
* Shows PlayerSync's texture compression setting for the selected pair. With a PlayerSync build that has the source-quality export
  interface ([integrations/playersync](integrations/playersync)), **Download originals for this export** gets the original textures
  into a temporary folder for this export only, without changing your settings or PlayerSync's cache. See [docs/SOURCE_QUALITY.md](docs/SOURCE_QUALITY.md).

## Requirements

* FFXIV with Dalamud (API level 15).
* PlayerSync (internal name `MareSempiterne`). Tested against source version **1.15.5.9**; other versions show a warning and may be incompatible.
* Penumbra (for `.pmp` import and for converting meta manipulations).
* Glamourer and Customize+ are optional (used only to apply the exported appearance strings).

## Install (custom repository)

1. In game, open `/xlsettings` → Experimental → Custom Plugin Repositories.
2. Add `https://raw.githubusercontent.com/xdarkness0425/PlayerSync-Mod-Exporter/main/repo.json`, tick it and press Save.
3. Open `/xlplugins`, search for "PlayerSync Mod Exporter" and install it.

The repository serves the build in `dist/latest.zip`. To publish a new version, raise `<Version>` in the plugin's `.csproj`, build in
Release, copy `bin/Release/PlayerSyncModExporter/latest.zip` to `dist/` and update `AssemblyVersion` and `LastUpdate` in `repo.json`.

## Install (development build)

1. Build: `dotnet build PlayerSyncModExporter.slnx -c Release` (set `DALAMUD_HOME` to your Dalamud folder if it is not in the default
   XIVLauncher location).
2. In game: `/xlsettings` → Experimental → Dev Plugin Locations → add `src/PlayerSyncModExporter/bin/Release`, then enable
   "PlayerSync Mod Exporter" in `/xlplugins`.

## Use

1. `/psexport` opens the window.
2. Pick a character on the left (or target them and press **Select my target**). Paused pairs and pairs without data cannot be captured.
3. **Capture and analyze.** The table shows every file with its status, size and source. Hover a row for its hash, game paths and local
   path. "Only problems" and the filter box help with large characters.
4. Tick the files to include. Player files are preselected. Minion, mount and pet files need "Include minion, mount and pet files". Use
   "Exclude compressed versions" to leave out non-original textures.
5. Choose the formats, the export folder (it must be outside the PlayerSync cache and Penumbra's mod folder) and the package name.
6. Confirm the three permission statements. They are required for every export and reset whenever you select or recapture a character.
7. **Export.** Progress and Cancel are shown. If the folder exists you can keep both or replace a previous export (never any other folder).
8. The result line shows the folder, Complete/Partial, and the reasons. **Open folder** opens it.

Importing: see [docs/EXPORT_FORMAT.md](docs/EXPORT_FORMAT.md). In short, import the `.pmp` with Penumbra's "Import Mod", or apply the
`.mcdf` to a GPose actor through PlayerSync's Character Data Hub ("From MCDF"), which also restores Glamourer, Customize+ and meta data.

## Optional read-only resource copy and fingerprint catalog

You can optionally specify a separate directory containing copied PlayerSync content-addressed files through **Additional read-only resource copy** in the export window. Recapture the selected visible pair after changing the setting. The exporter only probes filenames corresponding to the pair's permission-filtered hashes, verifies each file with SHA-1, and never writes to the resource copy. Its location is excluded as an export destination. Missing copied files do not prove the resource never existed; PlayerSync's regular cache remains the primary lookup source.

After capture, expand **Mod fingerprint catalog (read-only)**. It shows mod candidates based on exact file-hash and game-path matches from your own installed Penumbra library, with counts of distinct matching paths and shared/ambiguous paths. These are *candidates*, not verified claims about which full mod packages another player installed. The catalog does not upload hashes or scan unrelated players.

## File locations

| What | Where |
|---|---|
| Exports | The folder you choose: `<folder>/<package name>/` |
| Temporary staging | `<folder>/.psme-staging-<id>/` (removed automatically) |
| Plugin settings | `pluginConfigs/PlayerSyncModExporter.json` |
| Read (never written) | PlayerSync cache folder, your Penumbra mods' `meta.json`/`default_mod.json`/`group_*.json` (for names and options), `pluginConfigs/MareSempiterne/FileCache.csv`, the `CacheFolder` value in `pluginConfigs/MareSempiterne/config.json` (only if PlayerSync's live index is unavailable), Penumbra's mod folder |
| Never read | PlayerSync's `server.json` (secret keys) or any other credentials |

## Errors and what to do

| Message | Meaning |
|---|---|
| PlayerSync is not installed / disabled / incompatible | Install or enable PlayerSync. "Incompatible" means its internals changed; hover the status for details. |
| No paired characters are visible | The pair must be visible and handled by PlayerSync right now. |
| This pair is paused | Unpause them in PlayerSync. The exporter does not bypass pauses. |
| Their appearance changed since this capture | Recapture before exporting. |
| `Missing` files | Not downloaded yet or evicted. Wait for PlayerSync to finish downloading, then recapture. |
| `Compressed` files | Press **Download originals for this export** (needs the patched PlayerSync), or set PlayerSync → Settings → Performance → "Always use source quality", press "Reload last data" on the pair, then recapture. |
| changed after analysis | PlayerSync replaced or deleted a file mid-export. Nothing was kept; analyze again. |
| Not enough free disk space | The estimate (size + 5% + 16 MiB) exceeds free space. |
| Meta manipulations are not embedded | Penumbra could not convert them. The `.mcdf` and `appearance/penumbra-meta.b64.txt` still contain them. |

The **Log** section and Dalamud's log (`/xllog`) contain details.

## Responsible use

* Ask first, every time. The plugin requires you to confirm you have the character owner's permission before each export.
* Having a file in your cache does **not** give you the right to share it. Mods belong to their creators. Do not redistribute paid,
  Patreon-exclusive or otherwise restricted mods against their terms. The exporter cannot know a mod's license, so every file's sharing
  status is "Unknown". Exclude what you are not allowed to share.
* Files the PlayerSync server forbids are never exported.
* The manifest contains the character label you chose and the capture date, never PlayerSync UIDs, notes or credentials.

## Documentation

* [docs/PLAYERSYNC_INTERNALS.md](docs/PLAYERSYNC_INTERNALS.md): how PlayerSync syncs, stores and verifies files, from its source code, and the design that follows from it.
* [docs/EXPORT_FORMAT.md](docs/EXPORT_FORMAT.md): package layout, manifest schema, generation steps.
* [docs/RECOVERY.md](docs/RECOVERY.md): what mod metadata and texture data can be recovered, with evidence and limits.
* [docs/SOURCE_QUALITY.md](docs/SOURCE_QUALITY.md): getting original textures through PlayerSync's source-quality mode.
* [docs/TESTING.md](docs/TESTING.md): build and test results, coverage, and what still needs in-game testing.

## Project layout

```
src/PlayerSyncModExporter.Core    platform-independent logic (analysis, hashing, packaging, safety)
src/PlayerSyncModExporter         Dalamud plugin (PlayerSync/Penumbra adapters, UI)
tests/PlayerSyncModExporter.Tests xUnit tests for Core
```

The project name is defined once in `Directory.Build.props` (`ExporterProjectName`).

## License and credits

MIT, see [LICENSE](LICENSE). Third-party components and references are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
This project is not affiliated with or endorsed by the PlayerSync, Penumbra or Snappy developers.
