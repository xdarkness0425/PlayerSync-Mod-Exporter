# Export format

Each export creates one folder in the chosen export directory:

```
<Package Name>/
├── <Package Name>.pmp            Penumbra mod pack (optional, on by default)
├── <Package Name>.mcdf           PlayerSync character file (optional, on by default)
├── manifest.json                 inventory, integrity, completeness, consent (always)
└── appearance/                   (optional, on by default)
    ├── glamourer.b64.txt         Glamourer state, exactly as received
    ├── customizeplus.b64.txt     Customize+ data, exactly as received (if any)
    └── penumbra-meta.b64.txt     Penumbra meta manipulations, exactly as received (if any)
```

The folder is built in `<export dir>/.psme-staging-<guid>/`, verified, then renamed into place in one step. If the export fails or is
cancelled, the staging folder is deleted. Staging folders left by a crash are removed on the next export (only folders carrying the
`.psme-staging` marker file and older than one hour).

The package is a **reconstruction**. PlayerSync transfers individual content-addressed files, not mod archives (see
`PLAYERSYNC_INTERNALS.md` §4 and `RECOVERY.md`). Original mod names and option groups are only recovered for files that match a
mod installed on the exporting computer byte-for-byte; `manifest.json` always says `"origin": "reconstructed"`.

## 1. `.pmp` (Penumbra mod pack)

A zip archive in Penumbra's **FileVersion 3** layout, which every current Penumbra imports (newer versions migrate it to FileVersion 4 on load):

```
meta.json
default_mod.json
files/<content sha1 lower>.<ext>
group_NNN_<mod> - <group>.json  (one per recovered option group, only when files match installed mods)
export_manifest.json            (copy of manifest.json, ignored by Penumbra)
```

`meta.json`:

```json
{
  "FileVersion": 3,
  "Name": "<Package Name>",
  "Author": "Unknown (reconstructed from PlayerSync cache)",
  "Description": "Reconstructed from the PlayerSync appearance of ... This is not an original mod archive ...",
  "Version": "2026.10.27.1200",
  "Website": "",
  "ModTags": ["PlayerSync Export"]
}
```

`default_mod.json` (the single default option):

```json
{
  "Name": "",
  "Description": "",
  "Files":     { "<game path>": "files\\<sha1>.<ext>", ... },
  "FileSwaps": { "<game path>": "<other game path>", ... },
  "Manipulations": [ { "Type": "Rsp", "Manipulation": { ... } }, ... ]
}
```

* Game paths are lower-case with forward slashes and are validated (no rooted paths, `..`, drive letters, control or non-ASCII characters).
* Identical content is stored once and referenced by every game path that uses it.
* If the same game path is replaced by different files for different objects (for example the player and a minion), the player's file
  is kept and a warning is recorded.
* Minion, mount, pet and companion files are only included when "Include minion, mount and pet files" is enabled. They are merged into
  the same option, because Penumbra applies a mod to a whole collection.
* `Manipulations` is filled only when the received meta string can be converted exactly: format version 0 (JSON) by the built-in decoder,
  formats 1 and 2 (binary) by Penumbra's own `MetaApi.ConvertManips` in the running game. Otherwise it is `[]`, the manifest says
  `meta.embeddedInPmp: false`, and the package is marked Partial. The raw meta string is still in the `.mcdf` and in `appearance/`.

Recovered option groups (`group_001_cool top - color.json`), in the FileVersion 3 shape Penumbra's `GroupDeserialization` reads:

```json
{
  "Name": "Cool Top - Color",
  "Description": "<original description>\n\n[Recovered from your installed copy of \"Cool Top\". 1 option(s) this character does not use are not included. ...]",
  "Priority": 0,
  "Type": "Single",
  "DefaultSettings": 0,
  "Options": [ { "Name": "Red", "Description": "<original>", "Files": { "<game path>": "files\\<sha1>.<ext>" }, "FileSwaps": {}, "Manipulations": [] } ]
}
```

