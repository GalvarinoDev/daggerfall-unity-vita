// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Native saves: writes Assets/Scripts/Vita/VitaSaveCodec.cs, plain Read/Write code for every type
// FullSerializer puts in a save, with the members FullSerializer's own metadata (fsMetaType) lists, so no reflection walk is left on the
// Vita (FullSerializer's took 19-21 s per save). Stream format: VitaSaveIO.cs. Rerun after upstream merges that touch save data.
// Batch (fork): VITA_CODEGEN_OUT=<main repo file, Z: path> vita/unity2017.sh VitaSaveCodegen.Generate savecodegen
// Check: VITA_SAVES=<folder with SAVEn dirs> vita/unity2017.sh VitaSaveCodegen.Verify saveverify: every save file read as JSON (object A),
// written native, read back (B); FullSerializer's JSON of A and B must be the same.

#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Player;
using DaggerfallWorkshop.Utility.AssetInjection;

public static class VitaSaveCodegen
{
    // Save roots and the file each one is in (SaveLoadManager.SaveGame)
    public static readonly KeyValuePair<string, Type>[] Roots =
    {
        new KeyValuePair<string, Type>("SaveData.txt", typeof(SaveData_v1)),
        new KeyValuePair<string, Type>("SaveInfo.txt", typeof(SaveInfo_v1)),
        new KeyValuePair<string, Type>("FactionData.txt", typeof(FactionData_v2)),
        new KeyValuePair<string, Type>("QuestData.txt", typeof(QuestMachine.QuestMachineData_v1)),
        new KeyValuePair<string, Type>("DiscoveryData.txt", typeof(Dictionary<int, PlayerGPS.DiscoveredLocation>)),
        new KeyValuePair<string, Type>("ConversationData.txt", typeof(TalkManager.SaveDataConversation)),
        new KeyValuePair<string, Type>("NotebookData.txt", typeof(PlayerNotebook.NotebookData_v1)),
        new KeyValuePair<string, Type>("WorldVariationData.txt", typeof(WorldDataVariants.WorldVariationData_v1)),
        new KeyValuePair<string, Type>("QuestExceptions.txt", typeof(QuestMachine.StoredException[])),
        new KeyValuePair<string, Type>("AutomapData.txt", typeof(Dictionary<string, Automap.AutomapDungeonState>)),
        // Not save files: ItemHelper's tables from Resources, baked by BakeTables
        new KeyValuePair<string, Type>("ItemTemplates", typeof(List<DaggerfallConnect.FallExe.ItemTemplate>)),
        new KeyValuePair<string, Type>("MagicItemTemplates", typeof(List<DaggerfallConnect.FallExe.MagicItemTemplate>)),
        new KeyValuePair<string, Type>("books", typeof(List<DaggerfallConnect.FallExe.BookMappingTemplate>)),
    };

    // Native tables: the Resources JSON tables ItemHelper parses with FullSerializer at boot
    // -> StreamingAssets/VitaNative/itemtables.bin (VitaSaveIO parts, one per table), each checked: FullSerializer JSON of the table
    // read back from native must equal the JSON of the parsed original. Batch: vita/unity2017.sh VitaSaveCodegen.BakeTables tables
    public static readonly string[] Tables = { "ItemTemplates", "MagicItemTemplates", "books" };

