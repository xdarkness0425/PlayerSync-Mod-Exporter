# Third-party notices

## Redistributed dependencies

### Penumbra.Api (NuGet 5.15.1)
MIT License. https://github.com/Ottermandias/Penumbra.Api

### K4os.Compression.LZ4 and K4os.Compression.LZ4.Legacy (NuGet 1.3.8)
MIT License, Copyright (c) 2017 Milosz Krajewski. https://github.com/MiloszKrajewski/K4os.Compression.LZ4

## Formats and behaviour implemented from documented sources

### PlayerSync client (MareSynchronosClient) and MareAPI
The MCDF file layout, the `FileCache.csv` layout, the content-hash scheme and the names of the data members read by reflection come from
the PlayerSync client source (https://github.com/universalconquistador/MareSynchronosClient, commit b2f8ce8) and its MareAPI submodule.
No source files were copied; the formats were re-implemented. The test `CompatibilityTests` contains small type declarations mirroring
`MareCharaFileData`. `integrations/playersync/0001-source-quality-export-ipc.patch` is a patch to the PlayerSync client: it contains
context lines from PlayerSync's own files and new code meant to be distributed as part of PlayerSync under PlayerSync's license.
Both projects are licensed under:

```
MIT License

Copyright (c) 2022 Penumbra-Sync
Copyright (c) 2022 Mare Synchronos

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Technical references (no code used)

### Snappy (https://github.com/BeSlightly/Snappy)
Snappy has no license file, so no permission to reuse its code was assumed. It was studied as a reference for how a plugin can read
PlayerSync's pair data (originally based on work by eqbot, ViviAshe and astrodoobs; maintained by BeSlightly). All code in this repository
was written independently.

### Penumbra (https://github.com/xivdev/Penumbra)
The `.pmp` layout, import rules and meta-manipulation format were studied in Penumbra's source. No code was copied. At runtime the plugin
calls the Penumbra assembly that is already loaded in the game to convert meta manipulations.
