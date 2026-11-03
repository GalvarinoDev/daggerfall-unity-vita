// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Daggerfall's textures are 256-colour palette art. On Vita they are kept that way: one byte per pixel (the original palette index,
// Alpha8) plus one shared 256x1 palette texture, and the shader looks the colour up (Resources/Vita/DaggerfallDefaultPalette.shader).
// Exact original colours at a quarter of ARGB32's memory, so full-size textures fit.
// Needs point filtering (DFU's default MainFilterMode): filtering would blend indices, not colours; other modes keep ARGB32.
// Mips: each level averages the colours of the one above and takes the block's own colour nearest to that average. Snapping the
// average to the whole palette put unrelated colours (green, pink) on brown/yellow wood at glancing angles: the "static" on shop
// shelves.
// Sprite atlases (TextureReader.TryPackAtlasOnCpu) use the same format with the Billboard palette shaders; their mips keep a pixel
// opaque when at least half its block is, as upstream's averaged alpha does at the 0.5 cutoff.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Utility;

public static class VitaPalette
{
    public static readonly int PaletteTex = Shader.PropertyToID("_PaletteTex");
    public const string ShaderName = "Daggerfall/Vita/DefaultPalette";
    public const string AutomapShaderName = "Daggerfall/Vita/AutomapPalette";
    public const string BillboardShaderName = "Daggerfall/Vita/BillboardPalette";
    public const string BillboardSpectralShaderName = "Daggerfall/Vita/BillboardSpectralPalette";   // spectral flats (Lysandus)
    public const string GhostShaderName = "Daggerfall/Vita/GhostPalette";                           // spectral enemies (SetupDemoEnemy)
    public static readonly int EmissionPaletteTex = Shader.PropertyToID("_EmissionPalette");
    public const string BillboardBatchShaderName = "Daggerfall/Vita/BillboardBatchPalette";
    public const string BillboardBatchNoShadowsShaderName = "Daggerfall/Vita/BillboardBatchNoShadowsPalette";
    public const string TilemapShaderName = "Daggerfall/Vita/TilemapPalette";
    public const string AtlasShaderName = "Daggerfall/Vita/AtlasPalette";

    // By palette (hash of its colours) and alpha index: ART_PAL.COL for Daggerfall's files, own palettes for some mod flats (flatpack.py)
    static readonly Dictionary<long, Texture2D> palettes = new Dictionary<long, Texture2D>();
    static readonly Dictionary<int, Texture2D> lastByAlpha = new Dictionary<int, Texture2D>();   // CachedPalette
    static Color32[] colours;   // the palette last loaded (Load), for mips and nearest-colour lookups
    static uint loaded;
    // By excluded index (-1 = none): 15-bit colour → nearest palette index (0 = not computed yet, stored as index + 1)
    static readonly Dictionary<int, byte[]> nearest = new Dictionary<int, byte[]>();
    static bool failureLogged;

    /// <summary>The shared palette as a 256x1 texture; alphaIndex gets alpha 0.</summary>
    public static Texture2D Palette(DFPalette palette, int alphaIndex)
    {
        Texture2D texture;
        if (palettes.TryGetValue(Key(Hash(palette), alphaIndex), out texture) && texture)
            return lastByAlpha[alphaIndex] = texture;
        Load(palette);
        return MakePalette((Color32[])colours.Clone(), loaded, alphaIndex);
    }

