// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// PC <-> Vita save converter. The Vita keeps each save in one SAVEn.vita file (VitaSaveIO parts, values in
// the generated VitaSaveCodec format); PC DFU keeps a SAVEn folder of FullSerializer JSON files. Both directions go through DFU's own
// classes: JSON -> SaveLoadManager.Deserialize -> VitaSaveCodec.Write, and VitaSaveCodec.Read -> SaveLoadManager.Serialize -> JSON.
// Bio and mod data are text in both; the thumbnail is Screenshot.jpg on PC and raw RGB24 (Screenshot.vita, rows bottom-up) on the Vita.
// Editor only: vita/tools/convert_saves.sh topc|tovita <in-folder> <out-folder>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Player;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Utility.AssetInjection;

public static class VitaSaveConverter
{
    const string Thumb = "Screenshot.vita", Jpg = "Screenshot.jpg", Bio = "bio.txt";
    const int ThumbWidth = 480;   // the Vita writes half its screen (480x272)

    // SaveLoadManager's part names and types, in the order VitaSaveGame writes them (the thumbnail goes second)
    static readonly KeyValuePair<string, Type>[] parts =
    {
        new KeyValuePair<string, Type>("SaveInfo.txt", typeof(SaveInfo_v1)),
        new KeyValuePair<string, Type>("SaveData.txt", typeof(SaveData_v1)),
        new KeyValuePair<string, Type>("FactionData.txt", typeof(FactionData_v2)),
        new KeyValuePair<string, Type>("QuestData.txt", typeof(QuestMachine.QuestMachineData_v1)),
        new KeyValuePair<string, Type>("DiscoveryData.txt", typeof(Dictionary<int, PlayerGPS.DiscoveredLocation>)),
        new KeyValuePair<string, Type>("ConversationData.txt", typeof(TalkManager.SaveDataConversation)),
        new KeyValuePair<string, Type>("NotebookData.txt", typeof(PlayerNotebook.NotebookData_v1)),
        new KeyValuePair<string, Type>("WorldVariationData.txt", typeof(WorldDataVariants.WorldVariationData_v1)),
        new KeyValuePair<string, Type>("QuestExceptions.txt", typeof(QuestMachine.StoredException[])),
        new KeyValuePair<string, Type>("AutomapData.txt", typeof(Dictionary<string, Automap.AutomapDungeonState>)),
    };

    public static void ToVita()
    {
        Run((input, output) =>
        {
            int n = 0;
            foreach (string folder in Directory.GetDirectories(input, "SAVE*").OrderBy(f => f))
            {
                var list = new List<KeyValuePair<string, byte[]>>();
                foreach (var part in parts)
                {
                    string file = Path.Combine(folder, part.Key);
                    if (!File.Exists(file))
                        continue;
                    object value = SaveLoadManager.Deserialize(part.Value, File.ReadAllText(file));
                    if (value == null)
                        throw new Exception(file + ": JSON did not deserialize");
                    list.Add(new KeyValuePair<string, byte[]>(part.Key, VitaSaveIO.Encode(value)));
                }
                if (list.Count == 0 || list[0].Key != parts[0].Key)
                {
                    Debug.Log("VITASAVECONV skip " + folder + ": no SaveInfo.txt");
                    continue;
                }
                string jpg = Path.Combine(folder, Jpg);
                if (File.Exists(jpg))
                    list.Insert(1, new KeyValuePair<string, byte[]>(Thumb, ThumbFromJpg(File.ReadAllBytes(jpg))));
                string bio = Path.Combine(folder, Bio);
                if (File.Exists(bio))   // PC: one line per entry; the Vita joins them with \n and writes them before the automap
                {
                    int at = list.FindIndex(p => p.Key == parts[parts.Length - 1].Key);
                    list.Insert(at < 0 ? list.Count : at, new KeyValuePair<string, byte[]>(Bio, VitaSaveIO.Encode(string.Join("\n", File.ReadAllLines(bio)))));
                }
                foreach (string mod in Directory.GetFiles(folder, "mod_*.txt"))
                    list.Add(new KeyValuePair<string, byte[]>(Path.GetFileName(mod), VitaSaveIO.Encode(File.ReadAllText(mod))));
                File.WriteAllBytes(Path.Combine(output, Path.GetFileName(folder) + VitaSaveIO.Extension), VitaSaveIO.Pack(list));
                Debug.LogFormat("VITASAVECONV {0} -> {1}{2}: {3} parts", folder, Path.GetFileName(folder), VitaSaveIO.Extension, list.Count);
                n++;
            }
            return n;
        });
    }

