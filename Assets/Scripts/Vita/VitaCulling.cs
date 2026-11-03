// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Dungeon CPU cost is per-object scripts (billboards, lights, audio sources, doors, enemies), visible or not (runs 55-58). Far ones
// are switched off: a block's Flats/Lights/Action Doors when its footprint is over a block away, an enemy when it is over 1 block
// away (enemies cull by their own position, so a chaser from a far block stays active). Models stay, with their colliders.
// Idea from Vwing's DFU Android port, its CulledGameObjectManager (github.com/Vwing/daggerfall-unity-android, MIT).
// Only objects switched off here are switched back on (quests hide foes too). Nothing is culled in the first seconds of a dungeon:
// serializable objects register for saving in Start, which only runs while active.
// city 15.1 -> 21.9 fps at 300 m). Only renderers, so colliders, scripts and NPCs stay; terrain and trees keep the long view.
// Dungeon models: a block's model renderers (Models, Action Models and the Vita batches) are switched off when its footprint is over
// Town flats beyond the range also get their billboard script switched off (no facing/animation restart work). Both from Vwing's port.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Utility;

public class VitaCulling : MonoBehaviour
{
    const float blockRange = RDBLayout.RDBSide;
    public static float EnemyRange = RDBLayout.RDBSide;
    const float hysteresis = 4f;
    const int checkFrames = 10;
    const float startDelay = 2f;
    static readonly string[] blockGroups = { "Flats", "Lights", "Action Doors" };
    static readonly string[] modelGroups = { "Models", "Action Models", "VitaModelBatch", "VitaFlatBatch" };
    public static float ModelRange = RDBLayout.RDBSide;

    DaggerfallDungeon dungeon;
    float dungeonSince, enemiesListed, lastLog;
    DaggerfallEnemy[] enemies = new DaggerfallEnemy[0];
    readonly HashSet<GameObject> culled = new HashSet<GameObject>();
    readonly Dictionary<Transform, List<Renderer>> culledModels = new Dictionary<Transform, List<Renderer>>();   // only what we switched off