    /// <summary>Palette() for 768 bytes of RGB in native memory (a baked atlas record, VitaAtlasCache); no allocation once cached.</summary>
    public static Texture2D Palette(System.IntPtr rgb, int alphaIndex)
    {
        uint hash = 2166136261;
        for (int i = 0; i < 768; i++)
            hash = (hash ^ System.Runtime.InteropServices.Marshal.ReadByte(rgb, i)) * 16777619;
        Texture2D texture;
        if (palettes.TryGetValue(Key(hash, alphaIndex), out texture) && texture)
            return lastByAlpha[alphaIndex] = texture;
        var row = new Color32[256];
        for (int i = 0; i < 256; i++)
            row[i] = new Color32(System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3), System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3 + 1),
                System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3 + 2), 255);
        return MakePalette(row, hash, alphaIndex);
    }

    /// <summary>Palettes for a spectral atlas (indices remapped by TextureReader.SetSpectral): alpha as GetColor32's spectral path
    /// (alphaIndex 0, eyes 255, the rest 180) and the emission colour GetSpectralEmissionColors32 gives each index (eyes red, else the
    /// colour scaled by V^1.9), so the GPU looks up per pixel what upstream builds per pixel on the CPU.</summary>
    public static Texture2D Spectral(DFPalette palette, int alphaIndex, out Texture2D emission)
    {
        Load(palette);
        return Spectral((Color32[])colours.Clone(), loaded, alphaIndex, out emission);
    }

    /// <summary>Spectral() for 768 bytes of RGB in native memory (a baked atlas record).</summary>
    public static Texture2D Spectral(System.IntPtr rgb, int alphaIndex, out Texture2D emission)
    {
        uint hash = 2166136261;
        var row = new Color32[256];
        for (int i = 0; i < 768; i++)
            hash = (hash ^ System.Runtime.InteropServices.Marshal.ReadByte(rgb, i)) * 16777619;
        for (int i = 0; i < 256; i++)
            row[i] = new Color32(System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3), System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3 + 1),
                System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3 + 2), 255);
        return Spectral(row, hash, alphaIndex, out emission);
    }

    const int spectralEyes = 247;   // TextureReader.spectralEyesPatched
    static readonly Dictionary<long, Texture2D> spectralEmission = new Dictionary<long, Texture2D>();

    static Texture2D Spectral(Color32[] row, uint hash, int alphaIndex, out Texture2D emission)
    {
        long key = Key(hash ^ 0x9e3779b9u, alphaIndex);
        Texture2D albedo;
        if (palettes.TryGetValue(key, out albedo) && albedo && spectralEmission.TryGetValue(key, out emission) && emission)
            return albedo;
        var glow = new Color32[256];
        for (int i = 0; i < 256; i++)
        {
            row[i].a = (byte)(i == spectralEyes ? 255 : 180);
            if (i == spectralEyes)
                glow[i] = Color.red;
            else
            {
                float h, sat, v;
                Color.RGBToHSV(row[i], out h, out sat, out v);
                glow[i] = Color.Lerp(Color.black, row[i], Mathf.Clamp01(Mathf.Pow(v, 1.9f)));
            }
        }
        if (alphaIndex >= 0 && alphaIndex < 256 && alphaIndex != spectralEyes)
            row[alphaIndex].a = 0;
        albedo = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        albedo.name = "VitaSpectralPalette";
        emission = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        emission.name = "VitaSpectralEmission";
        foreach (Texture2D t in new[] { albedo, emission })
        {
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
        }
        albedo.SetPixels32(row);
        albedo.Apply(false, true);
        emission.SetPixels32(glow);
        emission.Apply(false, true);
        palettes[key] = albedo;
        spectralEmission[key] = emission;
        return albedo;
    }

    static Texture2D MakePalette(Color32[] row, uint hash, int alphaIndex)
    {
        var texture = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        texture.name = "VitaPalette";
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        if (alphaIndex >= 0 && alphaIndex < 256)
            row[alphaIndex].a = 0;
        texture.SetPixels32(row);
        texture.Apply(false, true);
        palettes[Key(hash, alphaIndex)] = texture;
        return lastByAlpha[alphaIndex] = texture;
    }

    static long Key(uint hash, int alphaIndex)
    {
        return (long)hash << 16 | (long)(alphaIndex & 0xffff);
    }

    // FNV-1a over the 256 RGB colours (same bytes as a baked record's palette)
    static uint Hash(DFPalette palette)
    {
        uint hash = 2166136261;
        for (int i = 0; i < 256; i++)
        {
            hash = (hash ^ palette.GetRed(i)) * 16777619;
            hash = (hash ^ palette.GetGreen(i)) * 16777619;
            hash = (hash ^ palette.GetBlue(i)) * 16777619;
        }
        return hash;
    }

    /// <summary>The palette texture made by Palette() for alphaIndex, or null. For materials whose shader doesn't declare _PaletteTex as a
    /// property, where Material.GetTexture returns null.</summary>
    public static Texture2D CachedPalette(int alphaIndex)
    {
        Texture2D texture;
        return lastByAlpha.TryGetValue(alphaIndex, out texture) ? texture : null;
    }

    // Instance id → original width << 16 | height, for index textures stretched to POT (VitaAtlas packs them at their own size)
    static readonly Dictionary<int, int> originalSizes = new Dictionary<int, int>();

    /// <summary>The bitmap size behind an index texture (its own size unless IndexTexture stretched it to POT).</summary>
    public static void OriginalSize(Texture texture, out int width, out int height)
    {
        int size;
        if (originalSizes.TryGetValue(texture.GetInstanceID(), out size))
        {
            width = size >> 16;
            height = size & 0xffff;
        }
        else
        {
            width = texture.width;
            height = texture.height;
        }
    }

    /// <summary>Alpha8 texture of the bitmap's palette indices (rows bottom-up, like TextureFile.GetColor32), with palette-aware mips.</summary>
    public static Texture2D IndexTexture(DFBitmap bitmap, DFPalette palette, bool mipMaps, bool stayReadable)
    {
        byte[] raw;
        int width, height;
        return IndexTexture(bitmap, palette, mipMaps, stayReadable, out raw, out width, out height);
    }

    /// <summary>IndexTexture that also returns the bytes it uploaded (POT, for VitaWorldTextures) and the uploaded size.</summary>
    public static Texture2D IndexTexture(DFBitmap bitmap, DFPalette palette, bool mipMaps, bool stayReadable, out byte[] raw, out int uploadedWidth, out int uploadedHeight)
    {
        DFSize size;
        byte[] level = Frame(bitmap, 0, 0, out size);
        int width = size.Width, height = size.Height;
        // NPOT index textures render wrong on the Vita: the 64x90 dungeon exit door showed in-between indices (blue/yellow specks) with
        // mips and Repeat, stripes with Clamp or without mips. The same image stretched to POT (nearest texel,
        // so the meshes' 0-1 UVs show the same texels) renders right. Still 8-bit, at most 2x the bytes of a small texture
        if (!Mathf.IsPowerOfTwo(width) || !Mathf.IsPowerOfTwo(height))
        {
            int potW = Mathf.NextPowerOfTwo(width), potH = Mathf.NextPowerOfTwo(height);
            uploadedWidth = potW;
            uploadedHeight = potH;
            var stretched = new byte[potW * potH];
            for (int y = 0; y < potH; y++)
            {
                int row = (int)((y + 0.5f) * height / potH) * width;
                for (int x = 0; x < potW; x++)
                    stretched[y * potW + x] = level[row + (int)((x + 0.5f) * width / potW)];
            }
            Texture2D texture = Build(stretched, potW, potH, palette, -1, mipMaps, stayReadable, out raw);
            if (texture)
                originalSizes[texture.GetInstanceID()] = width << 16 | height;
            return texture;
        }
        uploadedWidth = width;
        uploadedHeight = height;
        return Build(level, width, height, palette, -1, mipMaps, stayReadable, out raw);
    }

    /// <summary>A baked index texture (VitaWorldTextures): LoadRaw, remembering the bitmap size if it was stretched to POT.</summary>
    public static Texture2D LoadIndexTexture(System.IntPtr raw, int size, int width, int height, int originalWidth, int originalHeight, bool mipMaps, bool stayReadable)
    {
        Texture2D texture = LoadRaw(raw, size, width, height, mipMaps, stayReadable);
        if (texture && (width != originalWidth || height != originalHeight))
            originalSizes[texture.GetInstanceID()] = originalWidth << 16 | originalHeight;
        return texture;
    }

    // By palette hash and emission kind (VitaWorldTextures.Emit*)
    static readonly Dictionary<long, Texture2D> worldEmission = new Dictionary<long, Texture2D>();

    /// <summary>The emission colour per palette index for a world texture's emission kind (VitaWorldTextures.Emit*): what TextureReader's
    /// emission map gives a pixel of that index (window glass white, self-lit = albedo, fire walls lerp(fire, albedo, 0.3)).</summary>
    public static Texture2D WorldEmission(DFPalette palette, int kind)
    {
        Load(palette);
        return WorldEmission((Color32[])colours.Clone(), loaded, kind);
    }

    /// <summary>WorldEmission() for 768 bytes of RGB in native memory (a baked palette record).</summary>
    public static Texture2D WorldEmission(System.IntPtr rgb, int kind)
    {
        uint hash = 2166136261;
        var row = new Color32[256];
        for (int i = 0; i < 768; i++)
            hash = (hash ^ System.Runtime.InteropServices.Marshal.ReadByte(rgb, i)) * 16777619;
        for (int i = 0; i < 256; i++)
            row[i] = new Color32(System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3), System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3 + 1),
                System.Runtime.InteropServices.Marshal.ReadByte(rgb, i * 3 + 2), 255);
        return WorldEmission(row, hash, kind);
    }

    static Texture2D WorldEmission(Color32[] row, uint hash, int kind)
    {
        long key = (long)hash << 8 | (uint)kind;
        Texture2D texture;
        if (worldEmission.TryGetValue(key, out texture) && texture)
            return texture;
        var glow = new Color32[256];
        Color fireColor = new Color(0.706f, 0.271f, 0.086f);    // TextureReader's fire wall colour
        for (int i = 0; i < 256; i++)
        {
            if (kind == VitaWorldTextures.EmitWindow)
                glow[i] = i == 0xff ? (Color32)Color.white : new Color32();
            else if (kind == VitaWorldTextures.EmitBlack)
                glow[i] = new Color32(0, 0, 0, 255);
            else if (kind == VitaWorldTextures.EmitFireWall)
                glow[i] = Color32.Lerp(fireColor, row[i], 0.3f);
            else
                glow[i] = row[i];
        }
        texture = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        texture.name = "VitaWorldEmission";
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(glow);
        texture.Apply(false, true);
        worldEmission[key] = texture;
        return texture;
    }

    /// <summary>The bitmap's palette indices with rows bottom-up and a border of alphaIndex, laid out as BaseImageFile.GetColor32's.</summary>
    public static byte[] Frame(DFBitmap bitmap, int border, int alphaIndex, out DFSize sizeOut)
    {
        int width = bitmap.Width + border * 2, height = bitmap.Height + border * 2;
        var frame = new byte[width * height];
        if (border > 0 && alphaIndex != 0)
            for (int i = 0; i < frame.Length; i++)
                frame[i] = (byte)alphaIndex;
        for (int y = 0; y < bitmap.Height; y++)
            System.Buffer.BlockCopy(bitmap.Data, y * bitmap.Width, frame, (height - 1 - border - y) * width + border, bitmap.Width);
        sizeOut = new DFSize(width, height);
        return frame;
    }

    /// <summary>Alpha8 sprite atlas of palette indices; alphaIndex pixels are transparent and stay so in the mips. Null on failure.</summary>
    public static Texture2D AtlasTexture(byte[] indices, int width, int height, DFPalette palette, int alphaIndex, bool mipMaps, bool stayReadable)
    {
        byte[] raw;
        return Build(indices, width, height, palette, alphaIndex, mipMaps, stayReadable, out raw);
    }

    /// <summary>AtlasTexture that also returns the bytes it uploaded (null for NPOT), for VitaAtlasCache.</summary>
    public static Texture2D AtlasTexture(byte[] indices, int width, int height, DFPalette palette, int alphaIndex, bool mipMaps, bool stayReadable, out byte[] raw)
    {
        return Build(indices, width, height, palette, alphaIndex, mipMaps, stayReadable, out raw);
    }

    /// <summary>Alpha8 emission mask for a palette atlas (255 = the pixel glows in its palette colour, see the Billboard palette shaders);
    /// a mip pixel glows if any pixel of its 2x2 block does. Null on failure.</summary>
    public static Texture2D MaskTexture(byte[] mask, int width, int height, bool mipMaps, bool stayReadable)
    {
        byte[] raw;
        return Build(mask, width, height, null, -1, mipMaps, stayReadable, out raw);
    }

    /// <summary>MaskTexture that also returns the bytes it uploaded (null for NPOT), for VitaAtlasCache.</summary>
    public static Texture2D MaskTexture(byte[] mask, int width, int height, bool mipMaps, bool stayReadable, out byte[] raw)
    {
        return Build(mask, width, height, null, -1, mipMaps, stayReadable, out raw);
    }

    /// <summary>LoadRaw from native memory (VitaAtlasCache reads the bytes there directly). Null on failure.</summary>
    public static Texture2D LoadRaw(System.IntPtr raw, int size, int width, int height, bool mipMaps, bool stayReadable)
    {
        var texture = new Texture2D(width, height, TextureFormat.Alpha8, mipMaps);
        texture.filterMode = FilterMode.Point;
        try
        {
            texture.LoadRawTextureData(raw, size);
            texture.Apply(false, !stayReadable);
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaPalette: raw {0}x{1} mips={2} data={3} bytes: {4}", width, height, mipMaps, size, e.Message);
            Object.Destroy(texture);
            return null;
        }
        return texture;
    }

    // Terrain tileset: one buffer holds level 0 and every mip level (POT, tight layout), filled block by block
    // and mipped in place, so building a new climate's tileset needs 5.6 MB instead of a 16 MB Color32[] plus a 22 MB ARGB32 texture
    // (travelling to Sentinel crashed on both: runs 131-133).
    static Dictionary<int, byte> exact;

    /// <summary>Buffer for a square POT index texture with its full mip chain.</summary>
    public static byte[] NewMippedData(int dim)
    {
        int size = 0;
        for (int w = dim; w >= 1; w /= 2)
            size += w * w;
        return new byte[size];
    }

    /// <summary>Writes a GetColor32 block (rows bottom-up, opaque palette colours) into level 0 of an index buffer at (x, y).</summary>
    public static void InsertIndices(Color32[] src, int width, int height, byte[] dst, int dstWidth, int x, int y, DFPalette palette)
    {
        Load(palette);
        if (exact == null)
        {
            exact = new Dictionary<int, byte>();
            for (int i = 255; i >= 0; i--)
                exact[colours[i].r << 16 | colours[i].g << 8 | colours[i].b] = (byte)i;  // lowest index wins for repeated colours
        }
        for (int row = 0; row < height; row++)
        {
            for (int col = 0; col < width; col++)
            {
                Color32 c = src[row * width + col];
                byte index;
                if (!exact.TryGetValue(c.r << 16 | c.g << 8 | c.b, out index))
                    index = Nearest(c.r, c.g, c.b, -1);
                dst[(y + row) * dstWidth + x + col] = index;
            }
        }
    }

    /// <summary>Alpha8 texture from a NewMippedData buffer whose level 0 is filled: the mips are made in place (2x2 colour average snapped
    /// to the palette, as Build's). Null on failure.</summary>
    public static Texture2D MippedTexture(byte[] data, int dim, bool stayReadable)
    {
        var block = new byte[4];
        int src = 0;
        for (int w = dim; w > 1; w /= 2)
        {
            int dst = src + w * w, dw = w / 2;
            for (int y = 0; y < dw; y++)
            {
                for (int x = 0; x < dw; x++)
                {
                    int i = src + y * 2 * w + x * 2;
                    Color32 a = colours[data[i]], b = colours[data[i + 1]], c = colours[data[i + w]], d = colours[data[i + w + 1]];
                    block[0] = data[i]; block[1] = data[i + 1]; block[2] = data[i + w]; block[3] = data[i + w + 1];
                    data[dst + y * dw + x] = NearestOf(block, 4, (a.r + b.r + c.r + d.r) / 4, (a.g + b.g + c.g + d.g) / 4, (a.b + b.b + c.b + d.b) / 4);
                }
            }
            src = dst;
        }
        return LoadMipped(data, dim, stayReadable);
    }

    /// <summary>Alpha8 texture from a NewMippedData buffer whose levels are all filled already. Null on failure.</summary>
    public static Texture2D LoadMipped(byte[] data, int dim, bool stayReadable)
    {
        var texture = new Texture2D(dim, dim, TextureFormat.Alpha8, true);
        texture.filterMode = FilterMode.Point;
        try
        {
            texture.LoadRawTextureData(data);
            texture.Apply(false, !stayReadable);
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaPalette: mipped {0}x{0} data={1} bytes: {2}", dim, data.Length, e.Message);
            Object.Destroy(texture);
            return null;
        }
        return texture;
    }

    /// <summary>The next mip level of an opaque index image, made as IndexTexture makes its mips.</summary>
    public static byte[] HalfSize(byte[] src, int width, int height, DFPalette palette)
    {
        Load(palette);
        return Downsample(src, width, height, -1);
    }

    /// <summary>The colours of palette indices, as GetColor32 makes them (for the ARGB32 fallback).</summary>
    public static Color32[] ToColor32(byte[] indices, DFPalette palette, int alphaIndex)
    {
        Load(palette);
        var pixels = new Color32[indices.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            pixels[i] = colours[indices[i]];
            if (indices[i] == alphaIndex)
                pixels[i].a = 0;
        }
        return pixels;
    }

    // transparent: index that mips treat as see-through (-1 = none, walls/floors); palette null = an emission mask
    static Texture2D Build(byte[] level, int width, int height, DFPalette palette, int transparent, bool mipMaps, bool stayReadable, out byte[] raw)
    {
        raw = null;
        if (palette != null)
            Load(palette);
        var texture = new Texture2D(width, height, TextureFormat.Alpha8, mipMaps);
        texture.filterMode = FilterMode.Point;
        var levels = new List<byte[]>();
        levels.Add(level);
        // Exactly Unity's level count: NPOT sizes get one more 1x1 level than halving down to 1x1 gives
        int w = width, h = height;
        while (levels.Count < texture.mipmapCount)
        {
            if (w > 1 || h > 1)
            {
                level = palette != null ? Downsample(level, w, h, transparent) : DownsampleMask(level, w, h);
                w = Mathf.Max(1, w / 2);
                h = Mathf.Max(1, h / 2);
            }
            levels.Add(level);
        }

        // NPOT (sprite atlases; IndexTexture stretches to POT): the raw layout is plain rows + 1 byte, which RowAlignment's
        // size match misread (texels scattered). Let Unity lay out each level from SetPixels32 (Alpha8 takes the alpha)
        if (!Mathf.IsPowerOfTwo(width) || !Mathf.IsPowerOfTwo(height))
        {
            try
            {
                for (int m = 0; m < levels.Count; m++)
                {
                    byte[] l = levels[m];
                    var pixels = new Color32[l.Length];
                    for (int i = 0; i < l.Length; i++)
                        pixels[i].a = l[i];
                    texture.SetPixels32(pixels, m);
                }
                texture.Apply(false, !stayReadable);
            }
            catch (System.Exception e)
            {
                if (!failureLogged)
                    Debug.LogWarningFormat("VitaPalette: NPOT {0}x{1} mips={2}: {3}", width, height, mipMaps, e.Message);
                failureLogged = true;
                Object.Destroy(texture);
                return null;
            }
            return texture;
        }

        // Check the layout against the bytes Unity expects for this size (once per size); rows could be padded on other formats.
        int align = RowAlignment(texture, width, height, levels.Count);
        if (align == 0)
        {
            Object.Destroy(texture);
            return null;
        }
        long sizeKey = (long)width << 32 | (long)height << 8 | (uint)levels.Count;
        var data = new byte[Mathf.Max(LayoutSize(width, height, levels.Count, align), expectedSizes[sizeKey])];
        int offset = 0;
        w = width;
        h = height;
        foreach (byte[] l in levels)
        {
            int stride = (w + align - 1) / align * align;
            for (int y = 0; y < h; y++)
                System.Buffer.BlockCopy(l, y * w, data, offset + y * stride, w);
            offset += stride * h;
            w = Mathf.Max(1, w / 2);
            h = Mathf.Max(1, h / 2);
        }
        try
        {
            texture.LoadRawTextureData(data);
            texture.Apply(false, !stayReadable);
            raw = data;
        }
        catch (System.Exception e)
        {
            // Never break a load over this: the caller falls back to ARGB32. Logged once.
            if (!failureLogged)
                Debug.LogWarningFormat("VitaPalette: {0}x{1} mips={2} data={3} bytes: {4}", width, height, mipMaps, data.Length, e.Message);
            failureLogged = true;
            Object.Destroy(texture);
            return null;
        }
        return texture;
    }

    static readonly Dictionary<long, int> alignments = new Dictionary<long, int>();   // (width, height, levels) → row alignment, 0 = none fits
    static readonly Dictionary<long, int> expectedSizes = new Dictionary<long, int>(); // (width, height, levels) → raw bytes Unity expects

    static int RowAlignment(Texture2D texture, int width, int height, int levels)
    {
        long key = (long)width << 32 | (long)height << 8 | (uint)levels;
        int align;
        if (alignments.TryGetValue(key, out align))
            return align;
        int expected = texture.GetRawTextureData().Length;
        align = 0;
        foreach (int candidate in new[] { 1, 4, 8, 16, 32 })
        {
            // NPOT sizes want one spare byte after the tight layout (runs 100-101: always exactly +1, same level count)
            int size = LayoutSize(width, height, levels, candidate);
            if (size == expected || size + 1 == expected)
            {
                align = candidate;
                break;
            }
        }
        expectedSizes[key] = expected;
        if (align == 0)
            Debug.LogWarningFormat("VitaPalette: no row layout matches {0}x{1} ({2} levels, Unity expects {3} bytes): ARGB32 for this size", width, height, levels, expected);
        alignments[key] = align;
        return align;
    }

    static int LayoutSize(int width, int height, int levels, int align)
    {
        int size = 0, w = width, h = height;
        for (int i = 0; i < levels; i++)
        {
            size += (w + align - 1) / align * align * h;
            w = Mathf.Max(1, w / 2);
            h = Mathf.Max(1, h / 2);
        }
        return size;
    }

    // Half size: average each 2x2 block's colours, take the block's colour nearest to it. Transparent pixels don't count towards the colour, and the
    // pixel is transparent when fewer than half its block are opaque.
    static byte[] Downsample(byte[] src, int w, int h, int transparent)
    {
        int dw = Mathf.Max(1, w / 2), dh = Mathf.Max(1, h / 2);
        var dst = new byte[dw * dh];
        var block = new byte[4];
        for (int y = 0; y < dh; y++)
        {
            for (int x = 0; x < dw; x++)
            {
                int r = 0, g = 0, b = 0, n = 0, total = 0;
                for (int sy = y * 2; sy < Mathf.Min(h, y * 2 + 2); sy++)
                {
                    for (int sx = x * 2; sx < Mathf.Min(w, x * 2 + 2); sx++)
                    {
                        total++;
                        if (src[sy * w + sx] == transparent)
                            continue;
                        Color32 c = colours[src[sy * w + sx]];
                        r += c.r; g += c.g; b += c.b;
                        block[n++] = src[sy * w + sx];
                    }
                }
                dst[y * dw + x] = n * 2 < total ? (byte)transparent : NearestOf(block, n, r / n, g / n, b / n);
            }
        }
        return dst;
    }

    static byte[] DownsampleMask(byte[] src, int w, int h)
    {
        int dw = Mathf.Max(1, w / 2), dh = Mathf.Max(1, h / 2);
        var dst = new byte[dw * dh];
        for (int y = 0; y < dh; y++)
            for (int x = 0; x < dw; x++)
                for (int sy = y * 2; sy < Mathf.Min(h, y * 2 + 2); sy++)
                    for (int sx = x * 2; sx < Mathf.Min(w, x * 2 + 2); sx++)
                        dst[y * dw + x] |= src[sy * w + sx];
        return dst;
    }

    // The one of the first n indices whose colour is nearest to (r, g, b)
    static byte NearestOf(byte[] indices, int n, int r, int g, int b)
    {
        byte best = indices[0];
        int bestDistance = int.MaxValue;
        for (int i = 0; i < n; i++)
        {
            Color32 c = colours[indices[i]];
            int dr = c.r - r, dg = c.g - g, db = c.b - b;
            int distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = indices[i];
            }
        }
        return best;
    }

    static byte Nearest(int r, int g, int b, int exclude)
    {
        byte[] cache;
        if (!nearest.TryGetValue(exclude, out cache))
            nearest[exclude] = cache = new byte[32768];
        int key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3);
        if (cache[key] != 0)
            return (byte)(cache[key] - 1);
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < 256; i++)
        {
            if (i == exclude)
                continue;
            int dr = colours[i].r - r, dg = colours[i].g - g, db = colours[i].b - b;
            int distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        cache[key] = (byte)(best + 1);
        return (byte)best;
    }

    // Daggerfall's files all use ART_PAL.COL; mod flats may have their own palette, so reload (and drop the lookups) when it changes
    static void Load(DFPalette palette)
    {
        uint hash = Hash(palette);
        if (colours != null && hash == loaded)
            return;
        if (colours == null)
            colours = new Color32[256];
        for (int i = 0; i < 256; i++)
            colours[i] = new Color32(palette.GetRed(i), palette.GetGreen(i), palette.GetBlue(i), 255);
        loaded = hash;
        exact = null;
        nearest.Clear();
    }
}
#endif
