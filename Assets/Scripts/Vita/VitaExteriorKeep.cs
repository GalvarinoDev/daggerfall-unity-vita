// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Building interiors without re-cooking the town's colliders (trial, 2026-11-12). Upstream switches the whole exterior off on the way
// into a building; on the way out every static MeshCollider in it is cooked again (Unity 2018.2 keeps no cooked data): ~3 s in a city.
// Moving a static collider is ~0.1 ms, reparenting or switching it off is a full cook. So on entry the exterior root stays active and
// everything is switched off except the chain of objects down to each static MeshCollider: on that chain behaviours are disabled with
// their coroutines stopped (what deactivation does), renderers and other colliders are switched off, and the kept colliders move 10 km
// down out of the interior's way. On exit it is all put back. Any other path that switches the exterior (dungeons, loads) calls Release
// first: the root goes inactive, then the changes under it are undone, which leaves exactly upstream's state.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;

public static class VitaExteriorKeep
{
    public static bool Off;                 // test switch: upstream behaviour
    static readonly Vector3 away = new Vector3(0, -10000f, 0);

    static GameObject root;
    static readonly List<GameObject> subtrees = new List<GameObject>();
    static readonly List<Behaviour> behaviours = new List<Behaviour>();
    static readonly List<Renderer> renderers = new List<Renderer>();
    static readonly List<Collider> colliders = new List<Collider>();
    static readonly List<Transform> moved = new List<Transform>();

    public static bool Active { get { return root != null; } }

    // Instead of exterior.SetActive(false) on the way into a building
    public static void Keep(GameObject exterior)
    {
        if (Off || !exterior || !exterior.activeSelf || Active)
        {
            if (exterior)
                exterior.SetActive(false);
            return;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var kept = new HashSet<MeshCollider>();
        var chain = new HashSet<Transform>();
        foreach (MeshCollider mc in exterior.GetComponentsInChildren<MeshCollider>())
        {
            if (!mc.enabled || mc.isTrigger || !mc.sharedMesh || mc.attachedRigidbody ||
                mc.GetComponentInParent<DaggerfallWorkshop.DaggerfallAction>())     // action objects move
                continue;
            kept.Add(mc);
            for (Transform t = mc.transform; t && t != exterior.transform; t = t.parent)
                chain.Add(t);
        }
        if (kept.Count == 0)
        {
            exterior.SetActive(false);
            return;
        }
        root = exterior;
        Walk(exterior.transform, chain, kept);
        foreach (MeshCollider mc in kept)
        {
            mc.transform.position += away;
            moved.Add(mc.transform);
        }
        if (VitaMemory.Probes)     // a log line on every building door is a hitch of its own (7-14 ms, far more with a big log)
            Debug.LogFormat("VitaExteriorKeep: {0} colliders kept, {1} subtrees off, {2} behaviours, {3} renderers, {4} ms", kept.Count,
                subtrees.Count, behaviours.Count, renderers.Count, watch.ElapsedMilliseconds);
    }

    static void Walk(Transform node, HashSet<Transform> chain, HashSet<MeshCollider> kept)
    {
        foreach (Behaviour b in node.GetComponents<Behaviour>())
            if (b && b.enabled)
            {
                b.enabled = false;
                MonoBehaviour mb = b as MonoBehaviour;
                if (mb)
                    mb.StopAllCoroutines();
                behaviours.Add(b);
            }
        foreach (Renderer r in node.GetComponents<Renderer>())
            if (r.enabled)
            {
                r.enabled = false;
                renderers.Add(r);
            }
        foreach (Collider c in node.GetComponents<Collider>())
            if (c.enabled && !(c is MeshCollider && kept.Contains((MeshCollider)c)))
            {
                c.enabled = false;
                colliders.Add(c);
            }
        foreach (Transform child in node)
        {
            if (chain.Contains(child))
                Walk(child, chain, kept);
            else if (child.gameObject.activeSelf)
            {
                child.gameObject.SetActive(false);
                subtrees.Add(child.gameObject);
            }
        }
    }

    // On the way out of the building, instead of exterior.SetActive(true): nothing is cooked again
    public static void Restore()
    {
        if (!Active)
            return;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Undo();
        if (VitaMemory.Probes)
            Debug.LogFormat("VitaExteriorKeep: restored in {0} ms", watch.ElapsedMilliseconds);
    }

    // Before anything else switches the exterior: upstream's state (root inactive, everything under it as it was)
    public static void Release()
    {
        if (!Active)
            return;
        if (root)
            root.SetActive(false);
        Undo();
    }

    static void Undo()
    {
        foreach (Transform t in moved)
            if (t)
                t.position -= away;
        foreach (Collider c in colliders)
            if (c)
                c.enabled = true;
        foreach (Renderer r in renderers)
            if (r)
                r.enabled = true;
        for (int i = behaviours.Count - 1; i >= 0; i--)
            if (behaviours[i])
                behaviours[i].enabled = true;
        foreach (GameObject go in subtrees)
            if (go)
                go.SetActive(true);
        moved.Clear();
        colliders.Clear();
        renderers.Clear();
        behaviours.Clear();
        subtrees.Clear();
        root = null;
    }
}
#endif