    public static void BakeTables()
    {
        Type codec = typeof(SaveLoadManager).Assembly.GetType("VitaSaveCodec");
        MethodInfo write = codec.GetMethod("Write"), read = codec.GetMethod("Read");
        var parts = new List<KeyValuePair<string, byte[]>>();
        int differ = 0;
        foreach (string name in Tables)
        {
            Type type = Roots.First(r => r.Key == name).Value;
            TextAsset asset = Resources.Load<TextAsset>(name);
            object a = SaveLoadManager.Deserialize(type, asset.text);
            var bytes = (byte[])write.Invoke(null, new[] { type, a });
            object b = read.Invoke(null, new object[] { type, bytes });
            string jsonA = SaveLoadManager.Serialize(type, a, false), jsonB = SaveLoadManager.Serialize(type, b, false);
            if (jsonA != jsonB)
                differ++;
            Debug.LogFormat("VITATABLES {0}: {1} entries, JSON {2} KB -> native {3} KB, round trip {4}", name, ((ICollection)a).Count,
                asset.text.Length / 1024, bytes.Length / 1024, jsonA == jsonB ? "same" : "DIFFERS");
            parts.Add(new KeyValuePair<string, byte[]>(name, bytes));
        }
        if (differ > 0)
            throw new Exception("VITATABLES round trip differs: not written");
        string outPath = "Assets/StreamingAssets/VitaNative/itemtables.bin";
        File.WriteAllBytes(outPath, VitaSaveIO.Pack(parts));
        Debug.LogFormat("VITATABLES wrote {0}", outPath);
    }

    enum Kind { Prim, String, Enum, SystemType, Nullable, Array, List, HashSet, Dictionary, Object, Poly, Json }

    class Member
    {
        public string name, key;
        public Type type;
        public bool isPublic, isProperty, canRead, canWrite;
    }

    class Node
    {
        public int id;
        public Type type;
        public Kind kind;
        public List<Member> members;
        public bool anyPrivate;
    }

