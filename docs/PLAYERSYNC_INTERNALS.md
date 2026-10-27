# PlayerSync internals: a source-based audit

This audit backs the design of PlayerSync Mod Exporter. Every statement below is either taken from source
(with the repository path, class and member) or listed under "assumptions" in section 11.

**Sources inspected (2026-10-27)**

| Repository | Commit | License | How it is used here |
|---|---|---|---|
| [universalconquistador/MareSynchronosClient](https://github.com/universalconquistador/MareSynchronosClient) (PlayerSync client) | `b2f8ce8` (2026-10-19), plugin version `1.15.5.9` (`PlayerSync/PlayerSync.csproj`) | MIT (`LICENSE`) | Studied. No code copied; file formats re-implemented with attribution. |
| MareAPI submodule ([universalconquistador/MareSynchronosAPI](https://github.com/universalconquistador/MareSynchronosAPI)) | `2a65787` | MIT | Data shapes read by reflection. |
| [BeSlightly/Snappy](https://github.com/BeSlightly/Snappy) | `5c44b2c` (2026-10-16), version `0.5.2.11` | **No license file.** All rights reserved by default. | Technical reference only. No code copied. |
| Penumbra (Snappy's submodule, [xivdev/Penumbra](https://github.com/xivdev/Penumbra)) | `b160e1d` | No license file in the repository | Reference for the .pmp layout and meta format. No code copied. |
| `Penumbra.Api` NuGet 5.15.1 | n/a | MIT | Used for IPC (the same version PlayerSync uses). |
| `K4os.Compression.LZ4.Legacy` NuGet 1.3.8 | n/a | MIT | Used for the MCDF container (the same library PlayerSync uses). |

Paths below are relative to the PlayerSync client repository unless they start with `Snappy/` or `Penumbra/`.

---

## 1. Architecture and version

* The plugin's Dalamud internal name is **`MareSempiterne`** (`PlayerSync/MareSempiterne.json`), display name
  "PlayerSync". The C# namespace is still `MareSynchronos` (for example `PlayerSync/Plugin.cs`, `namespace MareSynchronos`).
* `README.md`: "A fork of the late great Mare Synchronos ... not compatible with the final version of the classic Mare API, we have added extensions and changes to the API." The extensions that matter here are compressed texture alternates (section 3.4) and forbidden-file metadata (section 3.5).
* `Plugin` (`PlayerSync/Plugin.cs`) builds a `Microsoft.Extensions.Hosting` host in the private field `_host` and registers every service as a singleton, including `FileCacheManager` (line 116), `FileTransferOrchestrator` (122), `CompressedAlternateManager` (142) and `PairManager` (171).
* Commands: `/sync` and `/psync` (`PlayerSync/Services/CommandManagerService.cs`, `_commandName`, `_secondaryCommandName`). `/sync` is registered only if no other plugin owns it, otherwise `/psync` is the active alias.
* PlayerSync targets Dalamud API 15 (`Dalamud.NET.Sdk/15.0.0`, `net10.0-windows7.0`) and depends on `Penumbra.Api 5.15.1`, `Glamourer.Api 2.8.0` and `K4os.Compression.LZ4.Legacy 1.3.8`.
* Storage: the user configures a dedicated cache folder (`MareConfig.CacheFolder`, `PlayerSync/MareConfiguration/Configurations/MareConfig.cs`). The setup guide (docs.playersync.io, "1st Time Setup") asks for separate folders for Penumbra and PlayerSync and says not to share the folder with other sync plugins.

## 2. Character synchronization lifecycle

```
 remote client                PlayerSync server            local PlayerSync                          Penumbra / Glamourer
 ─────────────                ─────────────────            ────────────────                          ────────────────────
 PlayerDataFactory builds
 CharacterData from its own
 Penumbra resource paths ──▶ UserPushData ──(SignalR)──▶ ApiController.Client_UserReceiveCharacterData
 (hashes = SHA-1 of files)                                  └▶ PairManager.ReceiveCharaData
                                                              └▶ AddApplyDataToQueue ─▶ Pair.ApplyDataAsync
                                                                   Pair.LastReceivedCharacterData = dto.CharaData
                                                                   Pair.RemoveNotSyncedFiles(deep clone)   (permissions/filters)
                                                                   └▶ PairHandler.ApplyCharacterDataAsync
                                                                        TryCalculateModdedDictionary
                                                                         ├ FileCacheManager.GetFileCacheByHash(hash)
                                                                         ├ CompressedAlternateManager (texture swap)
                                                                         └ missing → FileDownloadManager
                                                                              InitiateDownloadList (server metadata,
                                                                              IsForbidden, compressed alternates)
                                                                              DownloadFiles → .bin → LZ4 unwrap
                                                                              → <cache>/<hash>.<ext> → hash check
                                                                        ApplyDataApplicationAsync
                                                                         ├ validation (FileValidation)       ─▶ AssignTemporaryCollection(Mare_<uid>)
                                                                         ├ SetTemporaryModsAsync             ─▶ AddTemporaryMod("PS_<uid>_Files", gamePath → local file)
                                                                         ├ SetManipulationDataAsync          ─▶ AddTemporaryMod("PS_<uid>_Meta", meta string)
                                                                         └ ApplyCustomizationDataAsync        ─▶ Glamourer state, Customize+, Honorific, ...
                                                                                                                 + redraw
```

Step by step, with sources:

1. **Identity.** A pair is a `Pair` (`PlayerSync/PlayerData/Pairs/Pair.cs`) keyed by `UserData` (`UID`, `Alias`; `MareAPI/MareSynchronosAPI/Data/UserData.cs`). The on-screen character is resolved through an ident hash (`Pair.Ident`, `OnlineUserIdentDto`) by `PairHandler.Initialize` → `DalamudUtilService.GetPlayerCharacterFromCachedTableByIdent`. `Pair.Address` is the game object address of the visible character (`PairHandler.PlayerCharacter`).
2. **Access rules.** The server only sends data to clients that are paired directly or share a syncshell (`Pair.IsPaired => IndividualPairStatus == Bidirectional || UserPair.Groups.Any()`). Locally, `Pair.IsPaused` (`UserPermissions.Paused`) stops application. `Pair.RemoveNotSyncedFiles` (Pair.cs, ~line 306) strips sounds (`.scd`), animations (`.tmb`, `.pap`), VFX (`.atex`, `.avfx`), minion/mount and pet files according to the pair's `OwnPermissions`/`OtherPermissions` (`DisableSounds`, `DisableAnimations`, `DisableVFX`; `MareAPI/.../Enum/UserPermissions.cs`), the user's global filters (`MareConfig.FilterSounds` etc.) and zone-sync filters. Files removed here are never downloaded.
3. **Appearance data.** `CharacterData` (`MareAPI/MareSynchronosAPI/Data/CharacterData.cs`) holds, per `ObjectKind` (`Player`, `MinionOrMount`, `Companion`, `Pet`): `FileReplacements` (list of `FileReplacementData`), `GlamourerData` (Glamourer base64 state), `CustomizePlusData`, `LociData`; plus `ManipulationData` (Penumbra meta string), `HeelsData`, `HonorificData`, `MoodlesData`, `PetNamesData`.
4. **Required files.** `FileReplacementData` = `{ Hash, GamePaths[], FileSwapPath }`. With a hash it is a content file that replaces every listed game path. With an empty hash and a `FileSwapPath` it is a Penumbra file swap (game path → another game path, no payload).
5. **Transfer.** `PairHandler.DownloadAndApplyCharacterAsync` loops up to 10 times calling `FileDownloadManager.InitiateDownloadList` (asks the server for `DownloadFileDto` metadata) and `DownloadFiles`. Each file is downloaded compressed to `<cache>/<hash>.bin` (`FileCacheManager.GetCacheFilePath(hash, "bin")`), optionally XOR-"munged" with `MungeKey`, then `LZ4Wrapper.Unwrap` decompresses it and `FileCompactor.WriteAllBytesAsync` writes `<cache>/<hash lower>.<ext>` (`FileDownloadManager.DownloadFilesInternal`). The `.bin` is deleted.
6. **Hash linkage.** `FileDownloadManager.PersistFileToStorage` creates the cache entry, which hashes the written file (`FileCacheManager.CreateCacheEntry` → `Crypto.GetFileHash`) and deletes it on mismatch. The cache is therefore content-addressed: file name and index key are the SHA-1 of the decompressed bytes.
7. **Penumbra application.** `ApplyDataApplicationAsync` assigns a temporary collection created by `IpcCallerPenumbra.CreateTemporaryCollectionAsync` named **`Mare_<uid>`** with identity "PlayerSync", then `SetTemporaryModsAsync` adds a temporary mod **`PS_<uid>_Files`** mapping each game path to the local cache file (or swap target), and `SetManipulationDataAsync` adds **`PS_<uid>_Meta`** with the meta string (`PlayerSync/Interop/Ipc/IpcCallerPenumbra.cs`, lines 229-340). Without a UID the keys are `MareChara_Files` / `MareChara_Meta` (used for MCDF/GPose data).
8. **Refresh.** Every new `OnlineUserCharaDataDto` replaces `Pair.LastReceivedCharacterData` (a new object) and re-runs the pipeline; `PairHandler` compares with `_cachedData` to apply only changed parts.
9. **Failures.** Missing downloads are retried; after 10 attempts the hashes are logged as failed ("file(s) may be forbidden") and application continues without them. Forbidden files are recorded in `FileTransferOrchestrator.ForbiddenTransfers`. Files that fail validation (`FileValidation.ValidateFile`, crash level) are dropped from `moddedPaths`. If the player turns invisible mid-application, the data is cached and re-applied later.
10. **What another plugin can reach.** Only two IPC functions are public (`PlayerSync/Interop/Ipc/IpcProvider.cs`): `PlayerSync.GetHandledAddresses` / `MareSynchronos.GetHandledAddresses` (`List<nint>` of handled game objects) and `PlayerSync.LoadMcdf(Async)` / `MareSynchronos.LoadMcdf(Async)` (apply an MCDF to a target). Character data and the cache index are internal; they can only be read through reflection into the plugin's service provider (section 6).

## 3. File transfer and cache lifecycle

### 3.1 Storage layout

| Item | Location | Source |
|---|---|---|
| Cache folder | `MareConfig.CacheFolder` (user chosen) | `MareConfig.cs` |
| Cached file | `<CacheFolder>/<sha1 lower>.<ext>` (raw, decompressed bytes) | `FileCacheManager.GetCacheFilePath` |
| Temporary download | `<CacheFolder>/<sha1>.bin` (compressed, deleted after unwrap) | `FileDownloadManager.DownloadFilesInternal` |
| Legacy names | `<hash>` without extension; migrated to `<HASH>.<ext>` (upper-cased full path) | `FileCacheManager.MigrateFileHashToExtension` |
| MCDF extraction | `<CacheFolder>/mare_<n>.tmp` | `CharaDataFileHandler.McdfExtractFiles` |
| Index | `pluginConfigs/MareSempiterne/FileCache.csv` (+ `.bak` during writes) | `FileCacheManager` constructor, `WriteOutFullCsvNow` |
| Settings | `pluginConfigs/MareSempiterne/config.json` (`CacheFolder`, filters, limits) | `MareConfigService.ConfigName` |

Secrets (secret keys and authentications) are stored by `ServerConfigService` in a separate file, `server.json` (`ServerStorage.SecretKeys`, `Authentications`). The exporter never opens it; the only PlayerSync file it may read directly is `config.json` for the `CacheFolder` value, and `FileCache.csv`.

### 3.2 `FileCache.csv`

Confirmed. One line per indexed file: `hash|prefixedPath|lastWriteTicks|size|compressedSize` (`FileCacheManager.WriteOutFullCsvNow`). The hash is written lower-case and parsed upper-case and must be 40 characters (`StartAsync`). `prefixedPath` begins with `{cache}\` (PlayerSync cache folder) or `{penumbra}\` (Penumbra's mod directory): PlayerSync also indexes the user's own installed mod files so it can upload them and reuse them instead of downloading (`CreateFileEntry`, `GetFileCacheByHash` prefers `{penumbra}` entries). Writes are debounced by 2 seconds and written in full; a `.bak` copy guards against torn writes.

### 3.3 Integrity

* Hash scheme: **SHA-1**, upper-case hex, of the full decompressed file (`PlayerSync/Utils/Crypto.cs`, `GetFileHash`, `GetStreamHashAsync`). SHA-256 is used only for identity strings (`GetHash256`), not file content.
* Check after download: `PersistFileToStorage` (mismatch → delete). Freshness check on lookup: `FileCacheManager.Validate` compares `LastWriteTimeUtc` ticks and **re-hashes and rewrites the index** when they differ. `GetFileCacheByHash` always validates, so calling it from outside can modify PlayerSync's index. `GetAllFileCachesByHash(hash, ignoreCacheEntries, validate: false)` does not.
* Full scan: `FileCacheManager.ValidateLocalIntegrity` (user-triggered in PlayerSync's settings) re-hashes cache files and deletes broken ones.
* Downloaded files get random past timestamps (`PersistFileToStorage`, `RandomDayInThePast`), so modification time says nothing about when a file was received.

### 3.4 Compressed alternates (PlayerSync extension)

`DownloadFileDto.CompressedAlternateFileDownload` (`MareAPI/.../Dto/Files/DownloadFileDto.cs`) lets the server offer a re-compressed version of a texture with a **different hash**. The client policy is `PlayerPerformanceConfig.TextureCompressionMode`, default `CompressedAlternateUsage.AlwaysCompressed` (`PlayerPerformanceConfig.cs`, `DefaultTextureCompressionMode`). `PairHandler.TryCalculateModdedDictionary` substitutes the alternate's hash and records it in the public property `PairHandler.ActiveCompressionRedirects` (original hash → alternate hash). The mapping cache (`CompressedAlternateManager._entryDictionary`) is in memory only and cleared on start.

Consequence: by default, many textures in your cache are **not the creator's original file**. The exporter detects this, exports the alternate only as a marked substitute, and flags the package as Partial.

### 3.5 Forbidden files

`DownloadFileDto.IsForbidden` / `ForbiddenBy`; the client collects them in `FileTransferOrchestrator.ForbiddenTransfers` (`PlayerSync/WebAPI/Files/FileTransferOrchestrator.cs`, line 67) and never downloads them. The exporter treats these hashes as restricted and never exports them, even if an identical file exists elsewhere on disk.

### 3.6 Queues, parallelism, transformation

* `FileTransferOrchestrator.WaitForDownloadSlotAsync` / `ReleaseDownloadSlot` bound parallel downloads; `GetOrStartDownloadTask` de-duplicates concurrent downloads of the same hash across pairs.
* Transformations: LZ4 (legacy wrapper) on the wire, optional XOR munging, optional NTFS/WOF transparent compression on disk (`FileCompactor`). Reads through the normal file API return the original bytes. No texture conversion happens on the client; compressed alternates are produced by the server.

### 3.7 Eviction and cleanup

`CacheMonitor.RecalculateFileCacheSize` deletes files in order of `LastAccessTime` once the folder exceeds `MaxLocalCacheInGiB` (default 40). Files are shared by every pair that uses the same content, so there is no per-player storage; "which files belong to this player" exists only in that player's `CharacterData`.

### 3.8 Duplicate detection

Content addressing makes identical files share one cache file. `FileReplacementData` groups all game paths of one hash, and MCDF export groups by hash (`MareCharaFileData` constructor).

## 4. Hashing, metadata and what can be reconstructed

* A cache file is exactly the bytes of one file inside some mod, identified only by SHA-1 and the game paths it replaces. **The original mod archive, its name, author, option groups, unused options, folder structure, default settings, images and description are not transferred and cannot be reconstructed.**
* `GamePaths` gives the game path(s) each file replaces, enough to rebuild a working single-option mod.
* `ManipulationData` is the merged meta state of the remote player's whole collection, not per mod.
* File swaps are reconstructible exactly.
* Compressed alternates are substitutes; the original bytes are unrecoverable locally.

## 5. PlayerSync, Penumbra and Glamourer

* Penumbra applies the files: one temporary collection per pair (`Mare_<uid>`), one temporary mod for files (`PS_<uid>_Files`) and one for meta (`PS_<uid>_Meta`). Penumbra's mod directory is unrelated to PlayerSync's cache, except that PlayerSync indexes it (`{penumbra}` prefix).
* Glamourer receives `GlamourerData[ObjectKind.Player]` (base64 state) through `IpcCallerGlamourer` during `ApplyCustomizationDataAsync`. That string is appearance and equipment state only and contains no mod files.
* Customize+, Honorific, Heels, Moodles, Loci and PetNames receive their own strings.

## 6. Relevant APIs, classes and members

**Public, supported**

| API | Signature | Notes |
|---|---|---|
| IPC `PlayerSync.GetHandledAddresses` | `List<nint>()` | Addresses of every non-owned `GameObjectHandler`, including pairs and GPose MCDF actors. |
| IPC `PlayerSync.LoadMcdf` / `LoadMcdfAsync` | `(string path, IGameObject target) → bool / Task<bool>` | Applies an MCDF file to a target (used to test exported MCDFs). |
| `IDalamudPluginInterface.InstalledPlugins` | `IExposedPlugin` | Detects install state, load state and version. |
| Penumbra IPC (Penumbra.Api 5.x) | `ApiVersion`, `GetModDirectory`, `GetGameObjectResourcePaths` | Used read-only. |

**Internal (reflection, version-dependent), as read by `src/PlayerSyncModExporter/Interop/PlayerSyncAdapter.cs`**

| Member | Use | Mutates? |
|---|---|---|
| Dalamud `Service<PluginManager>.Get().InstalledPlugins[].instance` | PlayerSync plugin instance (Dalamud hands other plugins no instance) | no |
| `Plugin._host.Services` | Service provider | no |
| `PairManager.GetVisiblePairs()` | Visible pairs | no |
| `Pair.PlayerName`, `Address`, `IsVisible`, `IsPaused`, `LastReceivedCharacterData` | Selection and capture | no |
| `Pair.RemoveNotSyncedFiles(CharacterData)` (private) | PlayerSync's own permission filter | mutates its argument, so it only receives a JSON deep copy |
| `Pair.CachedPlayer` (private) → `PairHandler.ActiveCompressionRedirects` | Compressed alternates in use | no (copied) |
| `FileCacheManager.GetAllFileCachesByHash(hash, false, validate: false)` | Locate local copies | no (with `validate: false`) |
| `FileCacheManager.CacheFolder` | Cache root | no |
| `FileTransferOrchestrator.ForbiddenTransfers` | Forbidden hashes | no (copied) |

**Penumbra internal (reflection)**: `Penumbra.Api.Api.MetaApi.ConvertManips(string, out MetaDictionary, out byte)` (internal static, `Penumbra/Api/Api/MetaApi.cs` ~line 230) and Newtonsoft serialization of `MetaDictionary` (its `[JsonConverter]` writes the same array a mod's `Manipulations` field contains; `Penumbra/Meta/Manipulations/MetaDictionary.cs`, `Converter.WriteJson`; read back by `Penumbra/Mods/SubMods/SubMod.cs`).

**Not used**: no SignalR, authentication, download or upload code is called or duplicated. The exporter only reads what PlayerSync already has locally.

## 7. Snappy: how it works and what was reused

* **Detection** (`Snappy/Snappy/Integrations/MareIpc/MareIpc.cs`): known forks by internal name; PlayerSync = `MareSempiterne` with namespace prefix `MareSynchronos`. It gets the plugin instance with ECommons' `TryGetLoadedPluginInstance`, then `_host.Services`, then `PairManager` and `FileCacheManager` from the service provider.
* **Pair listing** (`MareIpc.Addresses.cs`, `MareIpc.Pairs.cs`): `PlayerSync.GetHandledAddresses` IPC intersected with visible, connected pairs from `GetOnlineUserPairs` / `_allClientPairs`.
* **Capture** (`Snappy/Snappy/Services/SnapshotManager/MareSnapshotDataBuilder.cs`): reads `LastReceivedCharacterData` by reflection: player `GlamourerData`, `CustomizePlusData`, `ManipulationData`, and every `FileReplacements` entry (hash → game paths, or swap). It reads the **unfiltered** received data, does not apply `RemoveNotSyncedFiles`, and ignores compressed-alternate redirects and forbidden files.
* **File resolution** (`MareIpc.FileCache.cs`, `SnapshotFileService.UpdateSnapshotFromCaptureAsync`): `FileCacheManager.GetFileCacheByHash(hash)` → `ResolvedFilepath`, copied into a snapshot folder. That method validates and may rewrite PlayerSync's index. **Missing files are logged and silently dropped** ("Skipping missing source file ...") and no hash check is made on copy.
* **Export**: `Snappy/Snappy/Features/Pmp/PmpExportManager.cs` writes a `.pmp` with `meta.json` and `default_mod.json`, `files/<hash>.<ext>` entries and backslash paths; it converts meta with its own `PenumbraMetaCodec`, compiled against Penumbra's source. `Features/Mcdf/McdfManager.cs` imports and exports MCDF with lz4net; `Features/Pcp` handles Penumbra character packs.
* **Licensing**: Snappy has no license file, so this project **did not copy any Snappy code**. Its approach (reflection into PlayerSync's service provider, the `.pmp` shape) was used as a reference and every component was written independently.

| Concern | Snappy | This exporter |
|---|---|---|
| Permission filter | Unfiltered received data | PlayerSync's own `RemoveNotSyncedFiles` on a copy; filtered files reported as "Not synced" and never exported |
| Forbidden files | Not considered | Never exported |
| Compressed alternates | Not distinguished | Detected and flagged as non-original |
| Index access | `GetFileCacheByHash` (validating, can write) | `GetAllFileCachesByHash(validate: false)` + read-only folder probe + read-only CSV |
| Integrity | None | SHA-1 on analysis, during copy and after writing |
| Missing files | Silently skipped | Listed, package marked Partial |
| Meta conversion | Compiles against Penumbra source | Penumbra's loaded assembly at runtime; failures reported, raw data preserved |
| Consent | README notice | Required per-export confirmation, recorded in the manifest |

## 8. Export formats and limitations

| Format | Contains mod payloads? | Notes |
|---|---|---|
| PlayerSync `CharacterData` | No (hashes and game paths only) | Network DTO. |
| MCD (online character data, `CharaDataDownloadDto`) | No, references hashes on the server | Needs a server connection to download files. |
| **MCDF** (PlayerSync character file) | **Yes**, player object only | LZ4 stream of header + JSON + raw files (`CharaDataFileHandler.SaveCharaFileAsync`). PlayerSync itself only exports the **local** player (`CreatePlayerData`). Loadable with `PlayerSync.LoadMcdf` / the Character Data Hub in GPose. Glamourer, Customize+ and meta included. |
| Glamourer state (base64) | No | Appearance and equipment only. |
| Penumbra collection | No | References installed mods; temporary collections are in memory. |
| **.pmp** (Penumbra mod pack) | **Yes** | Zip with `meta.json` + option files. Penumbra imports any zip with a root `meta.json` and a `Name` (`Penumbra/Import/TexToolsImporter.Archives.cs`). |
| .ttmp2 (TexTools) | Yes | Not produced: TexTools-specific metadata cannot be reconstructed. |
| Snappy snapshot folder | Yes | Snappy-specific `snapshot.json` + blobs. |

**Can a self-contained, Penumbra-importable package be built?** Yes, for whatever is locally available: a single-option `.pmp` whose default option maps every game path to the cached file, plus file swaps and (when Penumbra can decode it) meta manipulations. It is a **reconstruction**: not the original archive, with no options, and with the merged meta of the whole remote collection. Missing, filtered, forbidden or corrupt files cannot be included, and compressed alternates are substitutes. The exporter also writes an MCDF, the only format that carries Glamourer, Customize+ and meta together with the files and that PlayerSync can apply directly. Details are in `docs/EXPORT_FORMAT.md`.

Penumbra file version: current Penumbra writes `FileVersion` 4 with all data in `meta.json` (`Penumbra/Files/ModSerialization.cs`), but still reads FileVersion ≤ 3 (`meta.json` + `default_mod.json`) and migrates it (`Penumbra/Files/ModDeserialization.cs`, `MigrateGroups`). The exporter writes FileVersion 3 because older and newer Penumbra versions both accept it.

## 9. Failure modes and compatibility risks

* **Internal API drift.** Any rename of the members in section 6 breaks capture. The adapter reports `Incompatible` with the missing member instead of guessing. It warns when the PlayerSync version is not the tested `1.15.5.9`.
* **Dalamud internals.** Getting the plugin instance relies on `Dalamud.Service<PluginManager>` and `LocalPlugin.instance` (verified against Dalamud 15.0.3.9 reference assemblies).
* **Not yet downloaded / evicted files** → Missing. Forbidden files → never available. Filtered files → Not synced.
* **Compressed alternates** (default setting) → many textures are substitutes. To export originals, the user can set PlayerSync's texture compression to "Always use source quality" and let the pair re-download.
* **Cache changes during export.** PlayerSync can evict or replace files at any time. Every file is re-hashed while it is copied, and the export aborts cleanly if anything changed.
* **Paused or invisible pair**: capture is refused. **Disconnected from server**: `LastReceivedCharacterData` may still exist for visible pairs, and the cache is local, so capture works on whatever was last received. **PlayerSync not installed, disabled or reloaded**: the adapter re-binds on `ActivePluginsChanged`, and the UI shows the state.
* **Meta conversion** depends on Penumbra internals; if it fails, the `.pmp` has no meta and is marked Partial, while the MCDF keeps the meta.
* **Last access times.** Reading cache files can update their last-access timestamp on systems that track it, which affects only PlayerSync's eviction order.

## 10. Recommended (and implemented) architecture

```
src/PlayerSyncModExporter.Core   (net10.0, no Dalamud dependency, fully unit-tested)
  Model/        CapturedCharacter, AssetEntry, PlayerSyncDataReader (reflection over CharacterData shape)
  Locating/     CacheFolderLocator, FileCacheCsv(+Locator), DelegateLocator
  Hashing/      ContentHash (SHA-1, read-only shared open), FileHashCache (path+size+mtime)
  Analysis/     AssetAnalyzer (resolve, verify, classify, dedupe)
  Meta/         MetaManipulationCodec, converters
  Packaging/    PackagePlan, PmpWriter, McdfFormat, ExportManifest, ExportPackager (staging, verify, atomic move)
  IO/ Safety/   PathSafety, StreamingCopy, StagingDirectory, ExportConsent
src/PlayerSyncModExporter        (Dalamud plugin)
  Interop/PlayerSyncAdapter      every PlayerSync internal lives here
  Interop/PenumbraAdapter        Penumbra IPC + reflection meta converter
  Services/ExportSession         workflow state machine
  UI/MainWindow                  ImGui window (/psexport)
```

## 11. Verified from source vs. still to verify at runtime

**Verified from source**

* Internal name `MareSempiterne`, namespace `MareSynchronos`, version 1.15.5.9; IPC names and signatures.
* Cache naming, the `FileCache.csv` format and prefixes, SHA-1 hashing, LZ4 + munge transfer, post-download hash check.
* Penumbra names `Mare_<uid>`, `PS_<uid>_Files`, `PS_<uid>_Meta` (and `MareChara_*`).
* `RemoveNotSyncedFiles` rules, compressed alternates and the default `AlwaysCompressed`, forbidden transfers, LRU eviction.
* MCDF v1 layout; PlayerSync's MCDF export covers only the local player.
* Penumbra `.pmp` import requirements and FileVersion 3 compatibility; meta string format versions 0-2.
* All reflection targets exist with the expected signatures in the inspected sources and in Dalamud 15.0.3.9's assemblies.

**Assumptions requiring a live FFXIV session** (see `docs/TESTING.md`)

* Reflection binding succeeds against the installed PlayerSync build, including `CachedPlayer` and `RemoveNotSyncedFiles` invocation with a JSON-cloned `CharacterData`.
* `GetVisiblePairs` and `GetHandledAddresses` produce the expected list in crowded areas.
* Penumbra's `ConvertManips` and `MetaDictionary` serialization work through reflection in the installed Penumbra version.
* A generated `.pmp` imports and renders correctly in Penumbra, and a generated MCDF applies through PlayerSync's Character Data Hub or `PlayerSync.LoadMcdf`.
* The ImGui window renders and behaves as designed.
