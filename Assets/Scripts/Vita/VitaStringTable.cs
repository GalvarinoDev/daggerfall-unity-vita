// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// An English string table without objects per entry. A Dictionary<string, string> holds two strings per row, and every garbage
// collection marks each of them: the loaded tables were ~37k of the city's ~161k live objects.
// Here the keys and values of the baked rows live in one char[] and the index in int[]s (pointer-free: the GC never scans them).
// A hit makes the value string. Add() (runtime tables such as Internal_Quests) and the CSV fallback go to an ordinary dictionary.
// Same answers as the dictionary it replaces.
#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.Text;

public class VitaStringTable
{
    char[] chars;
    int[] hashes;                       // sorted
    int[] order;                        // row of each sorted hash
    int[] keyStart, keyLength, valueStart, valueLength;     // valueLength -1 = null value
    Dictionary<string, string> added;

    public VitaStringTable()
    {
    }

    public VitaStringTable(Dictionary<string, string> rows)
    {
        added = rows;
    }

    public int Count
    {
        get { return (hashes != null ? hashes.Length : 0) + (added != null ? added.Count : 0); }
    }

    /// <summary>
    /// Rows as VitaTextTables writes them from offset: per row a BinaryWriter string (name), a bool (has value) and the value string.
    /// Null if the bytes end early or a length is bad. Later duplicates win, as with table[name] = value.
    /// </summary>
    public static VitaStringTable FromRows(byte[] bytes, int offset, int count)
    {
        if (count < 0)
            return null;
        var t = new VitaStringTable();
        t.chars = new char[Math.Max(0, bytes.Length - offset)];    // UTF-8 never decodes to more chars than bytes
        t.keyStart = new int[count];
        t.keyLength = new int[count];
        t.valueStart = new int[count];
        t.valueLength = new int[count];
        UTF8Encoding utf8 = new UTF8Encoding();     // same decoding as BinaryReader.ReadString (invalid bytes -> U+FFFD)
        int p = offset, used = 0;
        for (int i = 0; i < count; i++)
        {
            if (!ReadString(bytes, ref p, utf8, t.chars, ref used, out t.keyStart[i], out t.keyLength[i]) || p >= bytes.Length)
                return null;
            if (bytes[p++] != 0)
            {
                if (!ReadString(bytes, ref p, utf8, t.chars, ref used, out t.valueStart[i], out t.valueLength[i]))
                    return null;
            }
            else
                t.valueLength[i] = -1;
        }
        t.hashes = new int[count];
        t.order = new int[count];
        for (int i = 0; i < count; i++)
        {
            t.hashes[i] = Hash(t.chars, t.keyStart[i], t.keyLength[i]);
            t.order[i] = i;
        }
        Array.Sort(t.hashes, t.order);
        return t;
    }

    static bool ReadString(byte[] bytes, ref int p, UTF8Encoding utf8, char[] chars, ref int used, out int start, out int length)
    {
        start = used;
        length = 0;
        int byteCount = 0, shift = 0;
        while (true)
        {
            if (p >= bytes.Length || shift > 28)
                return false;
            byte b = bytes[p++];
            byteCount |= (b & 0x7f) << shift;
            if ((b & 0x80) == 0)
                break;
            shift += 7;
        }
        if (byteCount < 0 || byteCount > bytes.Length - p)
            return false;
        length = utf8.GetChars(bytes, p, byteCount, chars, used);
        used += length;
        p += byteCount;
        return true;
    }

    static int Hash(char[] c, int start, int length)
    {
        uint h = 2166136261;
        for (int i = start; i < start + length; i++)
            h = (h ^ c[i]) * 16777619;
        return (int)h;
    }

    static int Hash(string s)
    {
        uint h = 2166136261;
        for (int i = 0; i < s.Length; i++)
            h = (h ^ s[i]) * 16777619;
        return (int)h;
    }

    // Index of the row whose key is key (the last one if a key repeats), or -1
    int Find(string key)
    {
        if (hashes == null)
            return -1;
        int h = Hash(key);
        int i = Array.BinarySearch(hashes, h);
        if (i < 0)
            return -1;
        while (i > 0 && hashes[i - 1] == h)
            i--;
        int found = -1;
        for (; i < hashes.Length && hashes[i] == h; i++)
        {
            int row = order[i];
            if (keyLength[row] != key.Length || row < found)
                continue;
            int s = keyStart[row], k = 0;
            while (k < key.Length && chars[s + k] == key[k])
                k++;
            if (k == key.Length)
                found = row;
        }
        return found;
    }

    public bool TryGetValue(string key, out string value)
    {
        if (key == null)
            throw new ArgumentNullException("key");
        if (added != null && added.TryGetValue(key, out value))
            return true;
        int row = Find(key);
        if (row < 0)
        {
            value = null;
            return false;
        }
        value = valueLength[row] < 0 ? null : new string(chars, valueStart[row], valueLength[row]);
        return true;
    }

    public bool ContainsKey(string key)
    {
        if (key == null)
            throw new ArgumentNullException("key");
        return (added != null && added.ContainsKey(key)) || Find(key) >= 0;
    }

    public void Add(string key, string value)
    {
        if (ContainsKey(key))
            throw new ArgumentException("An element with the same key already exists: " + key);
        if (added == null)
            added = new Dictionary<string, string>();
        added.Add(key, value);
    }
}
#endif
