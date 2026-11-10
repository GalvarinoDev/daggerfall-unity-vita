// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Classic UI image baker (runtime side in Assets/Scripts/Vita/VitaImages.cs): every IMG, CIF, RCI, CFA, BSS and GFX file in ARENA2, read
// with DFU's own reader classes as ImageReader.GetImageData and the windows that open them directly do, into
// StreamingAssets/VitaNative/images.pak. Each record is decoded again with VitaImages.Parse (the Vita's own code) and compared with the
// reader's output before it is written. Editor only.
//   vita/tools/bake_images.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;
using DaggerfallWorkshop;

public static class VitaImageBaker
{
    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        DaggerfallUnity.Settings.AssetInjection = false;
        DaggerfallUnity.Instance.ChangeArena2Path(arena2);
        if (!DaggerfallUnity.Instance.IsReady)
        {
            Debug.Log("VITABAKE failed: ARENA2 not valid: " + arena2);
            return;
        }

        var records = new SortedDictionary<uint, byte[]>();
        var names = new Dictionary<uint, string>();
        int files = 0, images = 0, frames = 0, skipped = 0, bad = 0;
        foreach (string path in Directory.GetFiles(arena2))
        {
            string name = Path.GetFileName(path).ToUpperInvariant();
            BaseImageFile reader = Open(path, Path.GetExtension(name));
            if (reader == null)
                continue;
            VitaImages.File file = Collect(reader);
            if (file == null)
            {
                Debug.LogFormat("VITABAKE image {0}: not readable, left to the runtime path", name);
                skipped++;
                continue;
            }
            uint key = VitaPack.NameKey(name);
            if (names.ContainsKey(key))
            {
                Debug.LogFormat("VITABAKE failed: {0} and {1} share key {2:x8}", name, names[key], key);
                return;
            }
            names[key] = name;
            byte[] record = null;
            VitaImages.BakeSink = (k, bytes) => record = bytes;
            VitaImages.Save(name, file);
            if (!Same(file, record))
            {
                Debug.LogFormat("VITABAKE image {0}: decoded record differs", name);
                bad++;
                continue;
            }
            records[key] = record;
            files++;
            images += file.Records.Length;
            foreach (VitaImages.Record r in file.Records)
                frames += r.Frames.Length;
        }
        VitaImages.BakeSink = null;

