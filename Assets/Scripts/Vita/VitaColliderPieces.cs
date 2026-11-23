// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// A block built in play cooks its MeshCollider on one frame, and the cook grows with the mesh: a 5,500-vertex town block ~30 ms, a
// 20,842-vertex World of Daggerfall dock ~110-130 ms. Take() cuts each deferred collider mesh into pieces of up to N triangles and Cook()
// cooks them a few per frame, each piece a MeshCollider of its own on the same GameObject, so code that reads the hit collider's object
// (renderer, GetComponent<MeshCollider>) sees what it did before. Collision is the same triangles. The piece meshes are owned here and
// destroyed with the block.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;

public class VitaColliderPieces : MonoBehaviour
{
    public struct Piece
    {
        public GameObject Owner;
        public Mesh Mesh;
        public int Triangles;
    }

    readonly List<Mesh> meshes = new List<Mesh>();

    // Moves the deferred colliders (GameObjectHelper.VitaDeferColliders) into 'pieces': meshes up to maxTriangles as they are, bigger
    // ones cut into consecutive triangle runs (a combined block lists each model's triangles together, so a run stays local)
    public static void Take(List<GameObject> deferred, List<Piece> pieces, int maxTriangles)
    {
        foreach (GameObject go in deferred)
        {
            if (!go)
                continue;
            Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
            int[] tris = mesh.triangles;
            if (tris.Length / 3 <= maxTriangles)
            {
                pieces.Add(new Piece { Owner = go, Mesh = mesh, Triangles = tris.Length / 3 });
                continue;
            }
            VitaColliderPieces owner = go.GetComponent<VitaColliderPieces>();
            if (!owner)
                owner = go.AddComponent<VitaColliderPieces>();
            Vector3[] vertices = mesh.vertices;
            int[] map = new int[vertices.Length];
            List<Vector3> pieceVertices = new List<Vector3>();
            List<int> pieceTris = new List<int>();
            for (int start = 0; start < tris.Length; start += maxTriangles * 3)
            {
                int end = Mathf.Min(tris.Length, start + maxTriangles * 3);
                for (int i = 0; i < map.Length; i++)
                    map[i] = -1;
                pieceVertices.Clear();
                pieceTris.Clear();
                for (int i = start; i < end; i++)
                {
                    int v = tris[i];
                    if (map[v] < 0)
                    {
                        map[v] = pieceVertices.Count;
                        pieceVertices.Add(vertices[v]);
                    }
                    pieceTris.Add(map[v]);
                }
                Mesh piece = new Mesh();
                piece.name = mesh.name + " collider";
                piece.SetVertices(pieceVertices);
                piece.SetTriangles(pieceTris, 0);
                owner.meshes.Add(piece);
                pieces.Add(new Piece { Owner = go, Mesh = piece, Triangles = (end - start) / 3 });
            }
        }
        deferred.Clear();
    }

    // Cooks pieces from index 'next' until maxTriangles are cooked (at least one); returns the index of the first piece left
    public static int Cook(List<Piece> pieces, int next, int maxTriangles, MeshColliderCookingOptions options)
    {
        int cooked = 0;
        while (next < pieces.Count && (cooked == 0 || cooked + pieces[next].Triangles <= maxTriangles))
        {
            Piece p = pieces[next++];
            cooked += p.Triangles;
            if (!p.Owner)
                continue;
            // A MeshCollider added next to a MeshFilter takes (and cooks) its mesh: options first, then the mesh
            MeshFilter meshFilter = p.Owner.GetComponent<MeshFilter>();
            Mesh shown = meshFilter.sharedMesh;
            meshFilter.sharedMesh = null;
            MeshCollider collider = p.Owner.AddComponent<MeshCollider>();
            meshFilter.sharedMesh = shown;
            collider.cookingOptions = options;
            collider.sharedMesh = p.Mesh;
        }
        return next;
    }

    void OnDestroy()
    {
        foreach (Mesh mesh in meshes)
            Destroy(mesh);
        meshes.Clear();
    }
}
#endif