* Groups are ordered by mod name, then original group order; options keep their original order, names, descriptions and (Multi) priorities.
* Single groups contain the one option the character uses (`DefaultSettings` = 0). Multi groups contain every used option, all enabled.
* Every game path is in exactly one place (a Multi option may share a path with another option of the same group, pointing to the same file).
* Files from a source mod's default container, unattributed files, ambiguous files and meta manipulations are in `default_mod.json`.

**Import:** Penumbra → Mods → "Import Mod" (or drag the file in). Then enable the mod in the collection you want, at a priority above
conflicting mods. Apply `appearance/glamourer.b64.txt` with Glamourer ("Import from clipboard") to get the matching gear and customization.

## 2. `.mcdf` (PlayerSync character data file)

The format PlayerSync itself uses (`CharaDataFileHandler.SaveCharaFileAsync`), version 1, inside a legacy LZ4 stream
(`K4os.Compression.LZ4.Legacy.LZ4Stream`, HighCompression):

| Offset | Size | Content |
|---|---|---|
| 0 | 4 | ASCII `MCDF` |
| 4 | 1 | version `1` |
| 5 | 4 | int32 LE: JSON length *n* |
| 9 | *n* | UTF-8 JSON (below) |
| 9+*n* | Σ Length | raw file contents, in `Files` order |

```json
{
  "Description": "<label> (exported by PlayerSync Mod Exporter, <date>)",
  "GlamourerData": "<base64>",
  "CustomizePlusData": "<base64>",
  "ManipulationData": "<base64>",
  "Files":     [ { "GamePaths": ["..."], "Length": 1234, "Hash": "<SHA-1 of these bytes>" } ],
  "FileSwaps": [ { "GamePaths": ["..."], "FileSwapPath": "<game path>" } ]
}
```

MCDF only carries the **player** object; minion, mount and pet files are never written to it. Files over 2 GiB cannot be stored (the
length field is int32); the export fails rather than truncating.

**Load:** in GPose, open PlayerSync's Character Data Hub ("From MCDF" tab, `PlayerSync/UI/CharaDataHubUi.cs`), press "Load MCDF" and apply it to a GPose
actor, or call the `PlayerSync.LoadMcdf` IPC from another plugin. Unlike the `.pmp`, the MCDF restores Glamourer, Customize+ and meta.

## 3. `manifest.json`

Schema `playersync-mod-exporter/manifest`, `schemaVersion` 1, camelCase, enums as strings.

| Field | Meaning |
|---|---|
| `generator` | Tool name and version. |
| `package` | `name`, `createdAtUtc`, `formats` (`pmp`, `mcdf`), file names, `origin: "reconstructed"` and an explanation. |
| `character` | `label` (name, optionally `@World`, as chosen in the UI), `capturedAtUtc`, `source` ("PlayerSync"), `sourceVersion`. **No UID, alias, account ID, note or credential is ever written.** |
| `completeness.status` | `Complete` only if every file PlayerSync applies is included with its original bytes, nothing was excluded, filtered, forbidden, missing or corrupt, and meta is embedded (when a .pmp is written). Otherwise `Partial`. |
| `completeness.reasons` | One human-readable line per shortfall, with counts. |
| `consent` | The three confirmations given in the UI, with the time. |
| `summary` | Counts: captured, included, bytes, duplicates merged, missing, hash mismatch, unreadable, not synced, forbidden, invalid, excluded by user, compressed alternates included, invalid content, originals from installed mods, file swaps. |
| `files[]` | **Every** captured file, exported or not: `hash` (as referenced by PlayerSync), `contentHash` (SHA-1 of the exported bytes), `extension`, `size`, `gamePaths`, `objectKinds`, `status`, `included`, `provenance`, `isOriginalContent`, `representation` (`Original`, `OriginalFromInstalledMod`, `CompressedAlternate`), `verifiedAgainst` (`hash` or `compressedAlternateHash`), `texture` (header check: `headerValid`, `format`, `width`, `height`, `mipLevels`, `arraySize`, `problem`), `sharingStatus`, `archivePath`, `note`, `attribution[]` (per game path: `classification`, `modName`, `modFolder`, `group`, `options`, `placement` = `group`/`default`/`unattributed`, `candidateMods`, `inferredCategory`, `note`). |
| `fileSwaps[]` | Every swap and whether it was included. |
| `metadata` | Mod name and option recovery: `libraryAvailable`, `modsIndexed`, `attributedPaths`, `ambiguousPaths`, `unattributedPaths`, `sourceMods[]` (name, author, description, version, website as `{value, classification, source}`, folder, `groups[]` with `authorDefault` (Recovered), `inferredSelection` (Inferred), `reproducedOptions`, `totalOptions`, `packageGroup`) and `unavailable[]` (what cannot be recovered and why). |
| `meta` | `present`, `formatVersion`, `embeddedInPmp`, `embeddedEntries`, `converter`, `note`. |
| `appearance` | Relative paths of the files in `appearance/`. |
| `warnings` | Malformed entries ignored, path conflicts and similar. |

