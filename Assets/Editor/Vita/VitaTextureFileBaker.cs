// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// TEXTURE file baker (runtime side in Assets/Scripts/Vita/VitaTextureFiles.cs): every record and frame of TEXTURE.000-511 as DFU's own
// TextureFile hands them out (headers, decoded RLE, solid colour archives), into StreamingAssets/VitaNative/texfiles.pak. Each record is
// decoded again with VitaTextureFiles' parsers (the Vita's own code) and compared with TextureFile's output before it is written. Editor only.
//   vita/tools/bake_texfiles.sh   (VITA_ARENA2 = ARENA2 folder, VITA_BAKE_OUT = output .pak, both Windows paths)

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.Utility;

public static class VitaTextureFileBaker
{
    public static void Bake()
    {
        string arena2 = Environment.GetEnvironmentVariable("VITA_ARENA2");
        string output = Environment.GetEnvironmentVariable("VITA_BAKE_OUT");
        var records = new SortedDictionary<uint, byte[]>();
        byte[] last = null;
        VitaTextureFiles.BakeSink = (key, bytes) => records[key] = last = bytes;
        int archives = 0, images = 0, frames = 0, left = 0, bad = 0;
        for (int archive = 0; archive < 512; archive++)
        {
            string name = TextureFile.IndexToFileName(archive), path = Path.Combine(arena2, name);
            if (!File.Exists(path) || !new TextureFile().IsFilenameSupported(name))
                continue;   // missing or refused by TextureFile.Load (215, 217, 436): the Vita fails the same way without reading
            var file = new TextureFile(path, FileUsage.UseMemory, true);
            if (string.IsNullOrEmpty(file.FilePath))
            {
                Debug.LogFormat("VITABAKE texfile {0}: not readable, left to the runtime path", name);
                left++;
                continue;
            }
            var header = new short[file.RecordCount * VitaTextureFiles.HeaderFields];
            for (int r = 0; r < file.RecordCount; r++)
            {
                DFPosition offset = file.GetOffset(r);
                DFSize size = file.GetSize(r), scale = file.GetScale(r);
                int count = Math.Max(file.GetFrameCount(r), 0);
                int i = r * VitaTextureFiles.HeaderFields;
                header[i] = (short)offset.X;
                header[i + 1] = (short)offset.Y;
                header[i + 2] = (short)size.Width;
                header[i + 3] = (short)size.Height;
                header[i + 4] = (short)scale.Width;
                header[i + 5] = (short)scale.Height;
                header[i + 6] = (short)count;

                var bitmaps = new DFBitmap[count];
                bool readable = true;
                for (int f = 0; f < count && readable; f++)
                {
                    bitmaps[f] = file.GetDFBitmap(r, f);
                    readable = bitmaps[f].Data != null && bitmaps[f].Data.Length >= bitmaps[f].Width * bitmaps[f].Height;
                }
                if (!readable)
                {
                    Debug.LogFormat("VITABAKE texfile {0} record {1}: frame not readable", name, r);
                    bad++;
                    continue;
                }
                VitaTextureFiles.SaveFrames(archive, r, bitmaps);
                if (!SameFrames(bitmaps, last))
                {
                    Debug.LogFormat("VITABAKE texfile {0} record {1}: decoded record differs", name, r);
                    bad++;
                }
                images++;
                frames += count;
            }
            VitaTextureFiles.SaveHeader(archive, header);
            if (!SameHeader(header, last))
            {
                Debug.LogFormat("VITABAKE texfile {0}: decoded header differs", name);
                bad++;
            }
            archives++;
        }
        VitaTextureFiles.BakeSink = null;
        long total = VitaPackWriter.Write(output, VitaTextureFiles.PackMagic, records);
        Debug.LogFormat("VITABAKE done: {0} archives, {1} records, {2} frames, {3} left, {4} differ, {5:F1} MB -> {6}",
            archives, images, frames, left, bad, total / 1048576.0, output);
    }

    static bool SameHeader(short[] header, byte[] record)
    {
        GCHandle pin = GCHandle.Alloc(record, GCHandleType.Pinned);
        try
        {
            short[] decoded = VitaTextureFiles.ParseHeader(pin.AddrOfPinnedObject(), record.Length);
            if (decoded == null || decoded.Length != header.Length)
                return false;
            for (int i = 0; i < header.Length; i++)
                if (decoded[i] != header[i])
                    return false;
            return true;
        }
        finally
        {
            pin.Free();
        }
    }

    static bool SameFrames(DFBitmap[] frames, byte[] record)
    {
        GCHandle pin = GCHandle.Alloc(record, GCHandleType.Pinned);
        try
        {
            DFBitmap[] decoded = VitaTextureFiles.ParseFrames(pin.AddrOfPinnedObject(), record.Length, null);
            if (decoded == null || decoded.Length != frames.Length)
                return false;
            for (int f = 0; f < frames.Length; f++)
            {
                if (decoded[f].Width != frames[f].Width || decoded[f].Height != frames[f].Height)
                    return false;
                for (int i = 0; i < frames[f].Width * frames[f].Height; i++)
                    if (decoded[f].Data[i] != frames[f].Data[i])
                        return false;
            }
            return true;
        }
        finally
        {
            pin.Free();
        }
    }
}