    public static void ToPC()
    {
        Run((input, output) =>
        {
            int n = 0;
            foreach (string file in Directory.GetFiles(input, "SAVE*" + VitaSaveIO.Extension).OrderBy(f => f))
            {
                Dictionary<string, byte[]> found = VitaSaveIO.ReadSave(file);
                if (found == null)
                    throw new Exception(file + ": not a readable Vita save");
                string folder = Path.Combine(output, Path.GetFileNameWithoutExtension(file));
                Directory.CreateDirectory(folder);
                foreach (var part in found)
                {
                    string path = Path.Combine(folder, part.Key);
                    Type type = parts.Where(p => p.Key == part.Key).Select(p => p.Value).FirstOrDefault();
                    if (type != null)
                        File.WriteAllText(path, SaveLoadManager.Serialize(type, VitaSaveCodec.Read(type, part.Value)));
                    else if (part.Key == Thumb)
                        File.WriteAllBytes(Path.Combine(folder, Jpg), JpgFromThumb(part.Value));
                    else if (part.Key == Bio)
                        File.WriteAllLines(path, System.Text.Encoding.UTF8.GetString(part.Value).Split('\n'));
                    else if (part.Key.StartsWith("mod_"))
                        File.WriteAllBytes(path, part.Value);
                    else
                        Debug.Log("VITASAVECONV " + file + ": unknown part " + part.Key + " left out");
                }
                Debug.LogFormat("VITASAVECONV {0} -> {1}: {2} parts", file, folder, found.Count);
                n++;
            }
            return n;
        });
    }

    static void Run(Func<string, string, int> convert)
    {
        string input = Environment.GetEnvironmentVariable("VITA_SAVE_IN"), output = Environment.GetEnvironmentVariable("VITA_SAVE_OUT");
        try
        {
            Directory.CreateDirectory(output);
            Debug.Log("VITASAVECONV done: " + convert(input, output) + " saves");
        }
        catch (Exception e)
        {
            Debug.Log("VITASAVECONV failed: " + e);
        }
    }

    // Nearest-pixel scale to the Vita's thumbnail width; GetPixels32 rows are bottom-up like the Vita's
    static byte[] ThumbFromJpg(byte[] jpg)
    {
        var source = new Texture2D(2, 2, TextureFormat.RGB24, false);
        source.LoadImage(jpg);
        Color32[] pixels = source.GetPixels32();
        int sw = source.width, sh = source.height, w = Math.Min(ThumbWidth, sw), h = Math.Max(1, sh * w / sw);
        UnityEngine.Object.DestroyImmediate(source);
        byte[] data = new byte[8 + w * h * 3];
        BitConverter.GetBytes(w).CopyTo(data, 0);
        BitConverter.GetBytes(h).CopyTo(data, 4);
        int o = 8;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                Color32 c = pixels[(y * sh / h) * sw + x * sw / w];
                data[o++] = c.r;
                data[o++] = c.g;
                data[o++] = c.b;
            }
        return data;
    }

    static byte[] JpgFromThumb(byte[] data)
    {
        int w = BitConverter.ToInt32(data, 0), h = BitConverter.ToInt32(data, 4);
        var pixels = new Color32[w * h];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = new Color32(data[8 + i * 3], data[9 + i * 3], data[10 + i * 3], 255);
        var texture = new Texture2D(w, h, TextureFormat.RGB24, false);
        texture.SetPixels32(pixels);
        byte[] jpg = texture.EncodeToJPG();
        UnityEngine.Object.DestroyImmediate(texture);
        return jpg;
    }
}
