// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked world textures. Every model, dungeon, interior and exterior texture went
// through TextureReader.GetTexture2D: read the TEXTURE file from ARENA2, decode the record, and build the palette mips (VitaPalette) on each
// load. Assets/Editor/Vita/VitaTextureBaker.cs runs that same GetTexture2D in the editor for every record and frame of TEXTURE.000-511 with
// the settings MaterialReader.GetMaterial uses, and this class's Save writes what the palette path uploaded (Alpha8 level 0 + mips, POT,
// exactly the LoadRawTextureData bytes) into StreamingAssets/VitaNative/textures.pak ("VWTX", VitaPack layout, LZ4 by vita/tools/lz4pack.py).
// At runtime GetTexture2D reads the record instead: no ARENA2 read, no decode, no mip building. Palettes are records of their own (768 bytes
// RGB, key PaletteFlag | hash), so the per-texture records hold only pixels. Windows, self-lit textures and fire walls are palette textures too
// now: their emission is a function of the palette index (VitaPalette.WorldEmission), so the shader looks it up (DefaultPalette _EMISSION).
// Spectral archives hold SetSpectral's indices and use VitaPalette.Spectral's palettes, as the sprite atlases do.
// A texture the pack lacks falls back to the runtime path and logs "VITANATIVE miss texture".

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaWorldTextures
{
    public const int PackMagic = 0x58545756;    // "VWTX"
    const int Magic = 0x52545756;               // "VWTR", each record
    public const uint PaletteFlag = 0x80000000;
    public const uint TilesetFlag = 0x20000000;  // | ground archive: a terrain tileset (TextureReader.GetTerrainTilesetTexture)

    // Emission kinds (TextureReader.GetTexture2D's emission maps, as functions of the palette index)
    public const int EmitWindow = 1;            // GetWindowColors32: index 255 white
    public const int EmitAlbedo = 2;            // self-lit: the albedo map is the emission map
    public const int EmitFireWall = 3;          // GetFireWallColors32: lerp(fire colour, albedo, 0.3)
    public const int EmitSpectral = 4;          // GetSpectralEmissionColors32 (VitaPalette.Spectral)
    public const int EmitBlack = 5;             // mixed emissive archives' unlit records: a black map

    public static System.Action<uint, byte[]> BakeSink;     // VitaTextureBaker: Save hands it the records instead of nothing

    static VitaPack pack;
    static readonly HashSet<uint> missed = new HashSet<uint>();

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "textures.pak"); }
    }

#if VITA_MODS
    // World of Daggerfall - Biomes (vita/tools/bake_biomes.sh): ground archives 3, 4, 104 and 304
    static VitaPack biomes;
