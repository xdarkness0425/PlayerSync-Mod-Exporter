# Testing

## Environment used

* Linux container, .NET SDK 10.0.113.
* Dalamud reference assemblies 15.0.3.9 (`goatcorp/dalamud-distrib` `latest.zip`), pointed to with `DALAMUD_HOME`.
* No FFXIV client, Dalamud runtime, PlayerSync, Penumbra or Glamourer was running.

## Commands and results (2026-10-27)

```sh
export DALAMUD_HOME=/path/to/dalamud     # or install XIVLauncher / Dalamud dev hooks
dotnet build PlayerSyncModExporter.slnx -c Release
dotnet test  PlayerSyncModExporter.slnx -c Release
```

| Step | Result |
|---|---|
| `dotnet build -c Release` (Core, plugin, tests) | Succeeded, 0 warnings, 0 errors. DalamudPackager produced `bin/Release/PlayerSyncModExporter/latest.zip` and the manifest JSON (API level 15). |
| `dotnet test -c Release` | **110 passed, 0 failed, 0 skipped** (about 5 s). 65 original tests plus 45 for metadata and texture recovery. |

## What the automated tests cover

All tests use real files in a temporary folder that mimics PlayerSync's storage (cache folder with `<sha1>.<ext>` files, a Penumbra mod
directory and `FileCache.csv`). The tests are in `tests/PlayerSyncModExporter.Tests`.

| Required scenario | Tests |
|---|---|
| All required assets available | `AnalyzerTests.AllAssetsAvailable_AreVerifiedAndPreselected`, `ExportTests.CompleteExport_WritesValidPenumbraImportablePmp_McdfAndManifest` (also proves the cache is unchanged: same bytes, timestamps and file list) |
| Missing assets | `AnalyzerTests.MissingAsset_IsReportedNotSkipped`, `ExportTests.MissingAssets_ProducePartialPackage_WithReasons`, `ExportTests.NothingExportable_IsRefused` |
| Duplicate assets / identical hashes | `AnalyzerTests.DuplicateHashes_AreMergedIntoOneAsset`, `CompleteExport...` (two game paths, one archive entry), `NonPlayerObjects_AreOnlyIncludedWhenRequested_AndPlayerWinsConflicts` |
| Empty or invalid metadata | `AnalyzerTests.InvalidMetadata_IsReported`, `EmptyCharacter_ProducesNoAssets`, `PathSafetyTests.*`, `FileCacheCsvTests.ParsesValidLines_IgnoresMalformed_AndRejectsTraversal`, `PlayerSyncDataReaderTests.IncompatibleShape_IsReportedNotThrown`, `UnknownObjectKind_IsSkipped`, `MetaCodecTests.Garbage_IsNotDecodable` |
| Corrupt files / hash mismatch | `AnalyzerTests.CorruptFile_IsHashMismatch`, `ExportTests.SourceModifiedAfterAnalysis_FailsAndLeavesNothingBehind` |
| Interrupted operations / cancellation | `AnalyzerTests.Cancellation_StopsAnalysis`, `ExportTests.Cancellation_MidExport_CleansUpStaging`, `StagingTests.*` |
| Large packages | `ExportTests.LargePackage_IsStreamedAndVerified` (48 MiB payload streamed through a 1 MiB buffer and verified in both formats) |
| Insufficient disk space | `ExportTests.InsufficientDiskSpace_IsDetectedBeforeWriting` (simulated free space; nothing written) |
| Export-path conflicts | `ExistingFolder_FailPolicy_Throws_AndKeepsExisting`, `ExistingFolder_NumberedCopy_CreatesNewFolder`, `ReplaceExisting_OnlyReplacesPreviousExports`, `UnsafePackageName_IsSanitized` |
| PlayerSync unavailable / incompatible | `AnalyzerTests.PlayerSyncUnavailable_NoLocators_EverythingMissing`, `FailingLocator_DoesNotAbortAnalysis`, `PlayerSyncDataReaderTests.IncompatibleShape_IsReportedNotThrown`, `WithoutFilter_WarnsAndTreatsAllAsSynced` |
| Valid, inspectable, importable package | `CompatibilityTests.Pmp_SatisfiesPenumbraArchiveImportRules` (checks the rules Penumbra's archive importer and `SubMod` loader apply, extracts the zip and re-hashes every file), `CompatibilityTests.Mcdf_IsReadableWithPlayerSyncsReaderLogic` (reads the MCDF with the same `LZ4Stream`/`BinaryReader.ReadChars`/`ReadInt32`/JSON calls as PlayerSync's `MareCharaFileHeader.FromBinaryReader` and `McdfExtractFiles`) |
| Permission and licensing safeguards | `ExportWithoutConsent_IsRefused`, `NotSyncedAndForbiddenFiles_AreNeverExportable_EvenIfPresentLocally`, `CompressedAlternates_AreExportedButFlagged`, `UserExclusion_IsRecordedAsPartial`, manifest `sharingStatus: Unknown` asserted in `CompleteExport...` |
| Meta manipulations | `Version0Meta_IsEmbedded`, `BinaryMeta_WithoutPenumbra_IsPreservedButNotEmbedded`, `MetaCodecTests.*` |
| Metadata: known-good fixture | `MetadataRecoveryTests.KnownGoodV3Mod_RecoversNamesAndReproducesTheUsedSingleOption`, `V4Mod_MultiGroup_KeepsOriginalOrderAndEnablesUsedOptions` |
| Metadata: missing, malformed, partial | `NoInstalledCopy_FallsBackToDefaultOption_WithInferredCategoryAndUnavailableFields`, `WithoutModLibrary_NothingIsAttributed`, `PartiallyPopulatedMetadata_MarksMissingFieldsUnavailable`, `MalformedAndHostileMods_AreSkippedWithProblems`, `Limits_StopOversizedMods` |
| Metadata: Recovered/Inferred/Unavailable labels | `IdenticalFilesInTwoMods_TieIsUnavailable_ClearWinnerIsInferred`, `SameGamePathWithDifferentContent_IsNotAttributed`, `SameContentAtDifferentGamePath_IsNotAttributed`, `GamePathCategory_DescribesTheReplacedItem` (9 cases) |
| Metadata: ordering, defaults, file relationships, valid .pmp | `FileOnlyInAnUnusedSingleOption_StaysInDefaultOptionWithNote`, `ExportedPmpWithGroups_SatisfiesPenumbraGroupRules`, `IdenticalInputs_ProduceIdenticalPackageMetadata` |
| Textures: header validation (valid, truncated, invalid, size limits) | `TexFileValidatorTests.*` (5 valid formats, truncated header, truncated data, huge claimed surface, 6 invalid headers, random bytes) |
| Textures: every confirmed representation | `TextureRecoveryTests.ValidCompressedAlternate_IsPreservedByteForByte_AndVerifiedAgainstTheAlternateHash` (T4), `OriginalMissingFromPlayerSyncIndex_IsRecoveredFromInstalledModByGamePath` (T4 recovery), `CacheFilesAreAlreadyDecoded_AndAreNeverDecompressedAgain` (T2), `OriginalWithUnrecognizedHeader_IsStillExportedUnchanged` (T1) |
| Textures: corrupt, unsupported, mixed outcomes | `InvalidOrTruncatedCompressedAlternate_IsNotExported` (2 cases), `InstalledModWithDifferentFileAtThatPath_DoesNotReplaceTheAlternate`, `MixedTextureOutcomes_AreEachReported_AndThePackageIsPartialWithReasons` |