### Status values

| `status` | Meaning | Exported? |
|---|---|---|
| `Available` | A local copy was found and its SHA-1 matches. | If selected. |
| `Missing` | No local copy (not downloaded yet, evicted, deleted). | No. |
| `HashMismatch` | A local copy exists but its content is different (corrupt or modified). | No. |
| `Unreadable` | A local copy could not be read. | No. |
| `NotSynced` | PlayerSync's permissions or your filters (sounds, animations, VFX, minions, pets) kept it from you. | Never. |
| `ForbiddenByServer` | The PlayerSync server marked it forbidden. | Never. |
| `InvalidMetadata` | Bad hash or no valid game path in the received data. | No. |
| `InvalidContent` | Only a compressed substitute exists and it is not a valid texture. | No. |

`provenance` is `PlayerSyncCache` (downloaded by PlayerSync) or `PenumbraModDirectory` (a byte-identical file in your own Penumbra mods).
`isOriginalContent: false` means only PlayerSync's server-compressed version of a texture was available. Its bytes differ from the
creator's file (`contentHash` ≠ `hash`).

`sharingStatus` is `Unknown` for everything except forbidden files (`Restricted`). The exporter has no way to learn a mod's license or
the creator's terms, and does not claim anything is safe to redistribute.

## 4. How a package is generated

1. **Capture** (framework thread): the selected pair's `LastReceivedCharacterData`, PlayerSync's own permission filter applied to a deep
   copy, compression redirects and forbidden hashes are copied into an immutable `CapturedCharacter`.
2. **Analyze** (thread pool, up to 4 in parallel): the installed Penumbra mods' description files are indexed (read-only). Each unique
   hash is located through PlayerSync's index (read-only), a direct probe of the cache folder, a read-only snapshot of `FileCache.csv`
   and, by game path, the installed mods. Every candidate is SHA-1 hashed; results are cached by path, size and modification time.
   `.tex` headers are checked. Files are then attributed to installed mods (see `RECOVERY.md`).
3. **Review:** the user picks files. Only `Available` entries can be ticked. Player files are preselected; other objects only when enabled.
4. **Plan:** `PackagePlan` deduplicates by content hash, resolves path conflicts and computes the exact size. The free disk space is
   checked against the size plus 5% and 16 MiB.
5. **Write:** each file is streamed with a 1 MiB buffer into the `.pmp`/`.mcdf` and re-hashed while copying. If the bytes no longer match
   (PlayerSync replaced or evicted the file), the export stops and nothing is kept.
6. **Verify:** the `.pmp` is reopened: `meta.json` must have a name, every `Files` target must exist, and every payload's size and SHA-1
   must match. The `.mcdf` is decompressed and every payload hash checked, with no trailing data allowed.
7. **Finalize:** the staging folder is renamed to the package folder. An existing folder is only replaced when the user chose "Replace
   previous export" **and** it contains a manifest from this tool; otherwise the user can keep both ("Name (2)").
