// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Native saves: the stream helpers the generated VitaSaveCodec uses. FullSerializer's reflection walk took
// 19-21 s per save on the Vita (cmpd1); the codec is plain generated code over these.
// Format, tolerant like JSON: an object is a member count, then per member a key ("name/Type", from a string table: index, or -1 and the
// string the first time) and the value's byte length, so a reader skips members it doesn't know (upstream renamed or changed a field) and
// leaves missing ones at their defaults. Class values start with a tag: 0 null, 1 the declared type, 2 another generated type (its name
// follows), 3 a type the codec doesn't know (name + FullSerializer JSON).

#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class VitaSaveIO
{
    public sealed class Writer
    {
        // One stream for every part of every save: a new 64 KB stream per part doubled its way up to the part's size, ~2 MB of
        // garbage per save that the never-shrinking heap grew for. Parts are written one at a time (ToArray copies out).
        static readonly MemoryStream shared = new MemoryStream(1 << 16);
        readonly MemoryStream stream;
        public readonly BinaryWriter W;
        readonly Dictionary<string, int> strings = new Dictionary<string, int>();

        public Writer()
        {
            stream = shared;
            stream.SetLength(0);
            W = new BinaryWriter(stream, Encoding.UTF8);
        }

        public byte[] ToArray()
        {
            W.Flush();
            return stream.ToArray();
        }

        // Interned string (member keys, type names)
        public void Key(string key)
        {
            int index;
            if (strings.TryGetValue(key, out index))
            {
                W.Write(index);
                return;
            }
            strings.Add(key, strings.Count);
            W.Write(-1);
            W.Write(key);
        }

        // Member value with its length in front: Begin returns where the length goes, End fills it in
        public long Begin(string key)
        {
            Key(key);
            long at = stream.Position;
            W.Write(0);
            return at;
        }

        public void End(long at)
        {
            long end = stream.Position;
            stream.Position = at;
            W.Write((int)(end - at - 4));
            stream.Position = end;
        }

        public void Str(string s)
        {
            W.Write(s != null);
            if (s != null)
                W.Write(s);
        }
    }

    public sealed class Reader
    {
        public readonly BinaryReader R;
        readonly List<string> strings = new List<string>();

        public Reader(byte[] data)
        {
            R = new BinaryReader(new MemoryStream(data, false), Encoding.UTF8);
        }

        public string Key()
        {
            int index = R.ReadInt32();
            if (index >= 0)
                return strings[index];
            string s = R.ReadString();
            strings.Add(s);
            return s;
        }

        public long Position
        {
            get { return R.BaseStream.Position; }
            set { R.BaseStream.Position = value; }
        }

        public string Str()
        {
            return R.ReadBoolean() ? R.ReadString() : null;
        }
    }

    // Types written by name (tag 2/3, System.Type members): DFU's own assembly first, then everything loaded
    static readonly Dictionary<string, Type> types = new Dictionary<string, Type>();

    public static Type FindType(string name)
    {
        if (name == null)
            return null;
        Type t;
        if (types.TryGetValue(name, out t))
            return t;
        t = typeof(VitaSaveIO).Assembly.GetType(name);
        if (t == null)
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                if ((t = a.GetType(name)) != null)
                    break;
        types[name] = t;
        return t;
    }

    public static string TypeName(Type t)
    {
        return t != null ? t.FullName : null;
    }

    // A save is one file, Saves/SAVEn.vita (no folder per save): magic, version, part count, then per part a name (the JSON file it
    // replaces: SaveInfo.txt, Screenshot.vita, SaveData.txt, bio.txt...), its length and bytes, then magic again. SaveInfo comes first and
    // the thumbnail second, so the save list reads only the start of each file. Written as .tmp and swapped in: a kill mid-write keeps the
    // last save.
    public const string Extension = ".vita";
    const int magic = 0x56554644;   // "DFUV"
    const int version = 1;

    // A part's bytes: strings (bio, mods' JSON) as UTF-8, byte[] as is, save classes through the generated codec
    public static byte[] Encode(object value)
    {
        if (value is byte[])
            return (byte[])value;
        if (value is string)
            return Encoding.UTF8.GetBytes((string)value);
        return VitaSaveCodec.Write(value.GetType(), value);
    }

    public static byte[] Pack(List<KeyValuePair<string, byte[]>> parts)
    {
        int size = 16;
        foreach (var part in parts)
            size += part.Key.Length + 8 + part.Value.Length;
        var w = new BinaryWriter(new MemoryStream(size), Encoding.UTF8);
        WritePacked(w, parts);
        return ((MemoryStream)w.BaseStream).ToArray();
    }

    static void WritePacked(BinaryWriter w, List<KeyValuePair<string, byte[]>> parts)
    {
        w.Write(magic);
        w.Write(version);
        w.Write(parts.Count);
        foreach (var part in parts)
        {
            w.Write(part.Key);
            w.Write(part.Value.Length);
            w.Write(part.Value);
        }
        w.Write(magic);
        w.Flush();
    }

    // Bumped by every save write/delete, so the save list is re-read only after a change (SaveLoadManager.EnumerateSaves)
    public static int Changes;

    /// <summary>Pack straight into the save file; returns its length.</summary>
    public static long WriteSave(string path, List<KeyValuePair<string, byte[]>> parts)
    {
        Changes++;
        string tmp = path + ".tmp";
        VitaFile.Delete(tmp);
        long length;
        using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            WritePacked(new BinaryWriter(file, Encoding.UTF8), parts);
            length = file.Length;
        }
        VitaFile.Delete(path);
        File.Move(tmp, path);
        return length;
    }

    public static void WriteSave(string path, byte[] data)
    {
        Changes++;
        string tmp = path + ".tmp";
        VitaFile.Delete(tmp);
        File.WriteAllBytes(tmp, data);
        VitaFile.Delete(path);
        File.Move(tmp, path);
    }

    // The first maxParts parts by name (all of them by default), or null when the file is missing or broken
    public static Dictionary<string, byte[]> ReadSave(string path, int maxParts = int.MaxValue)
    {
        foreach (string file in new[] { path, path + ".tmp" })    // .tmp: killed between delete and move
        {
            try
            {
                using (var r = new BinaryReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 4096), Encoding.UTF8))
                {
                    if (r.ReadInt32() != magic || r.ReadInt32() != version)
                        continue;
                    var parts = new Dictionary<string, byte[]>();
                    int count = r.ReadInt32();
                    for (int i = 0; i < count && i < maxParts; i++)
                    {
                        string name = r.ReadString();
                        parts[name] = r.ReadBytes(r.ReadInt32());
                    }
                    if (maxParts < count || r.ReadInt32() == magic)
                        return parts;
                }
            }
            catch (Exception) { }   // missing or cut short
        }
        return null;
    }
}
#endif
