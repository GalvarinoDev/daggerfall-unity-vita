// Emits Assets/Scripts/Vita/VitaFieldsGen.cs: plain C# readers for the types VitaFields stores (same layout as VitaFields.Write: instance
// fields public + private sorted by ordinal name, enums as Int64, null flag before reference types, arrays length-prefixed, rank>1 arrays
// one length per dimension). VitaFields' reflection reader took 1.9 s per DFBlock on the Vita and boxed every field.
// Types with non-public or readonly fields keep the reflection path. Run: vita/tools/fieldgen/fieldgen.sh
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

static class FieldGen
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Dictionary<Type, string> prim = new Dictionary<Type, string> {
        {typeof(bool),"ReadBoolean"},{typeof(byte),"ReadByte"},{typeof(sbyte),"ReadSByte"},{typeof(short),"ReadInt16"},{typeof(ushort),"ReadUInt16"},
        {typeof(int),"ReadInt32"},{typeof(uint),"ReadUInt32"},{typeof(long),"ReadInt64"},{typeof(ulong),"ReadUInt64"},{typeof(float),"ReadSingle"},
        {typeof(double),"ReadDouble"},{typeof(char),"ReadChar"} };
    static readonly List<Type> todo = new List<Type>();
    static readonly HashSet<Type> done = new HashSet<Type>();
    static readonly StringBuilder body = new StringBuilder();

    static string Name(Type t) { return t.IsGenericType ? t.Namespace + "." + t.Name.Split('`')[0] + "<" + string.Join(", ", t.GetGenericArguments().Select(Name)) + ">" : t.FullName.Replace('+', '.'); }
    static string Fn(Type t) { return "R_" + System.Text.RegularExpressions.Regex.Replace(t.IsGenericType ? t.Name.Split('`')[0] + string.Join("_", t.GetGenericArguments().Select(a => a.Name)) : t.FullName, "[^A-Za-z0-9]", "_"); }
    static FieldInfo[] FieldsOf(Type t)
    {
        var f = t.GetFields(Fields);
        Array.Sort(f, (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return f;
    }
    static bool Plain(Type t)   // every field settable from generated code
    {
        // internal is fine: the generated file is compiled into Assembly-CSharp too
        return (t.IsPublic || t.IsNestedPublic || t.IsNestedAssembly) && !t.IsGenericType &&
            FieldsOf(t).All(f => (f.IsPublic || f.IsAssembly || f.IsFamilyOrAssembly) && !f.IsInitOnly) &&
            (t.IsValueType || t.GetConstructor(Type.EmptyTypes) != null);
    }

    // An expression reading one value of type t
    static string Expr(Type t, string ind)
    {
        if (t.IsEnum) return "(" + Name(t) + ")r.ReadInt64()";
        string p;
        if (prim.TryGetValue(t, out p)) return "r." + p + "()";
        if (t == typeof(string)) return "(r.ReadBoolean() ? r.ReadString() : null)";
        if (t == typeof(byte[])) return "(r.ReadBoolean() ? r.ReadBytes(r.ReadInt32()) : null)";
        if (!done.Contains(t)) { done.Add(t); todo.Add(t); }
        return Fn(t) + "(r)";
    }

    static void Emit(Type t)
    {
        string n = Name(t);
        body.AppendFormat("    static {0} {1}(BinaryReader r)\n    {{\n", n, Fn(t));
        if (t.IsArray)
        {
            Type e = t.GetElementType();
            int rank = t.GetArrayRank();
            body.Append("        if (!r.ReadBoolean()) return null;\n");
            if (rank == 1)
            {
                body.AppendFormat("        var a = {0};\n        for (int i = 0; i < a.Length; i++) a[i] = {1};\n        return a;\n", NewArray(e, "r.ReadInt32()"), Expr(e, ""));
            }
            else
            {
                var dims = Enumerable.Range(0, rank).Select(d => "n" + d).ToArray();
                foreach (var d in dims) body.AppendFormat("        int {0} = r.ReadInt32();\n", d);
                body.AppendFormat("        var a = {0};\n", NewArray(e, string.Join(", ", dims)));
                for (int d = 0; d < rank; d++) body.AppendFormat("        {0}for (int i{1} = 0; i{1} < n{1}; i{1}++)\n", new string(' ', 4 * d), d);
                body.AppendFormat("        {0}a[{1}] = {2};\n        return a;\n", new string(' ', 4 * rank), string.Join(", ", Enumerable.Range(0, rank).Select(d => "i" + d)), Expr(e, ""));
            }
        }
        else if (!Plain(t))
        {
            // reflection path (e.g. Dictionary lookups, stored only as null); listed so a new one shows up in the log
            Console.WriteLine("fieldgen: reflected {0}", t.FullName.Split('[')[0]);
            body.AppendFormat("        return ({0})VitaFields.ReadReflected(r, typeof({0}));\n", Name(t));
        }
        else
        {
            if (!t.IsValueType) body.Append("        if (!r.ReadBoolean()) return null;\n");
            body.AppendFormat("        var v = new {0}();\n", n);
            foreach (var f in FieldsOf(t)) body.AppendFormat("        v.{0} = {1};\n", f.Name, Expr(f.FieldType, ""));
            body.Append("        return v;\n");
        }
        body.Append("    }\n\n");
    }

    // "new T[dims]" for element type e (jagged: T[] elements -> new T[n][])
    static string NewArray(Type e, string dims)
    {
        string suffix = "";
        while (e.IsArray) { suffix += "[" + new string(',', e.GetArrayRank() - 1) + "]"; e = e.GetElementType(); }
        return "new " + Name(e) + "[" + dims + "]" + suffix;
    }

    static int Main(string[] args)
    {
        var asm = Assembly.LoadFrom(args[0]);
        var roots = args.Skip(2).Select(n => asm.GetType(n, true)).ToArray();
        foreach (var t in roots) { done.Add(t); todo.Add(t); }
        for (int i = 0; i < todo.Count; i++) Emit(todo[i]);
        var sb = new StringBuilder();
        sb.Append("// Generated by vita/tools/fieldgen/fieldgen.sh from Assembly-CSharp: do not edit. Readers in VitaFields' layout (see FieldGen.cs).\n");
        sb.Append("// A changed DFU type: regenerate (the bakers' round-trip checks then pass again) and rebake.\n");
        sb.Append("#if UNITY_PSP2\nusing System;\nusing System.IO;\n\npublic static class VitaFieldsGen\n{\n");
        sb.Append("    /// <summary>The generated reader for type, or null if there is none.</summary>\n");
        sb.Append("    public static Func<BinaryReader, object> For(Type type)\n    {\n");
        foreach (var t in roots) sb.AppendFormat("        if (type == typeof({0})) return r => {1}(r);\n", Name(t), Fn(t));
        sb.Append("        return null;\n    }\n\n");
        sb.Append(body);
        sb.Append("}\n#endif\n");
        System.IO.File.WriteAllText(args[1], sb.ToString());
        Console.WriteLine("fieldgen: {0} readers ({1} reflected) -> {2}", todo.Count, todo.Count(t => !t.IsArray && !Plain(t)), args[1]);
        return 0;
    }
}
