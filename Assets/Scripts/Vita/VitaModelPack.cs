// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked ARCH3D models. MeshReader.GetModelData read each model from ARCH3D.BSA on the card,
// parsed its planes and triangulated them on every level load: ~9 s per Palace route (pal10: 2.4 of 3.1 s of a big Palace block's model
// work; merging was 0.4 s). Assets/Editor/Vita/VitaModelBaker.cs runs DFU's own GetModelData on the PC for every ARCH3D model and writes
// the ModelData into StreamingAssets/VitaNative/models.pak (VitaPack, key = model id); GetModelData now reads it from there.
// DFMesh keeps what code outside MeshReader's loaders reads (id, radius, size, centre, counts, submesh textures); Planes stay null.

#if UNITY_PSP2
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;

public static class VitaModelPack
{
    public const int Magic = 0x4C444D56;   // "VMDL"
    public static bool Disabled;           // the baker reads ARCH3D itself

    static VitaPack pack;

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "models.pak"); }
    }

    #region Write (baker)

    public static byte[] Serialize(ref ModelData m)
    {
        var memory = new MemoryStream();
        var w = new BinaryWriter(memory);
        w.Write(m.Vertices.Length);
        foreach (Vector3 v in m.Vertices) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        foreach (Vector3 v in m.Normals) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        foreach (Vector2 v in m.UVs) { w.Write(v.x); w.Write(v.y); }
        w.Write(m.Indices.Length);
        foreach (int i in m.Indices) w.Write(i);
        w.Write(m.SubMeshes.Length);
        foreach (ModelData.SubMeshData s in m.SubMeshes) { w.Write(s.StartIndex); w.Write(s.PrimitiveCount); w.Write(s.TextureArchive); w.Write(s.TextureRecord); }
        w.Write(m.Doors.Length);
        foreach (ModelDoor d in m.Doors)
        {
            w.Write(d.Index); w.Write((int)d.Type);
            foreach (Vector3 v in new[] { d.Vert0, d.Vert1, d.Vert2, d.Normal }) { w.Write(v.x); w.Write(v.y); w.Write(v.z); }
        }
        DFMesh f = m.DFMesh;
        w.Write(f.ObjectId); w.Write(f.Radius); Point(w, f.Size); Point(w, f.Centre); w.Write(f.TotalVertices); w.Write(f.TotalTriangles);
        w.Write(f.SubMeshes.Length);
        foreach (DFMesh.DFSubMesh s in f.SubMeshes) { w.Write(s.TextureArchive); w.Write(s.TextureRecord); w.Write(s.TotalTriangles); }
        w.Write(Magic);   // end mark
        return memory.ToArray();
    }

    static void Point(BinaryWriter w, DFMesh.DFPoint p)
    {
        foreach (float v in new[] { p.X, p.Y, p.Z, p.NX, p.NY, p.NZ, p.U, p.V })
            w.Write(v);
    }

    #endregion

    #region Read (runtime)

    /// <summary>The baked ModelData for id. False when models.pak has no such record (MeshReader then reads ARCH3D as upstream).</summary>
    public static bool TryGetModelData(uint id, out ModelData modelData)
    {
        modelData = new ModelData();
        if (Disabled)
            return false;
        if (pack == null)
            pack = new VitaPack(PackPath, Magic);
        if (!pack.Contains(id))
        {
            if (pack.Count > 0)
                Debug.LogFormat("VITANATIVE miss model {0}", id);
            return false;
        }
        System.IntPtr memory = System.IntPtr.Zero, data;
        int length;
        try
        {
            return pack.Read(id, out memory, out data, out length) && Parse(data, length, ref modelData);
        }
        finally
        {
            if (memory != System.IntPtr.Zero)
                Marshal.FreeHGlobal(memory);
        }
    }

    // Every count is checked against the record's length before anything is copied: a damaged record fails, it never reads past the end
    static unsafe bool Parse(System.IntPtr data, int length, ref ModelData m)
    {
        byte* p = (byte*)data, end = p + length;
        int n = Count(ref p, end, 32);
        if (n < 0)
            return false;
        m.Vertices = new Vector3[n];
        m.Normals = new Vector3[n];
        m.UVs = new Vector2[n];
        fixed (Vector3* v = m.Vertices) Copy(ref p, v, n * 12);
        fixed (Vector3* v = m.Normals) Copy(ref p, v, n * 12);
        fixed (Vector2* v = m.UVs) Copy(ref p, v, n * 8);
        if ((n = Count(ref p, end, 4)) < 0)
            return false;
        m.Indices = new int[n];
        fixed (int* v = m.Indices) Copy(ref p, v, n * 4);
        if ((n = Count(ref p, end, 16)) < 0)
            return false;
        m.SubMeshes = new ModelData.SubMeshData[n];
        for (int i = 0; i < n; i++)
        {
            m.SubMeshes[i].StartIndex = Int(ref p);
            m.SubMeshes[i].PrimitiveCount = Int(ref p);
            m.SubMeshes[i].TextureArchive = Int(ref p);
            m.SubMeshes[i].TextureRecord = Int(ref p);
        }
        if ((n = Count(ref p, end, 56)) < 0)
            return false;
        m.Doors = new ModelDoor[n];
        for (int i = 0; i < n; i++)
        {
            m.Doors[i].Index = Int(ref p);
            m.Doors[i].Type = (DoorTypes)Int(ref p);
            m.Doors[i].Vert0 = Vec3(ref p);
            m.Doors[i].Vert1 = Vec3(ref p);
            m.Doors[i].Vert2 = Vec3(ref p);
            m.Doors[i].Normal = Vec3(ref p);
        }
        if (end - p < 84)
            return false;
        m.DFMesh.ObjectId = Int(ref p);
        m.DFMesh.Radius = Float(ref p);
        m.DFMesh.Size = Point(ref p);
        m.DFMesh.Centre = Point(ref p);
        m.DFMesh.TotalVertices = Int(ref p);
        m.DFMesh.TotalTriangles = Int(ref p);
        if ((n = Count(ref p, end, 12)) < 0)
            return false;
        m.DFMesh.SubMeshes = new DFMesh.DFSubMesh[n];
        for (int i = 0; i < n; i++)
        {
            m.DFMesh.SubMeshes[i].TextureArchive = Int(ref p);
            m.DFMesh.SubMeshes[i].TextureRecord = Int(ref p);
            m.DFMesh.SubMeshes[i].TotalTriangles = Int(ref p);
        }
        return end - p == 4 && Int(ref p) == Magic;
    }

    // A count followed by count * size bytes (plus the 4-byte end mark at least), else -1
    static unsafe int Count(ref byte* p, byte* end, int size)
    {
        if (end - p < 4)
            return -1;
        int n = Int(ref p);
        return n >= 0 && (long)n * size <= end - p - 4 ? n : -1;
    }

    static unsafe void Copy(ref byte* p, void* to, int bytes)
    {
        System.Buffer.MemoryCopy(p, to, bytes, bytes);
        p += bytes;
    }

    static unsafe int Int(ref byte* p)
    {
        int v = *(int*)p;   // records are written in 4-byte steps from a 64-byte-aligned start: aligned reads
        p += 4;
        return v;
    }

    static unsafe float Float(ref byte* p)
    {
        float v = *(float*)p;
        p += 4;
        return v;
    }

    static unsafe Vector3 Vec3(ref byte* p)
    {
        return new Vector3(Float(ref p), Float(ref p), Float(ref p));
    }

    static unsafe DFMesh.DFPoint Point(ref byte* p)
    {
        DFMesh.DFPoint v = new DFMesh.DFPoint();
        v.X = Float(ref p); v.Y = Float(ref p); v.Z = Float(ref p);
        v.NX = Float(ref p); v.NY = Float(ref p); v.NZ = Float(ref p);
        v.U = Float(ref p); v.V = Float(ref p);
        return v;
    }

    #endregion
}
#endif