#endif

    static bool Read(uint key, out System.IntPtr memory, out System.IntPtr data, out int length)
    {
#if VITA_MODS
        if (DaggerfallUnity.Settings.VitaWorldOfDaggerfallBiomes)
        {
            if (biomes == null)
                biomes = new VitaPack(Path.Combine(Path.GetDirectoryName(PackPath), "biomes.pak"), PackMagic);
            if (biomes.Contains(key))
                return biomes.Read(key, out memory, out data, out length);
        }
#endif
        return pack.Read(key, out memory, out data, out length);
    }

    public static uint Key(int archive, int record, int frame)
    {
        return (uint)(archive << 16 | (record & 0xff) << 8 | (frame & 0xff));
    }

    public static uint PaletteKey(uint hash)
    {
        return PaletteFlag | (hash >> 1);
    }

    /// <summary>The settings the bake covers: GetMaterial's for world textures (palette path, no border/sharpen/normals, point filter).</summary>
    public static bool Covers(GetTextureSettings settings)
    {
        return settings.vitaPalette && settings.textureFile == null && !settings.atlasSource && settings.borderSize == 0 && !settings.sharpen &&
            !settings.createNormalMap && settings.archive < 512 &&
            DaggerfallUnity.Instance.MaterialReader.MainFilterMode == FilterMode.Point;
    }

    /// <summary>The baked texture for settings into results (albedo, palettes, record info). False if the pack doesn't have it.</summary>
    public static bool TryLoad(GetTextureSettings settings, int emission, bool mipMaps, bool stayReadable, ref GetTextureResults results)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return false;
        uint key = Key(settings.archive, settings.record, settings.frame);
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            if (!Read(key, out memory, out data, out length) || length < 64 || Marshal.ReadInt32(data, 0) != Magic ||
                (uint)Marshal.ReadInt32(data, 4) != key)
            {
                if (missed.Add(key))
                    Debug.LogFormat("VITANATIVE miss texture {0} {1} {2}", settings.archive, settings.record, settings.frame);
                return false;
            }
            int width = Marshal.ReadInt32(data, 8), height = Marshal.ReadInt32(data, 12);
            int originalWidth = Marshal.ReadInt32(data, 16), originalHeight = Marshal.ReadInt32(data, 20);
            int frames = Marshal.ReadInt32(data, 48);
            uint paletteKey = (uint)Marshal.ReadInt32(data, 52);
            int rawOffset = Marshal.ReadInt32(data, 56), rawLength = Marshal.ReadInt32(data, 60);
            if (rawOffset + rawLength + 4 > length || Marshal.ReadInt32(data, rawOffset + rawLength) != Magic)
                return false;

            Texture2D palette, glow = null;
            if (!Palette(paletteKey, settings.alphaIndex, emission, TextureFile.IsSpectralArchive(settings.archive), out palette, out glow))
                return false;
            // Without mips the texture is level 0 alone, the first width x height bytes (POT rows, no padding)
            Texture2D albedo = VitaPalette.LoadIndexTexture(new System.IntPtr(data.ToInt64() + rawOffset), mipMaps ? rawLength : width * height,
                width, height, originalWidth, originalHeight, mipMaps, stayReadable);
            if (!albedo)
                return false;
            VitaMemory.CountTexture(settings.archive, settings.record, settings.frame, stayReadable);
            results.albedoMap = albedo;
            results.vitaPalette = palette;
            results.vitaEmissionPalette = glow;
            results.isEmissive = glow != null;
            results.isWindow = ClimateSwaps.IsExteriorWindow(settings.archive, settings.record);
            results.singleRect = new Rect(0, 0, 1, 1);
            results.vitaBaked = true;
            results.vitaSize = new Vector2(Marshal.ReadInt32(data, 24), Marshal.ReadInt32(data, 28));
            results.vitaScale = new Vector2(Marshal.ReadInt32(data, 32), Marshal.ReadInt32(data, 36));
            results.vitaOffset = new Vector2(Marshal.ReadInt32(data, 40), Marshal.ReadInt32(data, 44));
            results.vitaFrameCount = frames;
            return true;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    // RGB palette record -> the alpha palette and, for emission kinds, the emission palette (both cached by VitaPalette)
    static bool Palette(uint paletteKey, int alphaIndex, int emission, bool spectral, out Texture2D palette, out Texture2D glow)
    {
        palette = glow = null;
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            if (!Read(paletteKey, out memory, out data, out length) || length != 768)
                return false;
            Texture2D spectralGlow = null;
            palette = spectral ? VitaPalette.Spectral(data, alphaIndex, out spectralGlow) : VitaPalette.Palette(data, alphaIndex);
            if (emission == EmitSpectral)
                glow = spectralGlow;
            else if (emission != 0)
                glow = VitaPalette.WorldEmission(data, emission);
            return palette;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    // VitaAtlas.IsPlain's size rule: a multiple of its border, at most 512
    static bool AtlasLevelsWanted(int width, int height)
    {
        int border = 1 << VitaAtlas.MaxLod;
        return width % border == 0 && height % border == 0 && width <= 512 && height <= 512;
    }

    /// <summary>
    /// Levels 0..maxLod of a texture at its own size (VitaAtlas.Fill: VitaPalette.Frame then HalfSize), from the pack: a POT texture's own
    /// mips, else the record's cell levels. levels[l] point into the decoded record; memory is what to free (Marshal.FreeHGlobal) when
    /// true. False (nothing to free, a miss logged) if the pack has no record of that size.
    /// </summary>
    public static bool ReadLevels(int archive, int record, int width, int height, int maxLod, out System.IntPtr memory, System.IntPtr[] levels)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        uint key = Key(archive, record, 0);
        System.IntPtr data;
        int length;
        memory = System.IntPtr.Zero;
        bool ok = false;
        try
        {
            if (pack.Count == 0 || !Read(key, out memory, out data, out length) || length < 72 || Marshal.ReadInt32(data, 0) != Magic ||
                Marshal.ReadInt32(data, 16) != width || Marshal.ReadInt32(data, 20) != height)
            {
                if (pack.Count > 0 && missed.Add(key | 0x40000000))
                    Debug.LogFormat("VITANATIVE miss texture levels {0} {1} ({2}x{3})", archive, record, width, height);
                return false;
            }
            bool stretched = Marshal.ReadInt32(data, 8) != width || Marshal.ReadInt32(data, 12) != height;
            int offset = stretched ? Marshal.ReadInt32(data, 64) : Marshal.ReadInt32(data, 56);
            if (stretched && Marshal.ReadInt32(data, 68) == 0)
                return false;
            int w = width, h = height;
            for (int l = 0; l <= maxLod; l++)
            {
                if (offset + w * h > length)
                    return false;
                levels[l] = new System.IntPtr(data.ToInt64() + offset);
                offset += w * h;
                w = Mathf.Max(1, w / 2);
                h = Mathf.Max(1, h / 2);
            }
            ok = true;
            return true;
        }
        finally
        {
            if (!ok && memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
            if (!ok)
                memory = System.IntPtr.Zero;
        }
    }

    /// <summary>Bake (editor): the record for what GetTexture2D's palette path just uploaded, and its palette.</summary>
    public static void Save(GetTextureSettings settings, TextureFile textureFile, DFBitmap bitmap, byte[] raw, int width, int height)
    {
        int originalWidth = bitmap.Width, originalHeight = bitmap.Height;
        if (BakeSink == null || raw == null)
            return;
        uint paletteKey = SavePalette(textureFile.Palette);

        uint key = Key(settings.archive, settings.record, settings.frame);
        DFSize size = textureFile.GetSize(settings.record), scale = textureFile.GetScale(settings.record);
        DFPosition offset = textureFile.GetOffset(settings.record);
        var memory = new MemoryStream();
        var w = new BinaryWriter(memory);
        // A stretched (NPOT) texture VitaAtlas can pack also gets its own-size levels 0..MaxLod (VitaAtlas.Fill's cell source)
        var cell = new MemoryStream();
        if ((width != originalWidth || height != originalHeight) && AtlasLevelsWanted(originalWidth, originalHeight))
        {
            DFSize frameSize;
            byte[] level = VitaPalette.Frame(bitmap, 0, 0, out frameSize);
            int lw = originalWidth, lh = originalHeight;
            for (int l = 0; l <= VitaAtlas.MaxLod; l++)
            {
                cell.Write(level, 0, level.Length);
                if (l < VitaAtlas.MaxLod)
                {
                    level = VitaPalette.HalfSize(level, lw, lh, textureFile.Palette);
                    lw /= 2;
                    lh /= 2;
                }
            }
        }
        int cellOffset = cell.Length > 0 ? (int)((512 + raw.Length + 4 + 511) & ~511) : 0;
        foreach (int v in new[] { Magic, (int)key, width, height, originalWidth, originalHeight, size.Width, size.Height, scale.Width, scale.Height,
            offset.X, offset.Y, textureFile.GetFrameCount(settings.record), (int)paletteKey, 512, raw.Length, cellOffset, (int)cell.Length })
            w.Write(v);
        w.Write(new byte[512 - memory.Position]);
        w.Write(raw);
        w.Write(Magic);
        if (cell.Length > 0)
        {
            w.Write(new byte[cellOffset - memory.Position]);
            w.Write(cell.ToArray());
        }
        BakeSink(key, memory.ToArray());
    }

    // Bake: the palette as a 768-byte RGB record, keyed by its hash
    static uint SavePalette(DFPalette palette)
    {
        var rgb = new byte[768];
        uint hash = 2166136261;
        for (int i = 0; i < 256; i++)
        {
            rgb[i * 3] = palette.GetRed(i);
            rgb[i * 3 + 1] = palette.GetGreen(i);
            rgb[i * 3 + 2] = palette.GetBlue(i);
        }
        foreach (byte b in rgb)
            hash = (hash ^ b) * 16777619;
        uint paletteKey = PaletteKey(hash);
        BakeSink(paletteKey, rgb);
        return paletteKey;
    }

    /// <summary>Bake (editor): TextureReader.GetTerrainTilesetTexture's index atlas with all its mips (a NewMippedData buffer).</summary>
    public static void SaveTileset(int archive, DFPalette palette, byte[] mipped, int dim)
    {
        if (BakeSink == null)
            return;
        uint paletteKey = SavePalette(palette);
        uint key = TilesetFlag | (uint)archive;
        var memory = new MemoryStream();
        var w = new BinaryWriter(memory);
        foreach (int v in new[] { Magic, (int)key, dim, dim, dim, dim, 0, 0, 0, 0, 0, 0, 0, (int)paletteKey, 512, mipped.Length, 0, 0 })
            w.Write(v);
        w.Write(new byte[512 - memory.Position]);
        w.Write(mipped);
        w.Write(Magic);
        BakeSink(key, memory.ToArray());
    }

    /// <summary>The baked terrain tileset of a ground archive into results (albedo with mips, palette). False if the pack doesn't have it.</summary>
    public static bool TryLoadTileset(int archive, bool stayReadable, ref GetTextureResults results)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return false;
        uint key = TilesetFlag | (uint)archive;
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            if (!Read(key, out memory, out data, out length) || length < 64 || Marshal.ReadInt32(data, 0) != Magic ||
                (uint)Marshal.ReadInt32(data, 4) != key)
            {
                if (missed.Add(key))
                    Debug.LogFormat("VITANATIVE miss tileset {0}", archive);
                return false;
            }
            int dim = Marshal.ReadInt32(data, 8);
            uint paletteKey = (uint)Marshal.ReadInt32(data, 52);
            int rawOffset = Marshal.ReadInt32(data, 56), rawLength = Marshal.ReadInt32(data, 60);
            if (rawOffset + rawLength + 4 > length || Marshal.ReadInt32(data, rawOffset + rawLength) != Magic)
                return false;
            Texture2D palette, glow;
            if (!Palette(paletteKey, -1, 0, false, out palette, out glow))
                return false;
            Texture2D albedo = VitaPalette.LoadRaw(new System.IntPtr(data.ToInt64() + rawOffset), rawLength, dim, dim, true, stayReadable);
            if (!albedo)
                return false;
            results.albedoMap = albedo;
            results.vitaPalette = palette;
            return true;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }
}
#endif
