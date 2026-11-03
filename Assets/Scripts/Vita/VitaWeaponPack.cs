// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked first-person weapon frames. FPSWeapon decoded every frame of a WEAPON*.CIF to ARGB32,
// tinted per metal, and packed them with PackTextures at each weapon change.
// Assets/Editor/Vita/VitaWeaponBaker.cs packs each file's palette indices once on the PC into StreamingAssets/VitaNative/weapons.pak;
// here an entry becomes an Alpha8 atlas (a quarter of ARGB32) and the metal tint is a 256x1 palette: ImageProcessing.ChangeDye is a pure
// index swap (0x70-0x7F) and BaseImageFile.GetColor32 a palette lookup with index 0 transparent, so the pixels are the same.
// Point filtering only (filtering would blend indices); other filter modes keep upstream's ARGB32 atlas.
// Pack: "VWPN", version, count, then per file: name, atlas width, height, frame count, x/y/w/h per frame (rows bottom-up), index bytes.

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaWeaponPack
{
    public const int Magic = 0x4e505756;   // "VWPN"
    public const int Version = 1;
    public const string ShaderName = "Daggerfall/Vita/UIPalette";
    public static bool Enabled = true;

    static readonly int ColorTint = Shader.PropertyToID("_ColorTint");

    class Entry
    {
        public int Width, Height;
        public int[] Frames;   // x, y, w, h per frame
        public long Offset;    // index bytes
    }

    static Dictionary<string, Entry> entries;
    static readonly Dictionary<int, Texture2D> palettes = new Dictionary<int, Texture2D>();
    static Material material;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "weapons.pak"); }
    }

    static void ReadIndex()
    {
        entries = new Dictionary<string, Entry>(System.StringComparer.OrdinalIgnoreCase);
        try
        {
            using (var reader = new BinaryReader(File.OpenRead(PackPath)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                    return;
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    string name = reader.ReadString();
                    var entry = new Entry { Width = reader.ReadInt32(), Height = reader.ReadInt32() };
                    entry.Frames = new int[reader.ReadInt32() * 4];
                    for (int f = 0; f < entry.Frames.Length; f++)
                        entry.Frames[f] = reader.ReadInt32();
                    entry.Offset = reader.BaseStream.Position;
                    reader.BaseStream.Seek(entry.Width * entry.Height, SeekOrigin.Current);
                    entries[name] = entry;
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning("VitaWeaponPack: " + e.Message);
        }
    }

    /// <summary>The baked atlas of a weapon file (caller owns the texture) and its frame rects, or null if the pack lacks it, its frame
    /// count differs from the file's, or filtering isn't point.</summary>
    public static Texture2D Load(string filename, int frameCount, out Rect[] rects)
    {
        rects = null;
        if (!Enabled || DaggerfallUnity.Instance.MaterialReader.MainFilterMode != FilterMode.Point)
            return null;
        if (entries == null)
            ReadIndex();
        Entry entry;
        if (!entries.TryGetValue(filename, out entry) || entry.Frames.Length != frameCount * 4)
            return null;

        var data = new byte[entry.Width * entry.Height];
        using (var stream = File.OpenRead(PackPath))
        {
            stream.Seek(entry.Offset, SeekOrigin.Begin);
            int read = 0, n;
            while (read < data.Length && (n = stream.Read(data, read, data.Length - read)) > 0)
                read += n;
        }
        Texture2D texture = VitaPalette.AtlasTexture(data, entry.Width, entry.Height, null, 0, false, false);
        if (!texture)
            return null;
        texture.wrapMode = TextureWrapMode.Clamp;
        rects = new Rect[frameCount];
        float ru = 1f / entry.Width, rv = 1f / entry.Height;
        for (int i = 0; i < frameCount; i++)
        {
            int[] f = entry.Frames;
            rects[i] = new Rect(f[i * 4] * ru, f[i * 4 + 1] * rv, f[i * 4 + 2] * ru, f[i * 4 + 3] * rv);
        }
        return texture;
    }

    /// <summary>256x1 palette with the metal's dye swap applied, as FPSWeapon.GetWeaponTexture2D tints (none for steel).</summary>
    public static Texture2D Palette(DFPalette palette, MetalTypes metalType)
    {
        Texture2D texture;
        if (palettes.TryGetValue((int)metalType, out texture) && texture)
            return texture;
        var remap = new DFBitmap(256, 1);
        for (int i = 0; i < 256; i++)
            remap.Data[i] = (byte)i;
        if (metalType != MetalTypes.Steel && metalType != MetalTypes.None)
            remap = ImageProcessing.ChangeDye(remap, ImageProcessing.GetMetalDyeColor(metalType), DyeTargets.WeaponsAndArmor);
        var row = new Color32[256];
        for (int i = 0; i < 256; i++)
        {
            int c = remap.Data[i];
            row[i] = new Color32(palette.GetRed(c), palette.GetGreen(c), palette.GetBlue(c), (byte)(c == 0 ? 0 : 255));
        }
        texture = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        texture.name = "VitaWeaponPalette";
        texture.filterMode = FilterMode.Point;
        texture.wrapMode = TextureWrapMode.Clamp;
        texture.SetPixels32(row);
        texture.Apply(false, true);
        return palettes[(int)metalType] = texture;
    }

    /// <summary>DaggerfallUI.DrawTextureWithTexCoords for a baked atlas (OnGUI repaint).</summary>
    public static void Draw(Rect position, Texture2D indices, Texture2D palette, Rect texCoords, Color tint)
    {
        if (!material)
            material = new Material(Shader.Find(ShaderName));
        material.SetTexture(VitaPalette.PaletteTex, palette);
        material.SetColor(ColorTint, tint);
        Graphics.DrawTexture(position, indices, texCoords, 0, 0, 0, 0, material);
    }
}
#endif