    static readonly Dictionary<Type, Node> nodes = new Dictionary<Type, Node>();
    static object fsConfig;
    static MethodInfo fsGet;
    static readonly Type[] prims = { typeof(bool), typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint),
        typeof(long), typeof(ulong), typeof(float), typeof(double), typeof(char), typeof(decimal) };

    public static void Generate()
    {
        Assembly fs = typeof(FullSerializer.fsSerializer).Assembly;
        Type metaType = fs.GetTypes().First(t => t.Name == "fsMetaType");
        Type configType = fs.GetTypes().First(t => t.Name == "fsConfig");
        fsConfig = Activator.CreateInstance(configType);
        fsGet = metaType.GetMethods(BindingFlags.Public | BindingFlags.Static).First(m => m.Name == "Get");
        nodes.Clear();

        foreach (var root in Roots)
            Add(root.Value);
        // Runtime types that can sit in a declared slot: subclasses of every generated class, and the quest system's SaveData_vN structs
        // (stored in object fields; an object slot itself adds nothing else); repeat until nothing new turns up
        Type[] game = typeof(SaveLoadManager).Assembly.GetTypes();
        for (int before = -1; before != nodes.Count;)
        {
            before = nodes.Count;
            foreach (Node n in nodes.Values.ToList())
                if ((n.kind == Kind.Object && !n.type.IsValueType || n.kind == Kind.Poly) && n.type != typeof(object) && !n.type.IsInterface)
                    foreach (Type t in game)
                        if (t != n.type && n.type.IsAssignableFrom(t) && Concrete(t))
                            Add(t);
            foreach (Type t in game)
                if (t.IsNested && System.Text.RegularExpressions.Regex.IsMatch(t.Name, @"^SaveData_v\d+$") && Concrete(t))
                    Add(t);
        }

        string code = Emit();
        string outPath = Environment.GetEnvironmentVariable("VITA_CODEGEN_OUT") ?? "Assets/Scripts/Vita/VitaSaveCodec.cs";
        File.WriteAllText(outPath, code);
        Debug.LogFormat("VITASAVEGEN {0} types ({1} objects, {2} JSON fallbacks: {3}) -> {4}", nodes.Count,
            nodes.Values.Count(n => n.kind == Kind.Object), nodes.Values.Count(n => n.kind == Kind.Json),
            string.Join(", ", nodes.Values.Where(n => n.kind == Kind.Json).Select(n => Name(n.type)).ToArray()), outPath);
    }

    static bool Concrete(Type t)
    {
        return !t.IsAbstract && !t.IsInterface && !t.ContainsGenericParameters && t.IsVisible && !typeof(UnityEngine.Object).IsAssignableFrom(t);
    }

    static Node Add(Type t)
    {
        Node n;
        if (nodes.TryGetValue(t, out n))
            return n;
        n = new Node { id = nodes.Count, type = t };
        nodes.Add(t, n);
        if (prims.Contains(t))
            n.kind = Kind.Prim;
        else if (t == typeof(string))
            n.kind = Kind.String;
        else if (t.IsEnum)
            n.kind = Kind.Enum;
        else if (t == typeof(Type))
            n.kind = Kind.SystemType;
        else if (!t.IsVisible || typeof(UnityEngine.Object).IsAssignableFrom(t) || t.IsPointer || t.IsArray && t.GetArrayRank() != 1)
            n.kind = Kind.Json;
        else if (t.IsArray)
        {
            n.kind = Kind.Array;
            Add(t.GetElementType());
        }
        else if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            n.kind = Kind.Nullable;
            Add(t.GetGenericArguments()[0]);
        }
        else if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(List<>) || t.GetGenericTypeDefinition() == typeof(HashSet<>) ||
            t.GetGenericTypeDefinition() == typeof(Dictionary<,>)))
        {
            Type d = t.GetGenericTypeDefinition();
            n.kind = d == typeof(List<>) ? Kind.List : d == typeof(HashSet<>) ? Kind.HashSet : Kind.Dictionary;
            foreach (Type a in t.GetGenericArguments())
                Add(a);
        }
        else if (t == typeof(object) || t.IsInterface || t.IsAbstract)
            n.kind = Kind.Poly;
        else if (typeof(IEnumerable).IsAssignableFrom(t) || t.IsGenericType)
            n.kind = Kind.Json;     // other collections / generic types: FullSerializer's own converters
        else
        {
            n.kind = Kind.Object;
            n.members = new List<Member>();
            object meta = fsGet.Invoke(null, new[] { fsConfig, t });
            foreach (object p in (Array)meta.GetType().GetProperty("Properties").GetValue(meta, null))
            {
                Type pt = p.GetType();
                var m = new Member
                {
                    name = (string)pt.GetProperty("MemberName").GetValue(p, null),
                    type = (Type)pt.GetProperty("StorageType").GetValue(p, null),
                    canRead = (bool)pt.GetProperty("CanRead").GetValue(p, null),
                    canWrite = (bool)pt.GetProperty("CanWrite").GetValue(p, null),
                };
                MemberInfo info = t.GetMember(m.name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).First();
                m.isProperty = info is PropertyInfo;
                m.isPublic = m.isProperty ? ((PropertyInfo)info).GetGetMethod(false) != null && ((PropertyInfo)info).GetSetMethod(false) != null :
                    ((FieldInfo)info).IsPublic && !((FieldInfo)info).IsInitOnly;
                m.key = m.name + "/" + Name(m.type);
                n.anyPrivate |= !m.isPublic;
                n.members.Add(m);
                Add(m.type);
            }
        }
        return n;
    }

    static string Name(Type t)
    {
        if (!t.IsGenericType)
            return t.FullName ?? t.Name;
        return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Name).ToArray()) + ">";
    }

    // C# spelling of a type
    static string Cs(Type t)
    {
        if (t.IsArray)
            return Cs(t.GetElementType()) + "[]";
        if (t.IsGenericParameter)
            return t.Name;
        string name = t.IsNested ? Cs(t.DeclaringType) + "." + t.Name : "global::" + (t.Namespace != null ? t.Namespace + "." : "") + t.Name;
        if (!t.IsGenericType)
            return name;
        Type[] args = t.GetGenericArguments();
        int own = t.IsNested ? args.Length - t.DeclaringType.GetGenericArguments().Length : args.Length;
        name = name.Split('`')[0];
        return own > 0 ? name + "<" + string.Join(", ", args.Skip(args.Length - own).Select(Cs).ToArray()) + ">" : name;
    }

    static string Emit()
    {
        var sb = new StringBuilder();
        sb.Append("// Generated by Assets/Editor/Vita/VitaSaveCodegen.cs from FullSerializer's metadata: don't edit, regenerate.\n");
        sb.Append("// Native saves; stream format in VitaSaveIO.cs.\n\n");
        sb.Append("#if UNITY_PSP2\nusing System;\nusing System.Collections.Generic;\nusing System.Reflection;\n");
        sb.Append("using DaggerfallWorkshop.Game.Serialization;\n\npublic static class VitaSaveCodec\n{\n");
        sb.Append("    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;\n\n");

        // Entry points for the save roots (by name: Mono's == said false for the same nested-type array Type across assemblies)
        sb.Append("    public static byte[] Write(Type type, object value)\n    {\n        var w = new VitaSaveIO.Writer();\n");
        foreach (var root in Roots)
            sb.AppendFormat("        if (type.FullName == typeof({0}).FullName) {{ W{1}(w, ({0})value); return w.ToArray(); }}\n", Cs(root.Value), nodes[root.Value].id);
        sb.Append("        throw new ArgumentException(\"not a save root: \" + type);\n    }\n\n");
        sb.Append("    public static object Read(Type type, byte[] data)\n    {\n        var r = new VitaSaveIO.Reader(data);\n");
        foreach (var root in Roots)
            sb.AppendFormat("        if (type.FullName == typeof({0}).FullName) return R{1}(r);\n", Cs(root.Value), nodes[root.Value].id);
        sb.Append("        throw new ArgumentException(\"not a save root: \" + type);\n    }\n\n");

        // Any generated type by runtime type (tag 2), else FullSerializer JSON (tag 3)
        List<Node> objects = nodes.Values.Where(n => n.kind == Kind.Object).ToList();
        sb.Append("    static readonly Dictionary<Type, int> anyIds = new Dictionary<Type, int>\n    {\n");
        foreach (Node n in objects)
            sb.AppendFormat("        {{ typeof({0}), {1} }},\n", Cs(n.type), n.id);
        sb.Append("    };\n\n");
        sb.Append("    static void WAny(VitaSaveIO.Writer w, object v)\n    {\n        Type t = v.GetType();\n        int id;\n");
        sb.Append("        if (!anyIds.TryGetValue(t, out id))\n        {\n            w.W.Write((byte)3);\n            w.Str(VitaSaveIO.TypeName(t));\n");
        sb.Append("            w.Str(SaveLoadManager.Serialize(t, v, false));\n            return;\n        }\n");
        sb.Append("        w.W.Write((byte)2);\n        w.Key(VitaSaveIO.TypeName(t));\n        switch (id)\n        {\n");
        foreach (Node n in objects)
            sb.AppendFormat("            case {0}: B{0}(w, ({1})v); break;\n", n.id, Cs(n.type));
        sb.Append("        }\n    }\n\n");
        sb.Append("    static object RAny(VitaSaveIO.Reader r, byte tag)\n    {\n        if (tag == 3)\n        {\n");
        sb.Append("            Type t = VitaSaveIO.FindType(r.Str());\n            string json = r.Str();\n");
        sb.Append("            return t != null ? SaveLoadManager.Deserialize(t, json) : null;\n        }\n        switch (r.Key())\n        {\n");
        foreach (Node n in objects)
            sb.AppendFormat("            case \"{0}\": return C{1}(r);\n", n.type.FullName, n.id);
        sb.Append("        }\n        throw new System.IO.InvalidDataException(\"unknown type in save\");\n    }\n\n");

        foreach (Node n in nodes.Values)
            EmitNode(sb, n);
        sb.Append("}\n#endif\n");
        return sb.ToString();
    }

    static void EmitNode(StringBuilder sb, Node n)
    {
        string T = Cs(n.type), W = "    static void W" + n.id + "(VitaSaveIO.Writer w, " + T + " v)\n    {\n",
            R = "    static " + T + " R" + n.id + "(VitaSaveIO.Reader r)\n    {\n";
        Type[] args = n.type.IsGenericType ? n.type.GetGenericArguments() : null;
        switch (n.kind)
        {
            case Kind.Prim:
                string read = n.type == typeof(bool) ? "Boolean" : n.type == typeof(byte) ? "Byte" : n.type == typeof(sbyte) ? "SByte" :
                    n.type == typeof(short) ? "Int16" : n.type == typeof(ushort) ? "UInt16" : n.type == typeof(int) ? "Int32" :
                    n.type == typeof(uint) ? "UInt32" : n.type == typeof(long) ? "Int64" : n.type == typeof(ulong) ? "UInt64" :
                    n.type == typeof(float) ? "Single" : n.type == typeof(double) ? "Double" : n.type == typeof(decimal) ? "Decimal" : null;
                if (n.type == typeof(char))
                    sb.Append(W + "        w.W.Write((ushort)v);\n    }\n" + R + "        return (char)r.R.ReadUInt16();\n    }\n\n");
                else
                    sb.Append(W + "        w.W.Write(v);\n    }\n" + R + "        return r.R.Read" + read + "();\n    }\n\n");
                break;
            case Kind.String:
                sb.Append(W + "        w.Str(v);\n    }\n" + R + "        return r.Str();\n    }\n\n");
                break;
            case Kind.Enum:
                sb.Append(W + "        w.W.Write(Convert.ToInt64(v));\n    }\n" + R + "        return (" + T + ")Enum.ToObject(typeof(" + T + "), r.R.ReadInt64());\n    }\n\n");
                break;
            case Kind.SystemType:
                sb.Append(W + "        w.Str(VitaSaveIO.TypeName(v));\n    }\n" + R + "        return VitaSaveIO.FindType(r.Str());\n    }\n\n");
                break;
            case Kind.Nullable:
                int inner = nodes[args[0]].id;
                sb.Append(W + "        w.W.Write(v.HasValue);\n        if (v.HasValue)\n            W" + inner + "(w, v.Value);\n    }\n");
                sb.Append(R + "        if (!r.R.ReadBoolean())\n            return null;\n        return R" + inner + "(r);\n    }\n\n");
                break;
            case Kind.Array:
            case Kind.List:
            case Kind.HashSet:
                Type e = n.kind == Kind.Array ? n.type.GetElementType() : args[0];
                int ei = nodes[e].id;
                string count = n.kind == Kind.Array ? "Length" : "Count";
                sb.Append(W + "        if (v == null)\n        {\n            w.W.Write(-1);\n            return;\n        }\n");
                sb.Append("        w.W.Write(v." + count + ");\n        foreach (" + Cs(e) + " x in v)\n            W" + ei + "(w, x);\n    }\n");
                sb.Append(R + "        int n = r.R.ReadInt32();\n        if (n < 0)\n            return null;\n");
                if (n.kind == Kind.Array)
                    sb.Append("        var v = new " + Cs(e).Replace("[]", "") + "[n]" + (e.IsArray ? "[]" : "") + ";\n        for (int i = 0; i < n; i++)\n            v[i] = R" + ei + "(r);\n");
                else
                    sb.Append("        var v = new " + T + "(" + (n.kind == Kind.List ? "n" : "") + ");\n        for (int i = 0; i < n; i++)\n            v.Add(R" + ei + "(r));\n");
                sb.Append("        return v;\n    }\n\n");
                break;
            case Kind.Dictionary:
                int ki = nodes[args[0]].id, vi = nodes[args[1]].id;
                sb.Append(W + "        if (v == null)\n        {\n            w.W.Write(-1);\n            return;\n        }\n");
                sb.Append("        w.W.Write(v.Count);\n        foreach (var x in v)\n        {\n            W" + ki + "(w, x.Key);\n            W" + vi + "(w, x.Value);\n        }\n    }\n");
                sb.Append(R + "        int n = r.R.ReadInt32();\n        if (n < 0)\n            return null;\n        var v = new " + T + "(n);\n");
                sb.Append("        for (int i = 0; i < n; i++)\n        {\n            var k = R" + ki + "(r);\n            v[k] = R" + vi + "(r);\n        }\n        return v;\n    }\n\n");
                break;
            case Kind.Poly:
                sb.Append(W + "        if (v == null)\n        {\n            w.W.Write((byte)0);\n            return;\n        }\n        WAny(w, v);\n    }\n");
                sb.Append(R + "        byte tag = r.R.ReadByte();\n        return tag == 0 ? null : (" + T + ")RAny(r, tag);\n    }\n\n");
                break;
            case Kind.Json:
                sb.Append(W + "        object o = v;\n        if (o == null)\n        {\n            w.W.Write((byte)0);\n            return;\n        }\n");
                sb.Append("        w.W.Write((byte)3);\n        w.Str(VitaSaveIO.TypeName(o.GetType()));\n        w.Str(SaveLoadManager.Serialize(o.GetType(), o, false));\n    }\n");
                sb.Append(R + "        byte tag = r.R.ReadByte();\n        if (tag == 0)\n            return default(" + T + ");\n");
                sb.Append("        return (" + T + ")RAny(r, tag);\n    }\n\n");
                break;
            case Kind.Object:
                EmitObject(sb, n, T, W, R);
                break;
        }
    }

    static void EmitObject(StringBuilder sb, Node n, string T, string W, string R)
    {
        bool isStruct = n.type.IsValueType;
        // Reflection only for members the generated code can't reach (DaggerfallStats/skills private fields and a few more)
        for (int i = 0; i < n.members.Count; i++)
            if (!n.members[i].isPublic || isStruct && n.anyPrivate)
                sb.AppendFormat("    static readonly {0} M{1}_{2} = typeof({3}).Get{4}(\"{5}\", Any);\n", n.members[i].isProperty ? "PropertyInfo" : "FieldInfo",
                    n.id, i, T, n.members[i].isProperty ? "Property" : "Field", n.members[i].name);

        // Value: null / runtime type tag for classes
        if (isStruct)
            sb.Append(W + "        B" + n.id + "(w, v);\n    }\n" + R + "        return C" + n.id + "(r);\n    }\n");
        else
        {
            sb.Append(W + "        if (v == null)\n        {\n            w.W.Write((byte)0);\n            return;\n        }\n");
            if (!n.type.IsSealed)
                sb.Append("        if (v.GetType() != typeof(" + T + "))\n        {\n            WAny(w, v);\n            return;\n        }\n");
            sb.Append("        w.W.Write((byte)1);\n        B" + n.id + "(w, v);\n    }\n");
            sb.Append(R + "        byte tag = r.R.ReadByte();\n        if (tag == 0)\n            return null;\n");
            sb.Append("        if (tag != 1)\n            return (" + T + ")RAny(r, tag);\n        return C" + n.id + "(r);\n    }\n");
        }

        // Body: member count, then each member as key + length + value
        List<int> written = Enumerable.Range(0, n.members.Count).Where(i => n.members[i].canRead).ToList();
        sb.Append("    static void B" + n.id + "(VitaSaveIO.Writer w, " + T + " v)\n    {\n        w.W.Write(" + written.Count + ");\n        long at;\n");
        foreach (int i in written)
        {
            Member m = n.members[i];
            string get = m.isPublic && !(isStruct && n.anyPrivate) ? "v." + m.name : "(" + Cs(m.type) + ")M" + n.id + "_" + i + ".GetValue(v" + (m.isProperty ? ", null" : "") + ")";
            sb.AppendFormat("        at = w.Begin(\"{0}\");\n        W{1}(w, {2});\n        w.End(at);\n", m.key, nodes[m.type].id, get);
        }
        sb.Append("    }\n");

        // Construction as FullSerializer does it (default constructor, else an uninitialized object), then the members present
        sb.Append("    static " + T + " C" + n.id + "(VitaSaveIO.Reader r)\n    {\n");
        if (isStruct)
            sb.Append("        var v = new " + T + "();\n");
        else if (n.type.GetConstructor(Type.EmptyTypes) != null)
            sb.Append("        var v = new " + T + "();\n");
        else if (n.type.GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, Type.EmptyTypes, null) != null)
            sb.Append("        var v = (" + T + ")Activator.CreateInstance(typeof(" + T + "), true);\n");
        else
            sb.Append("        var v = (" + T + ")System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(" + T + "));\n");
        bool boxed = isStruct && n.anyPrivate;
        if (boxed)
            sb.Append("        object box = v;\n");
        sb.Append("        int count = r.R.ReadInt32();\n        for (int i = 0; i < count; i++)\n        {\n");
        sb.Append("            string key = r.Key();\n            int length = r.R.ReadInt32();\n            long end = r.Position + length;\n");
        sb.Append("            switch (key)\n            {\n");
        for (int i = 0; i < n.members.Count; i++)
        {
            Member m = n.members[i];
            if (!m.canWrite)
                continue;
            string value = "R" + nodes[m.type].id + "(r)";
            string set = boxed ? "M" + n.id + "_" + i + ".SetValue(box, " + value + (m.isProperty ? ", null" : "") + ")" :
                m.isPublic ? "v." + m.name + " = " + value : "M" + n.id + "_" + i + ".SetValue(v, " + value + (m.isProperty ? ", null" : "") + ")";
            sb.AppendFormat("                case \"{0}\": {1}; break;\n", m.key, set);
        }
        sb.Append("            }\n            r.Position = end;\n        }\n");
        sb.Append(boxed ? "        return (" + T + ")box;\n    }\n\n" : "        return v;\n    }\n\n");
    }

    // Every save file in VITA_SAVES: JSON -> A -> native -> B, FullSerializer JSON of A and B compared
    public static void Verify()
    {
        string folder = Environment.GetEnvironmentVariable("VITA_SAVES");
        Type codec = typeof(SaveLoadManager).Assembly.GetType("VitaSaveCodec");
        MethodInfo write = codec.GetMethod("Write"), read = codec.GetMethod("Read");
        int same = 0, differ = 0;
        long jsonBytes = 0, nativeBytes = 0;
        var watch = new System.Diagnostics.Stopwatch();
        double fsMs = 0, nativeMs = 0;
        foreach (string dir in Directory.GetDirectories(folder).OrderBy(d => d))
            foreach (var root in Roots)
            {
                string path = Path.Combine(dir, root.Key);
                if (!File.Exists(path))
                    continue;
                string json = File.ReadAllText(path);
                object a = SaveLoadManager.Deserialize(root.Value, json);
                watch.Reset(); watch.Start();
                string jsonA = SaveLoadManager.Serialize(root.Value, a, false);
                fsMs += watch.Elapsed.TotalMilliseconds;
                watch.Reset(); watch.Start();
                var bytes = (byte[])write.Invoke(null, new[] { root.Value, a });
                nativeMs += watch.Elapsed.TotalMilliseconds;
                object b = read.Invoke(null, new object[] { root.Value, bytes });
                string jsonB = SaveLoadManager.Serialize(root.Value, b, false);
                jsonBytes += json.Length;
                nativeBytes += bytes.Length;
                if (jsonA == jsonB)
                    same++;
                else
                {
                    differ++;
                    int at = 0;
                    while (at < jsonA.Length && at < jsonB.Length && jsonA[at] == jsonB[at])
                        at++;
                    Debug.LogFormat("VITASAVEVERIFY differ {0}/{1} at {2}: A ...{3}... B ...{4}...", Path.GetFileName(dir), root.Key, at,
                        jsonA.Substring(Math.Max(0, at - 80), Math.Min(160, jsonA.Length - Math.Max(0, at - 80))),
                        jsonB.Substring(Math.Max(0, at - 80), Math.Min(160, jsonB.Length - Math.Max(0, at - 80))));
                }
            }
        Debug.LogFormat("VITASAVEVERIFY {0} same, {1} differ; JSON {2:F1} MB -> native {3:F1} MB; write: FullSerializer {4:F0} ms, native {5:F0} ms (PC)",
            same, differ, jsonBytes / 1048576.0, nativeBytes / 1048576.0, fsMs, nativeMs);
    }
}
#endif
