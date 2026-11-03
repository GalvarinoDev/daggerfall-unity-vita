// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// TEXTURE record sizes, scales and frame counts without loading the TEXTURE file. Enemies and town NPCs
// vita/tools/texinfo.py copies the fields from Daggerfall's TEXTURE.002-511 into StreamingAssets/VitaNative/texinfo.bin (66 KB).

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility.AssetInjection;

public static class VitaTextureInfo
{
    static Dictionary<int, short[]> table;   // archive -> per record width, height, scaleX, scaleY, frame count

    /// <summary>The billboard sizes and frame counts DaggerfallMobileUnit / MobilePersonBillboard compute from the TEXTURE file
    /// (same maths), or false when the archive isn't in the table (the caller loads the file as upstream).</summary>
    public static bool RecordSizes(int archive, out Vector2[] sizes, out int[] frames)
    {
        sizes = null;
        frames = null;
        if (table == null)
            Load();
        short[] r;
        if (!table.TryGetValue(archive, out r))
            return false;
        int count = r.Length / 5;
        sizes = new Vector2[count];
        frames = new int[count];
        for (int i = 0; i < count; i++)
        {
            int width = r[i * 5], height = r[i * 5 + 1], scaleX = r[i * 5 + 2], scaleY = r[i * 5 + 3];
            Vector2 finalSize;
            int xChange = (int)(width * (scaleX / BlocksFile.ScaleDivisor));
            int yChange = (int)(height * (scaleY / BlocksFile.ScaleDivisor));
            finalSize.x = (width + xChange);
            finalSize.y = (height + yChange);
            TextureReplacement.SetBillboardScale(archive, i, ref finalSize);
            sizes[i] = finalSize * MeshReader.GlobalScale;
            frames[i] = (ushort)r[i * 5 + 4];   // TextureFile reads it as (UInt16)ReadInt16
        }
        return true;
    }

    static void Load()
    {
        table = new Dictionary<int, short[]>();
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "texinfo.bin"));
        }
        catch (IOException)
        {
            Debug.LogWarning("VITANATIVE miss texinfo.bin");
            return;
        }
        using (BinaryReader reader = new BinaryReader(new MemoryStream(bytes)))
        {
            if (new string(reader.ReadChars(4)) != "VTXI" || reader.ReadInt32() != 1)
                return;
            int files = reader.ReadInt32();
            for (int f = 0; f < files; f++)
            {
                int archive = reader.ReadInt16();
                short[] r = new short[reader.ReadInt16() * 5];
                for (int i = 0; i < r.Length; i++)
                    r[i] = reader.ReadInt16();
                table[archive] = r;
            }
        }
    }
}
#endif
