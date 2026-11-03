// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked classic sky frames. DaggerfallSky read a palette and two 512x220 index
// frames from SKY00-31.DAT (or NITE00-03.IMG) at every sky change and turned them into Color32 arrays and ARGB32 textures on the Vita.
// Assets/Editor/Vita/VitaSkyBaker.cs reads every frame with DFU's own SkyFile/ImgFile on the PC into StreamingAssets/VitaNative/skies.pak
// ("VSKY", VitaPack layout, LZ4 by vita/tools/lz4pack.py): per day frame (sky << 8 | frame, frames 0-31) and night sky (NightFlag | n) the
// 768-byte RGB palette, then each hemisphere as a 512x256 Alpha8 level with GetColor32's row order (bottom-up; rows from the image height up
// unused). DaggerfallSky uploads them as they are and draws them through Daggerfall/Vita/UIPalette.
#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

public static class VitaClassicSky
{
    public const int PackMagic = 0x594B5356;    // "VSKY"
    const int Magic = 0x524B5356;               // "VSKR", each record
    public const uint NightFlag = 0x10000;
    public const int Width = 512, Height = 256;
    public const int DataOffset = 1024;         // palette at 64, hemispheres from here

    public static System.Action<uint, byte[]> BakeSink;     // VitaSkyBaker

    static VitaPack pack;
    static readonly HashSet<uint> missed = new HashSet<uint>();
    static readonly byte[] east = new byte[Width * Height], west = new byte[Width * Height];
    static readonly Color32[] colours = new Color32[256];

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "skies.pak"); }
    }

    public static uint DayKey(int sky, int frame)
    {
        return (uint)(sky << 8 | frame);
    }

    /// <summary>A baked frame: palette (alpha 255) and the hemisphere indices in shared buffers (night: west == east). False if missing.</summary>
    public static bool Read(uint key, out Color32[] palette, out byte[] eastIndices, out byte[] westIndices, out int height)
    {
        palette = null;
        eastIndices = westIndices = null;
        height = 0;
        if (pack == null)
            pack = new VitaPack(PackPath, PackMagic);
        if (pack.Count == 0)
            return false;
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            if (!pack.Read(key, out memory, out data, out length) || length < DataOffset + Width * Height || Marshal.ReadInt32(data, 0) != Magic ||
                (uint)Marshal.ReadInt32(data, 4) != key)
            {
                if (missed.Add(key))
                    Debug.LogFormat("VITANATIVE miss sky {0:x}", key);
                return false;
            }
            height = Marshal.ReadInt32(data, 8);
            int hemispheres = Marshal.ReadInt32(data, 12);
            if (length < DataOffset + hemispheres * Width * Height)
                return false;
            palette = colours;
            for (int i = 0; i < 256; i++)
                palette[i] = new Color32(Marshal.ReadByte(data, 64 + i * 3), Marshal.ReadByte(data, 65 + i * 3), Marshal.ReadByte(data, 66 + i * 3), 255);
            Marshal.Copy(new System.IntPtr(data.ToInt64() + DataOffset), east, 0, Width * Height);
            if (hemispheres > 1)
                Marshal.Copy(new System.IntPtr(data.ToInt64() + DataOffset + Width * Height), west, 0, Width * Height);
            eastIndices = east;
            westIndices = hemispheres > 1 ? west : east;
            return true;
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    /// <summary>Bake (editor): one record from source bitmaps (rows top-down, as SkyFile/ImgFile give them).</summary>
    public static void Save(uint key, byte[] rgb, int width, int height, params byte[][] hemispheres)
    {
        var record = new byte[DataOffset + hemispheres.Length * Width * Height];
        foreach (int[] v in new[] { new[] { 0, Magic }, new[] { 4, (int)key }, new[] { 8, height }, new[] { 12, hemispheres.Length } })
            System.BitConverter.GetBytes(v[1]).CopyTo(record, v[0]);
        System.Array.Copy(rgb, 0, record, 64, 768);
        for (int h = 0; h < hemispheres.Length; h++)
            for (int y = 0; y < height; y++)
                System.Array.Copy(hemispheres[h], y * width, record, DataOffset + h * Width * Height + (height - 1 - y) * Width, width);
        BakeSink(key, record);
    }
}
#endif
