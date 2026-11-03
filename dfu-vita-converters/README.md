# DFU Vita converters

These scripts turn Daggerfall Unity mods into data files for the PS Vita port of Daggerfall Unity. The Vita can't run mod scripts and has a fraction of a PC's memory, so each mod's data is converted on the PC ahead of time from the mod's own files.

Only the Vita port's loaders read the files these scripts write. PC DFU ignores them.

## Requirements

- Python 3 with `numpy`, `Pillow` and `UnityPy` (`pip install numpy pillow UnityPy`); `lz4pack.py` also needs `lz4`
- A Daggerfall `ARENA2` folder (for the palette, `ART_PAL.COL`, and vanilla data)
- Blender 4.x for `wod_decimate.py` only

## generic/ (any mod of that kind)

| Script | Input | Output |
|---|---|---|
| `detpack.py --named <name> <archive>` | a model mod's `.dfmod` (prefab meshes + materials) | `<name>_models.bin` + `TEXTURE.<archive>` (palette textures) |
| `detpack.py` (no `--named`) | Daggerfall Expanded Textures' `.dfmod` | its models, climate/winter textures and region banners |
| `flatpack.py` | flat (billboard) sprite PNGs | palette `TEXTURE.<archive>` files |
| `modtexpack.py` | a texture set named `<archive>_<record>-0.png` | palette `TEXTURE.<archive>` |
| `worldpack.py` | a location/block world-data mod's JSON | one world pack |
| `dfmod_text.py` | any `.dfmod` | its text and JSON assets as files |
| `wod_decimate.py` | an FBX | the same mesh with fewer triangles (Blender) |
| `lz4pack.py` | a port pack | the same pack LZ4-compressed |

Each script explains its arguments in the comment at the top. Textures are mapped to Daggerfall's 256-colour palette by nearest colour, and pixels with alpha below 128 become transparent. Give `detpack.py` a compare folder and it also writes side-by-side sheets of each original texture and its palette version, worst match first, so you can check the look on the PC.

## examples/ (written for one mod each)

- `snowlesspack.py`: Snowless Swamp and Jungle
- `biomespack.py`: World of Daggerfall - Biomes
- `bake_wodfar.py`: Distant Terrain full mode
- `wodtexpack.py`: World of Daggerfall materials (runs `generic/modtexpack.py`)
- `rrppack.py`: RMB Resource Pack (writes a job list for a baker in the port's Unity project)
- `basicroads_pack.py`: Basic Roads

## Make your own

Ask the mod's author before you share anything you convert from their mod.

1. See what the mod contains:

   ```
   python3 -I generic/dfmod_text.py <mod.dfmod>            # counts by kind
   python3 -I generic/dfmod_text.py <mod.dfmod> <out dir>  # extracts its text and JSON
   ```

2. Pick the script for what you found:

   - PNG textures named `<archive>_<record>-0.png`: `generic/modtexpack.py <ARENA2 dir> <out dir> <png>...`
   - Flat sprites plus world data that places them: `generic/flatpack.py <ARENA2 dir> <out dir> <report dir> <archive>... -- <png dir>... -- <world data json dir>...`
   - Models: `generic/detpack.py <mod.dfmod> <ARENA2 dir> <out dir> --named <name> <archive>`. Pick an archive number no other mod uses.
   - Location or block JSON: `generic/worldpack.py <out.pak> <ARENA2 dir> <json dir>`
   - C# scripts: these need porting by hand. They can't run on the Vita.

3. Read what the script prints. `modtexpack.py` reports how far each record is from Daggerfall's palette (0 means it already is palette art), and `flatpack.py` writes a report folder.
4. Copy `TEXTURE.<archive>` files into `Assets/StreamingAssets/VitaNative` and build. Texture files are read by `TextureFile`.
5. Compare the same spot on PC and Vita.

If no generic script fits, copy the closest one in `examples/`. `snowlesspack.py` is a good start for textures and `basicroads_pack.py` for raw data. Keep these rules:

- Texture files use the layout at the top of `detpack.py`: palette indices, top row first.
- Use the nearest `ART_PAL.COL` colour. Index 0 is transparent (alpha below 128) and index 255 is a lit window.
- Keep every record number, even for records you leave empty.
- Keep the original mod files out of your output folder.

The current alpha ships with no mods. It reads texture files, but the loaders for models and world packs are not in this release, so those packs need their own loader for now.

## What converts and what doesn't

Meshes, textures, flats and JSON/text data convert directly. C# mod scripts can't run on the Vita and have to be ported by hand.
`detpack.py` skips prefabs that have child objects or custom components.

Some steps of the Vita build run inside the port's Unity project and aren't in this folder: merging models into the city block meshes, building sprite atlases, and packing textures.