        long total = VitaPackWriter.Write(output, VitaImages.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} files, {1} images, {2} frames, {3} left, {4} differ, {5:F1} MB -> {6}",
            files, images, frames, skipped, bad, total / 1048576.0, output);
    }

    // The reader DFU uses for each extension, opened as its callers open it
    static BaseImageFile Open(string path, string extension)
    {
        switch (extension)
        {
            case ".IMG": return new ImgFile().IsFilenameSupported(Path.GetFileName(path)) ? new ImgFile(path, FileUsage.UseMemory, true) : null;
            case ".CIF":
            case ".RCI": return new CifRciFile(path, FileUsage.UseMemory, true);
            case ".CFA": return new CfaFile(path, FileUsage.UseMemory, true);
            case ".BSS": return new BssFile(path, FileUsage.UseMemory, true);
            case ".GFX": return new GfxFile(path, FileUsage.UseMemory, true);
            default: return null;
        }
    }

    // What the reader hands out: per record size, offset and every frame; the palette a palettized IMG carries
    static VitaImages.File Collect(BaseImageFile reader)
    {
        if (string.IsNullOrEmpty(reader.FilePath))
            return null;    // Load refused it (e.g. ImgFile's unsupported FMAP files)
        var file = new VitaImages.File();
        file.Records = new VitaImages.Record[reader.RecordCount];
        for (int r = 0; r < file.Records.Length; r++)
        {
            var record = new VitaImages.Record();
            int count = reader.GetFrameCount(r);
            record.Frames = new DFBitmap[Math.Max(count, 0)];
            for (int f = 0; f < record.Frames.Length; f++)
            {
                DFBitmap bitmap = reader.GetDFBitmap(r, f);
                if (bitmap.Data == null || bitmap.Data.Length < bitmap.Width * bitmap.Height)
                    return null;
                Translate(reader, Path.GetFileName(reader.FilePath).ToUpperInvariant(), r, f, bitmap);
                record.Frames[f] = bitmap;
            }
            DFSize size = reader.GetSize(r);
            record.Width = size.Width;
            record.Height = size.Height;
            DFPosition offset = reader is ImgFile ? ((ImgFile)reader).ImageOffset :
                reader is CifRciFile ? ((CifRciFile)reader).GetOffset(r) : new DFPosition(0, 0);
            record.X = offset.X;
            record.Y = offset.Y;
            file.Records[r] = record;
        }
        var img = reader as ImgFile;
        if (img != null && img.IsPalettized)
        {
            file.Palette = new byte[768];
            for (int i = 0; i < 256; i++)
            {
                file.Palette[i * 3] = img.Palette.GetRed(i);
                file.Palette[i * 3 + 1] = img.Palette.GetGreen(i);
                file.Palette[i * 3 + 2] = img.Palette.GetBlue(i);
            }
        }
        return file;
    }

    // Language VPKs (VITA_LANG_IMAGES = folder of PNGs named as DFU's Textures/Img and Textures/CifRci): the translated picture goes into
    // this frame's palette indices, transparent pixels to index 0. Colours off the palette are logged for review.
    static void Translate(BaseImageFile reader, string name, int r, int f, DFBitmap bitmap)
    {
        string folder = Environment.GetEnvironmentVariable("VITA_LANG_IMAGES");
        if (string.IsNullOrEmpty(folder))
            return;
        string png = Path.Combine(folder, reader is ImgFile ? name + ".png" : string.Format("{0}_{1}-{2}.png", name, r, f));
        if (!File.Exists(png))
            return;
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        texture.LoadImage(File.ReadAllBytes(png));
        if (texture.width != bitmap.Width || texture.height != bitmap.Height)
        {
            Debug.LogFormat("VITABAKE language image {0}: {1}x{2}, the game's is {3}x{4}: left as is", name, texture.width, texture.height, bitmap.Width, bitmap.Height);
            UnityEngine.Object.DestroyImmediate(texture);
            return;
        }
        Color32[] pixels = texture.GetPixels32();
        UnityEngine.Object.DestroyImmediate(texture);
        // The palette the game draws this frame with: a palettized IMG's own, otherwise the file's palette (ART_PAL.COL etc.), which the
        // baker's readers never load (ImageReader does at runtime)
        DFPalette palette = reader.Palette;
        var img = reader as ImgFile;
        if (img == null || !img.IsPalettized)
        {
            palette = new DFPalette();
            palette.Load(Path.Combine(DaggerfallUnity.Instance.Arena2Path, reader.PaletteName));
        }
        var nearest = new Dictionary<int, byte>();
        int off = 0;
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                Color32 c = pixels[(bitmap.Height - 1 - y) * bitmap.Width + x];     // Texture2D rows run bottom-up
                if (c.a < 128)
                {
                    bitmap.Data[y * bitmap.Width + x] = 0;
                    continue;
                }
                int key = c.r << 16 | c.g << 8 | c.b;
                byte index;
                if (!nearest.TryGetValue(key, out index))
                {
                    int best = int.MaxValue;
                    for (int i = 1; i < 256; i++)
                    {
                        int dr = palette.GetRed(i) - c.r, dg = palette.GetGreen(i) - c.g, db = palette.GetBlue(i) - c.b;
                        int d = dr * dr + dg * dg + db * db;
                        if (d < best)
                        {
                            best = d;
                            index = (byte)i;
                        }
                    }
                    nearest[key] = index;
                    if (best > 0)
                        off++;
                }
                bitmap.Data[y * bitmap.Width + x] = index;
            }
        Debug.LogFormat("VITABAKE language image {0} {1}-{2}: replaced, {3} colours off the palette", name, r, f, off);
    }

    // The record through the Vita's own decoder gives back exactly what the reader produced
    static bool Same(VitaImages.File source, byte[] record)
    {
        GCHandle pin = GCHandle.Alloc(record, GCHandleType.Pinned);
        try
        {
            VitaImages.File decoded = VitaImages.Parse(pin.AddrOfPinnedObject(), record.Length, null);
            if (decoded == null || decoded.Records.Length != source.Records.Length || (decoded.Palette == null) != (source.Palette == null))
                return false;
            if (source.Palette != null && !Equal(source.Palette, decoded.Palette, 768))
                return false;
            for (int r = 0; r < source.Records.Length; r++)
            {
                VitaImages.Record a = source.Records[r], b = decoded.Records[r];
                if (a.Width != b.Width || a.Height != b.Height || a.X != b.X || a.Y != b.Y || a.Frames.Length != b.Frames.Length)
                    return false;
                for (int f = 0; f < a.Frames.Length; f++)
                    if (a.Frames[f].Width != b.Frames[f].Width || a.Frames[f].Height != b.Frames[f].Height ||
                        !Equal(a.Frames[f].Data, b.Frames[f].Data, a.Frames[f].Width * a.Frames[f].Height))
                        return false;
            }
            return true;
        }
        finally
        {
            pin.Free();
        }
    }

    static bool Equal(byte[] a, byte[] b, int count)
    {
        for (int i = 0; i < count; i++)
            if (a[i] != b[i])
                return false;
        return true;
    }
}
