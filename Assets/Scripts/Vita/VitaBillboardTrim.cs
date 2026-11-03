// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// The wilderness is fragment bound and nature billboards are alpha-tested quads: every pixel a quad
// covers runs the discard pass, transparent or not. Tree sprites are ~42% opaque per rect, and an octagon around each record's opaque
// pixels covers ~87% of it. So DaggerfallBillboardBatch's quads become octagons: the batch shader places corners from the tangent.zw
// fractions, and the octagon's fractions and UVs lie on the same quad, so the visible pixels stay the same.
// Left as quads: custom-material batches, batches with animated billboards (AnimateUVJob rewrites 4 UVs per quad) and batches over
// 8191 billboards (8 vertices each, 16-bit indices).

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Unity.Mathematics;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;

namespace DaggerfallWorkshop
{
    public static class VitaBillboardTrim
    {
        public const int MaxBillboards = 65536 / corners - 1;
        // batches take it on their next Apply()
        public static bool Off = true;
        const int corners = 8;
        // Source pixels kept around the opaque ones: a VitaPalette mip texel is opaque only where most of its 2x2 block is, so mip L reaches
        // at most 2^L - 1 pixels past them; at level 2 and up, a texel is about a screen pixel and the cut part less than one.
        const int margin = 2;
        static readonly Vector2[] quad = { new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 1), new Vector2(1, 0),
                                           new Vector2(1, 0), new Vector2(0, 0), new Vector2(0, 0), new Vector2(0, 1) };
        static readonly Dictionary<int, Vector2[][]> hulls = new Dictionary<int, Vector2[][]>();   // archive -> record -> 8 (u, v) fractions of its rect
        static Vector3[] vertices, normals;     // shared scratch, main thread only
        static Vector4[] tangents;
        static Vector2[] uvs;
        static int[] triangles;

        /// <summary>Sets mesh to octagons for the batch's quads (4 vertices per billboard, records[i] = billboard i's record). False = not done.</summary>
        public static bool Set(Mesh mesh, int archive, int[] records, NativeArray<float3> vertex, NativeArray<float4> tangent, NativeArray<float2> uv)
        {
            int count = records.Length;
            if (Off || count == 0 || count > MaxBillboards || vertex.Length != count * 4)
                return false;
            Vector2[][] byRecord = Hulls(archive);
            Resize(count * corners, count * (corners - 2) * 3);
            Vector3 normal = new Vector3(0, 0.707106781187f, 0.707106781187f);
            for (int i = 0, o = 0; i < count; i++, o += corners)
            {
                int q = i * 4;
                float4 t = tangent[q];
                float2 min = uv[q + 2], max = uv[q + 1];
                Vector2[] hull = records[i] >= 0 && records[i] < byRecord.Length && byRecord[records[i]] != null ? byRecord[records[i]] : quad;
                for (int c = 0; c < corners; c++)
                {
                    Vector2 f = hull[c];
                    vertices[o + c] = vertex[q];
                    normals[o + c] = normal;
                    tangents[o + c] = new Vector4(t.x, t.y, f.x, f.y);
                    uvs[o + c] = new Vector2(min.x + f.x * (max.x - min.x), min.y + f.y * (max.y - min.y));
                }
                int tri = i * (corners - 2) * 3;
                for (int c = 1; c < corners - 1; c++, tri += 3)
                {
                    triangles[tri] = o;
                    triangles[tri + 1] = o + c;
                    triangles[tri + 2] = o + c + 1;
                }
            }
            mesh.vertices = vertices;
            mesh.normals = normals;
            mesh.tangents = tangents;
            mesh.uv = uvs;
            mesh.SetTriangles(triangles, 0, false);
            return true;
        }

        static void Resize(int vertexCount, int indexCount)
        {
            if (vertices == null || vertices.Length != vertexCount)
            {
                vertices = new Vector3[vertexCount];
                normals = new Vector3[vertexCount];
                tangents = new Vector4[vertexCount];
                uvs = new Vector2[vertexCount];
            }
            if (triangles == null || triangles.Length != indexCount)
                triangles = new int[indexCount];
        }

        // Per record: the octagon of its frame 0's opaque pixels (bounding box cut by the 45-degree lines touching them), null = keep the quad
        static Vector2[][] Hulls(int archive)
        {
            Vector2[][] byRecord;
            if (hulls.TryGetValue(archive, out byRecord))
                return byRecord;
            var textureFile = new TextureFile(Path.Combine(DaggerfallUnity.Instance.Arena2Path, TextureFile.IndexToFileName(archive)), FileUsage.UseMemory, true);
            byRecord = new Vector2[textureFile.RecordCount][];
            for (int r = 0; r < byRecord.Length; r++)
                byRecord[r] = Octagon(textureFile.GetDFBitmap(r, 0));
            hulls.Add(archive, byRecord);
            return byRecord;
        }

        // In pixel edges, x right and y down from the top row (DFBitmap order); returned as (u, v) with v up
        static Vector2[] Octagon(DFBitmap bitmap)
        {
            int w = bitmap.Width, h = bitmap.Height;
            if (bitmap.Data == null || w == 0 || h == 0)
                return null;
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            int minS = int.MaxValue, maxS = int.MinValue, minD = int.MaxValue, maxD = int.MinValue;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (bitmap.Data[y * w + x] == 0)     // alpha index of the billboard atlases
                        continue;
                    minX = Mathf.Min(minX, x); maxX = Mathf.Max(maxX, x + 1);
                    minY = Mathf.Min(minY, y); maxY = Mathf.Max(maxY, y + 1);
                    minS = Mathf.Min(minS, x + y); maxS = Mathf.Max(maxS, x + y + 2);
                    minD = Mathf.Min(minD, x - y - 1); maxD = Mathf.Max(maxD, x - y + 1);
                }
            }
            if (minX == int.MaxValue)
                return null;
            float x0 = Mathf.Max(0, minX - margin), x1 = Mathf.Min(w, maxX + margin), y0 = Mathf.Max(0, minY - margin), y1 = Mathf.Min(h, maxY + margin);
            float s0 = minS - 2 * margin, s1 = maxS + 2 * margin, d0 = minD - 2 * margin, d1 = maxD + 2 * margin;
            // Clockwise from the top edge's left end
            var points = new Vector2[]
            {
                new Vector2(Mathf.Max(x0, s0 - y0), y0), new Vector2(Mathf.Min(x1, d1 + y0), y0),
                new Vector2(x1, Mathf.Max(y0, x1 - d1)), new Vector2(x1, Mathf.Min(y1, s1 - x1)),
                new Vector2(Mathf.Min(x1, s1 - y1), y1), new Vector2(Mathf.Max(x0, d0 + y1), y1),
                new Vector2(x0, Mathf.Min(y1, x0 - d0)), new Vector2(x0, Mathf.Max(y0, s0 - x0)),
            };
            for (int c = 0; c < corners; c += 2)
            {
                Vector2 a = points[c], b = points[c + 1];
                if ((c == 0 && b.x < a.x) || (c == 2 && b.y < a.y) || (c == 4 && b.x > a.x) || (c == 6 && b.y > a.y))
                    return null;
            }
            for (int c = 0; c < corners; c++)
                points[c] = new Vector2(points[c].x / w, 1f - points[c].y / h);
            return points;
        }
    }
}
#endif
