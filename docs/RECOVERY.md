# Metadata and texture recovery

This document records what the exporter can and cannot recover about the mods behind a PlayerSync appearance, the evidence
for each conclusion, and how each case is handled. Sources: PlayerSync client `b2f8ce8` and MareAPI `2a65787` (MIT),
Penumbra `9bb7931` (read as a format reference only), Lumina from Dalamud 15.0.3.9 (texture header layout).

## 1. Mod names, metadata and option groups

### Root cause

PlayerSync never transfers mod metadata. The sender's `PlayerData/Data/FileReplacement` knows the resolved path inside their
mod folder, but `CharacterData.ToAPI()` and `FileReplacement.ToFileReplacementDto()` send only `FileReplacementData`
(`GamePaths`, `Hash`, `FileSwapPath`), grouped by hash. The receiver gets no mod name, author, version, option, group or
folder structure. Penumbra applies the files through PlayerSync's temporary mods (`PS_<uid>_Files`), which are built from
the same list and so carry no metadata either. `.mcdf` files contain only a free-text `Description` and the same list.

The only place original metadata can exist on the receiving computer is **the user's own installed Penumbra mods**: if the
same mod is installed locally, its `meta.json` and group files describe it, and its files can be matched to what PlayerSync
sent. PlayerSync itself indexes those files (`FileCacheManager.PenumbraPrefix`, `CacheMonitor`), which confirms they are
a legitimate local source.

### Recovery matrix

| Field | Source | Classification | Implementation |
|---|---|---|---|
| Mod name, author, description, version, website | `meta.json` of an installed mod that redirects the same game path to a file with the identical SHA-1 | **Recovered** (empty field: **Unavailable**) | `ModLibraryIndex` + `MetadataRecoverer`; manifest `metadata.sourceMods[]` as `{value, classification, source}` |
| Which mod a file belongs to | Same match | **Recovered** when one mod matches; **Inferred** when several mods contain the file and one explains strictly more of the character; **Unavailable** on a tie | manifest `files[].attribution[]` with `candidateMods` |
| Group name, type (Single/Multi), option names and descriptions, option order, option priority | Installed mod's groups (FileVersion 3 `group_NNN_*.json` or FileVersion 4 `meta.json` `Groups`) | **Recovered** | `.pmp` gets one `group_NNN_<mod> - <group>.json` per group the character uses, with the original type and the used options in original order |
| Author's default selection | Group `DefaultSettings` in the installed mod | **Recovered** | manifest `groups[].authorDefault` (not applied) |
| Options the character has enabled | Which options' files match | **Inferred** | manifest `groups[].inferredSelection`; the `.pmp` enables these so it reproduces the look |
| Files of the mod's default container | Installed mod's `default_mod.json` / `DefaultData` | **Recovered** attribution | stay in the `.pmp` default option (always applied, as in the original) |
| What game item a file replaces | Game path layout (`chara/equipment/e6001/...`) | **Inferred** | manifest `attribution[].inferredCategory`; never used as a mod name |
| Anything for a file with no identical installed copy | None | **Unavailable** | file stays in the default option; manifest says why |
| Options the character does not use, mod images, original archive layout | Never sent | **Unavailable** | listed in `metadata.unavailable` |
| IMC and combining groups | Read, but their data cannot be mapped to plain file redirections | **Unavailable** for file attribution | their files are not matched |

Rules that keep this truthful:

* A match requires the same game path **and** the same SHA-1 as the hash PlayerSync referenced. A file with the same name but
  different content, or the same content at another game path, is not attributed.
* The package's own name and author stay "reconstructed"; recovered mods are listed in the description and manifest, never
  presented as the package's author.
* A single-choice group can only enable one option. If a file only matches a different option than the one the rest of the
  character uses, it is kept in the default option with a note instead of breaking the group.
* Multi groups keep at most 32 options (Penumbra's `IModGroup.MaxMultiOptions`); the rest go to the default option with a warning.
* The mod directory is only read. Each JSON file is capped at 16 MiB, each mod at 512 groups, 512 options per group and
  250,000 redirections; mod files must resolve inside their own folder (no `..`, no drive letters, no links followed).

## 2. Textures

### Representations found in PlayerSync

| # | Representation | Where it happens (source) | Original bytes available? | Exporter handling |
|---|---|---|---|---|
| T1 | Original file in the cache, `<sha1>.<ext>` | `FileDownloadManager` writes the decoded file, `PersistFileToStorage` deletes it unless its SHA-1 equals the expected hash | Yes | Exported byte-for-byte, verified against `hash` (unchanged) |
| T2 | Transport compression: LZ4 (`LZ4Wrapper`) plus an XOR "munge" key | Applied by the file server; un-munged while downloading, `LZ4Wrapper.Unwrap` before writing; the temporary `<hash>.bin` is deleted in `finally` | Yes, already decoded on disk | **Never decoded again.** A still-wrapped copy fails the SHA-1 check and is reported as corrupt (test `CacheFilesAreAlreadyDecoded_AndAreNeverDecompressedAgain`) |
| T3 | NTFS/WOF file compression (`FileCompactor`, XPRESS8K) | Optional, Windows only | Yes, transparent to reads | Nothing to do: reads return the original bytes |
| T4 | Compressed alternate: a different file generated by the PlayerSync server | `CompressedAlternateManager`, `PairHandler.TryCalculateModdedDictionary` (default `AlwaysCompressed`), redirect in `ActiveCompressionRedirects` | **No.** Only the alternate is downloaded; it is a different, server-generated file with its own hash | See below |
| T5 | Crash leftover `<hash>.bin` (still munged/wrapped) | Only if the game dies between download and decode | Possibly, but rare | Not used: the exporter only reads `<hash>.<ext>` and PlayerSync re-downloads on the next apply |

### T4 recovery matrix

| Situation | Processing | Integrity check | Result |
|---|---|---|---|
| Original is in the cache or in PlayerSync's index | Already preferred over the alternate | SHA-1 = `hash` | `representation: Original`, Complete |
| Original is in an installed mod but not in PlayerSync's index | Look up the game path in the installed mods and hash the candidates | SHA-1 = `hash` | **Recovered exactly**: `representation: OriginalFromInstalledMod`, Complete |
| Only the alternate exists and its texture header is valid | Bytes preserved unchanged, no transcoding | SHA-1 = alternate hash; header checked (format, dimensions, mips, surface offsets, data length) | `representation: CompressedAlternate`, Partial with "cannot be reconstructed" |
| Only the alternate exists and it is not a valid texture (corrupt, truncated) | Not exported | Header check fails | `status: InvalidContent`, Partial with the reason |
| Neither exists | Nothing | | `Missing` |

The original cannot be rebuilt from an alternate: the alternate is a different file produced by the server, the client never
receives the source bytes, and the server's conversion is not part of the client source, so no reversal can be verified. The exporter therefore never claims an
alternate is the original and never transcodes one.

The header check (`TexFileValidator`) reads only the first 80 bytes and the file length, so a header that claims a huge
surface is rejected without allocating anything. Originals are checked too, but only for information: a hash-verified
original is exported unchanged even if its header is unusual, because those are the exact bytes the other player uses.

## 3. Limits that remain

* Metadata depends on the user having the same mods installed; for files from mods that are not installed nothing can be recovered.
* If the user renamed or edited their installed copy, the recovered name is the edited one ("names as recorded there").
* Two identical installed copies that explain the character equally well leave the file unattributed.
* Compressed alternates stay compressed unless the user switches PlayerSync to "Always use source quality" and the pair re-syncs.
* Meta manipulations stay in the package default option; PlayerSync sends them as one combined string with no per-mod split.