    struct TownRenderer
    {
        public Renderer renderer;
        public MonoBehaviour billboard;  // town flats: switched off with the renderer
        public Bounds bounds;       // relative to the location: StreamingWorld moves the world (floating origin)
        public bool culled;
    }
    static readonly Dictionary<GameObject, TownRenderer[]> towns = new Dictionary<GameObject, TownRenderer[]>();
    static readonly List<GameObject> deadTowns = new List<GameObject>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        GameObject go = new GameObject("VitaCulling");
        DontDestroyOnLoad(go);
        go.AddComponent<VitaCulling>();
        StreamingWorld.OnUpdateLocationGameObject += AddTown;     // static and lives for the whole run, like this object
    }

    // Block models (also VitaCityMerge's) and flats of a town; editor-marker flats stay hidden, so they are never touched
    static void AddTown(GameObject location, bool allowYield)
    {
        var list = new List<TownRenderer>();
        foreach (MeshRenderer renderer in location.GetComponentsInChildren<MeshRenderer>(true))
        {
            Billboard billboard = renderer.GetComponent<Billboard>();
            if (billboard ? billboard.Summary.FlatType == FlatTypes.Editor : renderer.name != "CombinedModels" && renderer.name != "Merged" && renderer.name != "VitaFlatBatch")
                continue;
            TownRenderer town;
            town.renderer = renderer;
            town.billboard = billboard;
            town.bounds = renderer.bounds;
            town.bounds.center -= location.transform.position;
            town.culled = false;
            list.Add(town);
        }
        towns[location] = list.ToArray();
    }

    // Returns how many renderers are culled
    static int CullTowns(Vector3 player, float range)
    {
        int count = 0;
        deadTowns.Clear();
        foreach (var pair in towns)
        {
            if (!pair.Key)
            {
                deadTowns.Add(pair.Key);
                continue;
            }
            TownRenderer[] list = pair.Value;
            Vector3 local = player - pair.Key.transform.position;
            for (int i = 0; i < list.Length; i++)
            {
                if (!list[i].renderer)
                    continue;
                float distance = range > 0 ? Mathf.Sqrt(list[i].bounds.SqrDistance(local)) : 0;
                if (!list[i].culled && distance > range && range > 0 && list[i].renderer.enabled)
                {
                    list[i].renderer.enabled = false;
                    if (list[i].billboard)
                    {
                        // Disabling doesn't stop its animation coroutine, and OnDisable makes Update start a new one on re-enable
                        list[i].billboard.StopAllCoroutines();
                        list[i].billboard.enabled = false;
                    }
                    list[i].culled = true;
                }
                else if (list[i].culled && distance < range - hysteresis)
                {
                    list[i].renderer.enabled = true;
                    if (list[i].billboard)
                        list[i].billboard.enabled = true;
                    list[i].culled = false;
                }
                if (list[i].culled)
                    count++;
            }
        }
        foreach (GameObject dead in deadTowns)
            towns.Remove(dead);
        return count;
    }

    public static int TownRenderersCulled;

    void Update()
    {
        if (Time.frameCount % checkFrames != 0 || !GameManager.HasInstance)
            return;
        PlayerEnterExit playerEnterExit = GameManager.Instance.PlayerEnterExit;
        if (playerEnterExit && !playerEnterExit.IsPlayerInside)
        {
            // range 0 (setting off) switches back on what was culled
            int townsCulled = CullTowns(GameManager.Instance.PlayerObject.transform.position, DaggerfallUnity.Settings.VitaTownDistance);
            TownRenderersCulled = townsCulled;
            if (VitaMemory.Probes && Time.realtimeSinceStartup - lastLog > 10)
            {
                lastLog = Time.realtimeSinceStartup;
                Debug.LogFormat("VITACULL town renderers {0} off of {1}", townsCulled, CountTownRenderers());
            }
        }
        DaggerfallDungeon current = playerEnterExit && playerEnterExit.IsPlayerInsideDungeon && DaggerfallUnity.Settings.VitaCulling ?
            playerEnterExit.Dungeon : null;
        float now = Time.realtimeSinceStartup;
        if (current != dungeon)
        {
            UncullAll();
            dungeon = current;
            dungeonSince = now;
            enemiesListed = -100;
        }
        if (!dungeon || now - dungeonSince < startDelay)
            return;

        Vector3 player = GameManager.Instance.PlayerObject.transform.position;
        int groups = 0, culledGroups = 0, culledBlocks = 0;
        foreach (Transform block in dungeon.transform)
        {
            if (!block.GetComponent<DaggerfallRDBBlock>())
                continue;
            // Horizontal distance from the player to the block footprint (position .. position + RDBSide)
            Vector3 corner = block.position;
            float dx = Mathf.Max(0, corner.x - player.x, player.x - (corner.x + RDBLayout.RDBSide));
            float dz = Mathf.Max(0, corner.z - player.z, player.z - (corner.z + RDBLayout.RDBSide));
            float distance = Mathf.Sqrt(dx * dx + dz * dz);
            foreach (string name in blockGroups)
            {
                Transform group = block.Find(name);
                if (group)
                {
                    groups++;
                    if (SetCulled(group.gameObject, distance, blockRange))
                        culledGroups++;
                }
            }
            if (SetModelsCulled(block, distance))
                culledBlocks++;
        }

        // New enemies (spawns, quest foes) are picked up every 10 s
        if (now - enemiesListed > 10)
        {
            enemies = dungeon.GetComponentsInChildren<DaggerfallEnemy>(true);
            enemiesListed = now;
        }
        int culledEnemies = 0;
        foreach (DaggerfallEnemy enemy in enemies)
            if (enemy && SetCulled(enemy.gameObject, Vector3.Distance(enemy.transform.position, player), EnemyRange))
                culledEnemies++;

        if (VitaMemory.Probes && now - lastLog > 10)
        {
            lastLog = now;
            Debug.LogFormat("VITACULL groups {0}/{1} enemies {2}/{3} model blocks {4}", culledGroups, groups, culledEnemies, enemies.Length, culledBlocks);
        }
    }

    // Returns true if the object is culled after the call
    bool SetCulled(GameObject go, float distance, float range)
    {
        bool isCulled = culled.Contains(go);
        if (!isCulled && distance > range && go.activeSelf)
        {
            go.SetActive(false);
            culled.Add(go);
            return true;
        }
        if (isCulled && distance < range - hysteresis)
        {
            go.SetActive(true);
            culled.Remove(go);
            return false;
        }
        return isCulled;
    }

    // Returns true if the block's models are culled after the call
    bool SetModelsCulled(Transform block, float distance)
    {
        List<Renderer> off;
        bool isCulled = culledModels.TryGetValue(block, out off);
        if (!isCulled && ModelRange > 0 && distance > ModelRange)
        {
            off = new List<Renderer>();
            foreach (string name in modelGroups)
            {
                Transform group = block.Find(name);
                if (group)
                    foreach (Renderer renderer in group.GetComponentsInChildren<Renderer>())
                        if (renderer.enabled)
                        {
                            renderer.enabled = false;
                            off.Add(renderer);
                        }
            }
            culledModels.Add(block, off);
            return true;
        }
        if (isCulled && (ModelRange <= 0 || distance < ModelRange - hysteresis))
        {
            ShowModels(off);
            culledModels.Remove(block);
            return false;
        }
        return isCulled;
    }

    static void ShowModels(List<Renderer> renderers)
    {
        foreach (Renderer renderer in renderers)
            if (renderer)
                renderer.enabled = true;
    }

    static int CountTownRenderers()
    {
        int count = 0;
        foreach (TownRenderer[] list in towns.Values)
            count += list.Length;
        return count;
    }

    void UncullAll()
    {
        foreach (GameObject go in culled)
            if (go)
                go.SetActive(true);
        culled.Clear();
        foreach (List<Renderer> off in culledModels.Values)
            ShowModels(off);
        culledModels.Clear();
    }
}
#endif
