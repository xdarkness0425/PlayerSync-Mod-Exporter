# Original (source-quality) textures

Sources: PlayerSync client `b2f8ce8` (version 1.15.5.9, MIT). The installed PlayerSync DLL was not available for this work, so everything
below was verified against that source only. Nothing here has been tested in game yet; see [TESTING.md](TESTING.md).

## What PlayerSync already does

PlayerSync's texture compression setting (Settings → Performance) is `PlayerPerformanceConfig.TextureCompressionMode` (`CompressedAlternateUsage`):

| Setting | Value | Effect |
|---|---|---|
| Always use source quality | `AlwaysSourceQuality` (0) | Downloads and applies the exact files the other player uploaded |
| Use automatically compressed textures for new downloads | `CompressedNewDownloads` (1) | Server-compressed alternate for files not yet downloaded |
| Always use automatically compressed textures | `AlwaysCompressed` (3) | **Default.** Alternate wherever the server has one |

Pairs on the **Override UIDs** list (`UIDsToOverride`) always get source quality (`PairHandler.ComputeCompressedAlternateUsage`). In
`FileDownloadManager.InitiateDownloadList` the alternate replaces the original only under the two compressed modes; otherwise the original
hash is downloaded, un-munged, LZ4-decoded and kept only if its SHA-1 matches.

Under the default mode the originals of substituted textures are never downloaded, which is why exports could only contain the compressed
versions. Changing the setting or the override list would change how the user syncs every time, so the exporter does not do that.

## The integration

**PlayerSync side** (a small extension, delivered as a patch in [`integrations/playersync`](../integrations/playersync)): an export-scoped
IPC, `PlayerSync.SourceQualityExport.*` (API version 1). For one visible, unpaused pair it fetches the originals of the requested hashes that
are in the pair's permission-filtered character data, using the existing server metadata request, forbidden-file recording, authenticated
download (slots, speed limit, 404 reporting) and transport decoding, and writes them, SHA-1 verified, into a staging folder the exporter
owns. It never writes to the cache or `FileCache.csv`, never calls `PersistFileToStorage`, and leaves the compression setting, the override
list and the pair's applied appearance unchanged. Full contract: `PlayerSync/Services/SourceQualityExport/README.md` in the patch.

Only the user's own PlayerSync needs the patch. Other players need nothing.

**Exporter side:**

1. Each capture records the pair's effective compression mode (computed like `ComputeCompressedAlternateUsage`, read only). It is shown in
   the window and written to `manifest.json` → `textureSource`.
2. When compressed textures are in use and the installed PlayerSync offers the IPC, **Download originals for this export** creates
   `<export folder>/.psme-originals-<id>/` (with a marker file) and asks PlayerSync for exactly those textures. Only textures that are synced,
   not forbidden and shown as compressed (or invalid) substitutes are requested.
3. When PlayerSync finishes, the character is captured and analysed again. Staged originals are offered to the analyzer only if PlayerSync
   reported them as `Downloaded` and they lie inside that folder, and the analyzer verifies their SHA-1 again before using them
   (`provenance: SourceQualityDownload`, `representation: Original`).
4. The staging folder is deleted when you select another character, cancel, the request fails, the window closes during the download, or the
   plugin unloads. Leftovers from a crash are removed the next time a download starts. Only folders with the marker are ever deleted.

## Representations

| Case | Result |
|---|---|
| Original downloaded for the export | `Original`, `provenance: SourceQualityDownload`, verified against `hash` |
| Original already in the cache or an installed mod | `Original` / `OriginalFromInstalledMod`, verified against `hash`, no download |
| Only a valid alternate (no download, or PlayerSync could not provide the original) | `CompressedAlternate`, verified against `compressedAlternateHash`, package Partial, file note says why |
| Alternate not a valid texture | `InvalidContent`, not exported |
| Staged bytes that do not match the hash | Rejected; the alternate (if valid) is used and flagged |
| Forbidden by the server / no longer on the server / download failed / SHA-1 mismatch at PlayerSync | Listed in `textureSource.download.notRetrieved` with PlayerSync's status and message |
| Not synced to you (filters, their permissions) | Never requested, never exported |
| Transport compression (munge + LZ4) | Undone by PlayerSync before the file is staged |

## Limits

* Only files the other player currently has applied and that PlayerSync syncs to you. This is not access to anyone's mod library.
* An original the server no longer has cannot be recovered, and an original is never rebuilt from an alternate.
* Without the patched PlayerSync, the button is disabled with an explanation. Setting "Always use source quality" yourself and reloading the
  pair in PlayerSync still works as before.
