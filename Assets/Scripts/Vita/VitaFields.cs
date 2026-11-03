// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Field walk shared by the baked records that store a whole DFU object as DFU's own code built it (VitaCareers, VitaMaps): every
// instance field, public and private, sorted by name, recursively. Primitives and enums as fixed-size values, strings and arrays with a
// null flag, structs and classes field by field. A changed DFU type changes the layout: rebake.
#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

public static class VitaFields
{
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    static readonly Dictionary<Type, FieldInfo[]> fieldCache = new Dictionary<Type, FieldInfo[]>();

    // Every instance field (public and private, by name), recursively: primitives, enums, strings, arrays, structs and classes
    static FieldInfo[] FieldsOf(Type type)
    {
        FieldInfo[] fields;
        if (!fieldCache.TryGetValue(type, out fields))
        {
            fields = type.GetFields(Fields);
            Array.Sort(fields, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            fieldCache[type] = fields;
        }
        return fields;
    }

    public static void Write(BinaryWriter writer, Type type, object value)
    {
        if (type.IsEnum)
            writer.Write(Convert.ToInt64(value));
        else if (type == typeof(bool)) writer.Write((bool)value);
        else if (type == typeof(byte)) writer.Write((byte)value);
        else if (type == typeof(sbyte)) writer.Write((sbyte)value);
        else if (type == typeof(short)) writer.Write((short)value);
        else if (type == typeof(ushort)) writer.Write((ushort)value);
        else if (type == typeof(int)) writer.Write((int)value);
        else if (type == typeof(uint)) writer.Write((uint)value);
        else if (type == typeof(long)) writer.Write((long)value);
        else if (type == typeof(ulong)) writer.Write((ulong)value);
        else if (type == typeof(float)) writer.Write((float)value);
        else if (type == typeof(double)) writer.Write((double)value);
        else if (type == typeof(char)) writer.Write((char)value);
        else
        {
            if (!type.IsValueType)
            {
                writer.Write(value != null);
                if (value == null)
                    return;
            }
            if (type == typeof(string))
                writer.Write((string)value);
            else if (type.IsArray)
            {
                var array = (Array)value;
                if (array.Rank > 1)
                {
                    // Multi-dimensional (DFBlock's RmbGroundScenery[,]): each length, then the elements in row-major order
                    for (int d = 0; d < array.Rank; d++)
                        writer.Write(array.GetLength(d));
                    foreach (object element in array)
                        Write(writer, type.GetElementType(), element);
                    return;
                }
                writer.Write(array.Length);
                if (type == typeof(byte[]))
                {
                    writer.Write((byte[])value);    // same bytes as the element loop, one call
                    return;
                }
                foreach (object element in array)
                    Write(writer, type.GetElementType(), element);
            }
            else
                foreach (FieldInfo field in FieldsOf(type))
                    Write(writer, field.FieldType, field.GetValue(value));
        }
    }

    /// <summary>One stored value: the generated reader (VitaFieldsGen.cs, same layout) where there is one, else the field walk.
    /// The walk took 1.9 s per DFBlock on the Vita and boxed every field; Reflect forces it.</summary>
    public static object Read(BinaryReader reader, Type type)
    {
        Func<BinaryReader, object> generated = Reflect ? null : VitaFieldsGen.For(type);
        return generated != null ? generated(reader) : ReadReflected(reader, type);
    }

    public static bool Reflect;

    public static object ReadReflected(BinaryReader reader, Type type)
    {
        if (type.IsEnum)
            return Enum.ToObject(type, reader.ReadInt64());
        if (type == typeof(bool)) return reader.ReadBoolean();
        if (type == typeof(byte)) return reader.ReadByte();
        if (type == typeof(sbyte)) return reader.ReadSByte();
        if (type == typeof(short)) return reader.ReadInt16();
        if (type == typeof(ushort)) return reader.ReadUInt16();
        if (type == typeof(int)) return reader.ReadInt32();
        if (type == typeof(uint)) return reader.ReadUInt32();
        if (type == typeof(long)) return reader.ReadInt64();
        if (type == typeof(ulong)) return reader.ReadUInt64();
        if (type == typeof(float)) return reader.ReadSingle();
        if (type == typeof(double)) return reader.ReadDouble();
        if (type == typeof(char)) return reader.ReadChar();
        if (!type.IsValueType && !reader.ReadBoolean())
            return null;
        if (type == typeof(string))
            return reader.ReadString();
        if (type == typeof(byte[]))
            return reader.ReadBytes(reader.ReadInt32());
        if (type.IsArray && type.GetArrayRank() > 1)
        {
            var lengths = new int[type.GetArrayRank()];
            for (int d = 0; d < lengths.Length; d++)
                lengths[d] = reader.ReadInt32();
            Array grid = Array.CreateInstance(type.GetElementType(), lengths);
            var index = new int[lengths.Length];
            for (int i = 0; i < grid.Length; i++)
            {
                for (int d = lengths.Length - 1, rest = i; d >= 0; d--)
                {
                    index[d] = rest % lengths[d];
                    rest /= lengths[d];
                }
                grid.SetValue(ReadReflected(reader, type.GetElementType()), index);
            }
            return grid;
        }
        if (type.IsArray)
        {
            Array array = Array.CreateInstance(type.GetElementType(), reader.ReadInt32());
            for (int i = 0; i < array.Length; i++)
                array.SetValue(ReadReflected(reader, type.GetElementType()), i);
            return array;
        }
        object value = Activator.CreateInstance(type, true);    // boxed for structs: fields are set on the box
        foreach (FieldInfo field in FieldsOf(type))
            field.SetValue(value, ReadReflected(reader, field.FieldType));
        return value;
    }
}
#endif