### Additional manual check (not in the repository)

`PlayerSyncDataReader.JsonClone` and `PlayerSyncDataReader.Read` were run against PlayerSync's real `CharacterData`,
`FileReplacementData` and `ObjectKind` source files (MareAPI `2a65787`) compiled into a scratch program. The deep copy was independent of
the original, `DataHash` still worked on the copy, and swaps, filtered files and Glamourer data were read correctly. This is not a
repository test because it would pull PlayerSync's MessagePack dependency into the test project.

## Not tested (requires a live FFXIV session)

None of the following has been run. They are not claimed to work until someone tests them in game:

1. Loading the plugin in Dalamud (`/xlsettings` → Experimental → Dev Plugin Locations → add the `bin/Release` folder) and opening `/psexport`.
2. Reflection binding to the installed PlayerSync: plugin instance through Dalamud's `PluginManager`, `PairManager.GetVisiblePairs`,
   `Pair.LastReceivedCharacterData`, `Pair.RemoveNotSyncedFiles` on a cloned `CharacterData`, `PairHandler.ActiveCompressionRedirects`,
   `FileCacheManager.GetAllFileCachesByHash(validate: false)`, `FileTransferOrchestrator.ForbiddenTransfers`.
3. The `PlayerSync.GetHandledAddresses` intersection and the character list with several visible pairs.
4. Penumbra meta conversion through `MetaApi.ConvertManips` and `MetaDictionary` serialization in the installed Penumbra.
5. Importing a generated `.pmp` in Penumbra, enabling it in a collection and checking that the appearance matches.
6. Applying a generated `.mcdf` to a GPose actor through PlayerSync's "From MCDF" tab.
7. The ImGui window: layout, the folder picker, the conflict popup, progress and cancellation during a real multi-GB export.
8. Behaviour while PlayerSync is disconnected, reloading, or downloading the selected pair's files.
9. Indexing a real Penumbra mod directory (hundreds of mods, FileVersion 4 files with conditions, IMC and combining groups) and how long it takes.
10. Importing a `.pmp` with recovered `group_NNN_*.json` files in the installed Penumbra and checking that the enabled options reproduce the look.
11. Real PlayerSync compressed alternates: confirming they pass the texture header check (only synthetic BC7 fixtures were used).

### Suggested in-game test plan

1. With a consenting friend paired and visible, open `/psexport`, select them and press **Capture and analyze**.
2. Compare counts with PlayerSync's own Character Analysis for that pair (`/sync` → Character Analysis).
3. Export both formats to an empty folder, confirm `manifest.json` lists Partial/Complete with sensible reasons.
4. Import the `.pmp` into a test collection assigned to a GPose actor; apply `appearance/glamourer.b64.txt` with Glamourer.
5. In GPose, apply the `.mcdf` to an actor through PlayerSync and compare.
6. Set PlayerSync texture compression to "Always use source quality", let the pair re-sync, re-capture: compressed alternates should disappear.
7. Pause the pair in PlayerSync: capture must be refused. Disable sounds for the pair: `.scd` files must show as "Not synced".
8. Start a large export and press Cancel: no folder or staging folder must remain.
