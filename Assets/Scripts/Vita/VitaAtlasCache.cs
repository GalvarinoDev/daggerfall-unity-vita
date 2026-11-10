// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Sprite atlas cache on the memory card. Building one palette
// atlas (TextureReader.TryPackAtlasOnCpu: decode every frame of a TEXTURE archive, pack, palette mips) took 20-1335 ms with GCs on hardware:
// of an archive saves the finished atlas (Alpha8 bytes exactly as LoadRawTextureData takes them, plus the layout); later loads read it back.
// Key: the TEXTURE file (length + FNV hash of its bytes) and every setting that changes the atlas. A bad or stale file is rebuilt.
// Files: UserData/VitaCache/atlas/<archive / 64>/<archive>-<key>.bin (folders stay under ~100 entries: exFAT slows past that).
// Atlas pack: the same records baked on the PC by Assets/Editor/Vita/VitaAtlasBaker.cs into
// StreamingAssets/VitaNative/atlas.pak ("VATP", version, count, then key/offset/length per record; records at 512-byte offsets) are read
// first; the card cache stays for settings the bake doesn't cover.

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaAtlasCache
{
    const int Magic = 0x56415443;   // "VATC"
    // 3 = 1 again: deflating the pixels (v2, atc4) saved card space but not time (hits 12.9 -> 13.4 s) and added GCs (18 -> 32).
    // 4: pixel blocks at 512-byte file offsets, read with sceIoPread into 64-byte-aligned native memory and uploaded from there
    // (v3 hits read ~5 MB/s into managed arrays, 9.6 of 12.9 s)
    // 5: key without the TEXTURE file's contents. 6: the atlas's palette (768 bytes RGB) after the layout: mod flats may have their own
    // (vita/tools/flatpack.py), and a hit then reads nothing from ARENA2
    const int Version = 6;

#if !UNITY_EDITOR
    [DllImport("__Internal")] static extern int sceIoOpen(string path, int flags, int mode);
    [DllImport("__Internal")] static extern int sceIoClose(int fd);
    [DllImport("__Internal")] static extern long sceIoLseek(int fd, long offset, int whence);
#endif

    public const int PackMagic = 0x50544156;   // "VATP"
    public const int PackVersion = VitaPack.Version;   // what VitaAtlasBaker writes; vita/tools/lz4pack.py makes it VitaPack.VersionLz4
    public static System.Action<uint, byte[]> BakeSink;   // VitaAtlasBaker: Save hands it the record instead of writing a file

    public static string PackPath
    {
        get { return System.IO.Path.Combine(System.IO.Path.Combine(Application.streamingAssetsPath, "VitaNative"), "atlas.pak"); }
    }

    static VitaPack pack;   // atlas.pak, opened on first use
    public static bool TestBypass;   // test harness: build on the CPU instead (nothing read, nothing saved)

    static int Align512(long position)
    {
        return (int)((position + 511) & ~511L);
    }

    // The whole file in 64-byte-aligned native memory (one open, one read); memory is what to free. False on failure.
    public static bool ReadFile(string path, out System.IntPtr memory, out System.IntPtr data, out int length)
    {
        memory = data = System.IntPtr.Zero;
        length = 0;
#if UNITY_EDITOR
        return false;
#else
        int fd = sceIoOpen(path, 1, 0);   // SCE_O_RDONLY
        if (fd < 0)
            return false;   // not cached yet
        try
        {
            length = (int)sceIoLseek(fd, 0, 2);   // SCE_SEEK_END
            return length > 0 && VitaPack.ReadAligned(fd, 0, length, path, out memory, out data);
        }
        finally
        {
            sceIoClose(fd);
        }
#endif
    }

    // The packed record for key, else the card cache file
    static bool ReadRecord(int archive, uint key, string path, out bool packed, out System.IntPtr memory, out System.IntPtr data, out int length)
    {
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        packed = pack.Contains(key);
        if (packed)
            return pack.Read(key, out memory, out data, out length);
        if (pack.Count > 0)
            Debug.LogFormat("VITANATIVE miss atlas {0} {1:x8}", archive, key);
        return ReadFile(path, out memory, out data, out length);
    }

    // Key: the archive and every setting that decides the atlas (and whether TryPackAtlasOnCpu makes a palette atlas at all), not the
    // TEXTURE file: a hit then never touches ARENA2, whose 1443-entry folder costs ~90 ms per lookup on hardware.
    // ARENA2 is fixed game data; a changed install needs UserData/VitaCache/atlas deleted.
    static string Path(GetTextureSettings settings, bool mips, out uint key)
    {
        uint hash = 2166136261;
        foreach (int v in new[] { Version, settings.archive, settings.alphaIndex, settings.emissionIndex, settings.borderSize, settings.atlasPadding,
            settings.atlasMaxSize, settings.atlasShrinkUVs, mips ? 1 : 0, settings.autoEmission ? 1 : 0, settings.autoEmissionForWindows ? 1 : 0,
            settings.vitaPalette ? 1 : 0, settings.sharpen ? 1 : 0, settings.copyToOppositeBorder ? 1 : 0, settings.dilate ? 1 : 0,
            settings.createNormalMap ? 1 : 0, settings.createEmissionMap ? 1 : 0, (int)DaggerfallUnity.Instance.MaterialReader.MainFilterMode })
            hash = (hash ^ (uint)v) * 16777619;
        key = hash;
        return System.IO.Path.Combine(DaggerfallUnityApplication.PersistentDataPath,
            string.Format("VitaCache/atlas/{0}/{1}-{2:x8}.bin", settings.archive / 64, settings.archive, hash));
    }

    /// <summary>Test harness: whether the pack holds the mipped atlas for these settings.</summary>
    public static bool InPack(GetTextureSettings settings)
    {
        uint key;
        Path(settings, true, out key);
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        return pack.Contains(key);
    }

    [StructLayout(LayoutKind.Explicit)]
    struct FloatBits
    {
        [FieldOffset(0)] public int i;
        [FieldOffset(0)] public float f;
    }

    // Header reads straight from the native record: no managed copy, stream or reader per hit
    static System.IntPtr cursor;
    static int pos;

    static int Int()
    {
        int v = Marshal.ReadInt32(cursor, pos);
        pos += 4;
        return v;
    }

    static float Float()
    {
        FloatBits bits = new FloatBits();
        bits.i = Int();
        return bits.f;
    }

    static bool Bool()
    {
        return Marshal.ReadByte(cursor, pos++) != 0;
    }

    /// <summary>The cached atlas for settings, before any TEXTURE file is read; results get no textureFile. False if none.</summary>
    public static bool TryLoad(GetTextureSettings settings, bool mips, bool stayReadable, ref GetTextureResults results)
    {
        if (TestBypass)
            return false;
        uint key;
        string path = Path(settings, mips, out key);
        // Retro mode without mips (UseMipMapsInRetroMode off) has no records of its own: the packing doesn't depend on mips and level 0
        // comes first in a mipped record as plain rows (POT atlases), so its first width x height bytes are that atlas exactly
        bool topLevel = false;
        if (!mips)
        {
            uint mipKey;
            Path(settings, true, out mipKey);
            if (pack == null)
                pack = new VitaPack(PackPath, PackMagic);
            if (!pack.Contains(key) && pack.Contains(mipKey))
            {
                key = mipKey;
                topLevel = true;
            }
        }
        Texture2D atlas = null, mask = null;
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        bool packed;
        try
        {
            bool read = ReadRecord(settings.archive, key, path, out packed, out memory, out data, out length);
            try
            {
                if (!read)
                    return false;
                if (length < 64)
                    return false;
                cursor = data;
                pos = 0;
                // a packed record may be shared by keys that built the same atlas (1024 and 2048 limits), so only its index key matches
                if (Int() != Magic || ((uint)Int() != key && !packed))
                    return false;
                int width = Int(), height = Int();
                bool animated = Bool(), emissive = Bool();
                int records = Int();
                if (records < 0 || 18 + 44L * records + 4 > length)
                    return false;   // damaged file: don't read past it
                var sizes = new List<Vector2>(records);
                var scales = new List<Vector2>(records);
                var offsets = new List<Vector2>(records);
                var frameCounts = new List<int>(records);
                var indices = new List<RecordIndex>(records);
                for (int i = 0; i < records; i++)
                {
                    sizes.Add(new Vector2(Float(), Float()));
                    scales.Add(new Vector2(Float(), Float()));
                    offsets.Add(new Vector2(Float(), Float()));
                    frameCounts.Add(Int());
                    indices.Add(new RecordIndex() { startIndex = Int(), frameCount = Int(), width = Int(), height = Int() });
                }
                int rectCount = Int();
                if (rectCount < 0 || pos + 16L * rectCount + 16 + 768 > length)
                    return false;
                var rects = new List<Rect>(rectCount);
                for (int i = 0; i < rectCount; i++)
                    rects.Add(new Rect(Float(), Float(), Float(), Float()));
                int rawOffset = Int(), rawLength = Int(), maskOffset = Int(), maskLength = Int();
                System.IntPtr palette = new System.IntPtr(data.ToInt64() + pos);
                int end = emissive ? maskOffset + maskLength : rawOffset + rawLength;
                if (end + 4 > length || Marshal.ReadInt32(data, end) != Magic)
                    return false;   // cut short (a write interrupted by a kill)

                if (topLevel)
                    rawLength = maskLength = width * height;
                atlas = VitaPalette.LoadRaw(new System.IntPtr(data.ToInt64() + rawOffset), rawLength, width, height, mips, stayReadable);
                if (atlas && emissive)
                    mask = VitaPalette.LoadRaw(new System.IntPtr(data.ToInt64() + maskOffset), maskLength, width, height, mips, stayReadable);
                if (!atlas || (emissive && !mask))
                {
                    if (atlas)
                        Object.Destroy(atlas);
                    return false;
                }
                results.atlasSizes = sizes;
                results.atlasScales = scales;
                results.atlasOffsets = offsets;
                results.atlasFrameCounts = frameCounts;
                results.atlasRects = rects;
                results.atlasIndices = indices;
                results.albedoMap = atlas;
                results.emissionMap = mask;
                results.vitaPalette = TextureFile.IsSpectralArchive(settings.archive) ?
                    VitaPalette.Spectral(palette, settings.alphaIndex, out results.vitaEmissionPalette) : VitaPalette.Palette(palette, settings.alphaIndex);
                results.isAtlasAnimated = animated;
                results.isEmissive = emissive;
                return true;
            }
            finally
            {
                if (memory != System.IntPtr.Zero)
                    Marshal.FreeHGlobal(memory);
            }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaAtlasCache: {0}: {1}", path, e.Message);
        }
        return false;
    }

    public static void Save(GetTextureSettings settings, bool mips, GetTextureResults results, byte[] raw, byte[] maskRaw)
    {
        if (raw == null || (results.isEmissive && maskRaw == null) || TestBypass)
            return;
        uint key;
        string path = Path(settings, mips, out key);
        if (BakeSink != null)
        {
            var memory = new MemoryStream();
            Write(new BinaryWriter(memory), key, results, raw, maskRaw);
            BakeSink(key, memory.ToArray());
            return;
        }
        string temp = path + ".tmp";
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            using (var writer = new BinaryWriter(new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 65536)))
                Write(writer, key, results, raw, maskRaw);
            if (File.Exists(path))
                File.Delete(path);
            File.Move(temp, path);
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaAtlasCache: saving {0}: {1}", path, e.Message);
        }
    }

    static void Write(BinaryWriter writer, uint key, GetTextureResults results, byte[] raw, byte[] maskRaw)
    {
        writer.Write(Magic);
        writer.Write(key);
        writer.Write(results.albedoMap.width);
        writer.Write(results.albedoMap.height);
        writer.Write(results.isAtlasAnimated);
        writer.Write(results.isEmissive);
        int records = results.atlasIndices.Count;
        writer.Write(records);
        for (int i = 0; i < records; i++)
        {
            writer.Write(results.atlasSizes[i].x); writer.Write(results.atlasSizes[i].y);
            writer.Write(results.atlasScales[i].x); writer.Write(results.atlasScales[i].y);
            writer.Write(results.atlasOffsets[i].x); writer.Write(results.atlasOffsets[i].y);
            writer.Write(results.atlasFrameCounts[i]);
            RecordIndex index = results.atlasIndices[i];
            writer.Write(index.startIndex); writer.Write(index.frameCount); writer.Write(index.width); writer.Write(index.height);
        }
        writer.Write(results.atlasRects.Count);
        foreach (Rect r in results.atlasRects)
        {
            writer.Write(r.x); writer.Write(r.y); writer.Write(r.width); writer.Write(r.height);
        }
        int rawOffset = Align512(writer.BaseStream.Position + 16 + 768);
        int maskOffset = results.isEmissive ? Align512(rawOffset + raw.Length) : 0;
        writer.Write(rawOffset);
        writer.Write(raw.Length);
        writer.Write(maskOffset);
        writer.Write(results.isEmissive ? maskRaw.Length : 0);
        DFPalette palette = results.textureFile.Palette;
        for (int i = 0; i < 256; i++)
        {
            writer.Write(palette.GetRed(i));
            writer.Write(palette.GetGreen(i));
            writer.Write(palette.GetBlue(i));
        }
        writer.Write(new byte[rawOffset - writer.BaseStream.Position]);
        writer.Write(raw);
        if (results.isEmissive)
        {
            writer.Write(new byte[maskOffset - writer.BaseStream.Position]);
            writer.Write(maskRaw);
        }
        writer.Write(Magic);
        writer.Flush();
    }
}
#endif
