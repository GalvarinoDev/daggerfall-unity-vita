// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Point lights for the Vita palette shaders (DaggerfallDefaultPalette, DaggerfallAtlasPalette). With no Unity pixel lights, those shaders
// light per pixel from near lights; Unity picks a renderer's 4 vertex lights per object, and a dungeon block is one combined mesh, so most
// torches in a block lit nothing. Here the 2 strongest lights at the camera go to the
// shaders as globals instead, so the lights of the room you stand in always show.
// (lights swapping slots popped: town pools appeared only close, the night ground flashed), a longer reach outdoors, warmer fire colours,
// and VITA_PIXEL_TERRAIN_LIGHTS: terrain lights slots 0-1 per pixel while one is a small light (the torch, range 6 m, against 6.4 m
// between terrain vertices: per vertex its pool jumped from vertex to vertex as you walked).
// in the slots lit anything). Town lamps never move, so their pools are painted into one texture over the town (same falloff and colour as
// the slots, by horizontal distance), sampled by world xz (VitaFill.cginc, VITA_TOWN_LIGHTS), and the slots skip them.

#if UNITY_PSP2
using UnityEngine;

public class VitaLights : MonoBehaviour
{
    const int count = 4;      // the per-pixel shaders light slots 0-1 only: 4 per pixel cost the dungeon 51 -> 43 FPS
    const float refreshSeconds = 2f;     // light list (culling and area changes add/remove lights)
    const float reachFactor = 1.5f;      // lights a little beyond their range still light the walls around you
    const float outdoorReachFactor = 3f;     // outdoors a lamp's pool shows from further down the street
    const float fadeSeconds = 0.4f;          // a newly picked light fades in
    const float keepBonus = 1.3f;            // a shown light stays until another beats it by this much
    const float smallRange = 30f;            // below this a light gets per-pixel terrain (IndirectLight's 150 m stays per vertex)

    // Warm fire light: green and blue scaled for lights that are already warm or white (r >= g >= b), so magic lights keep their colour.
    public static int Warmth = -1;
    static readonly Vector2[] warmthLevels = { new Vector2(1f, 1f), new Vector2(0.9f, 0.7f), new Vector2(0.82f, 0.55f), new Vector2(0.75f, 0.42f) };

    static readonly int[] posIds = { Shader.PropertyToID("_VitaLightPos0"), Shader.PropertyToID("_VitaLightPos1"),
        Shader.PropertyToID("_VitaLightPos2"), Shader.PropertyToID("_VitaLightPos3") };
    static readonly int[] colorIds = { Shader.PropertyToID("_VitaLightColor0"), Shader.PropertyToID("_VitaLightColor1"),
        Shader.PropertyToID("_VitaLightColor2"), Shader.PropertyToID("_VitaLightColor3") };
    static readonly int ambientId = Shader.PropertyToID("_VitaAmbient"), ambientLinearId = Shader.PropertyToID("_VitaAmbientLinear");

    // Unity still finds, per renderer per frame, which point lights touch it (CullPerObjectLights: the Palace's lights cost ~8 ms), though
    // with pixelLightCount 0 they are vertex lights and the palette shaders (noambient) drop those: lights come from here. So point lights
    public static bool MaskUnityLights = true;
    static readonly System.Collections.Generic.HashSet<Light> masked = new System.Collections.Generic.HashSet<Light>();

    // Masked lights still cost Unity ~50 us each per frame while enabled. The scene's own lamps
    // (DaggerfallLight / DungeonLightHandler) are therefore "parked": their Light component stays off and their on/off state lives here,
    // read and written through IsEnabled / SetEnabled (both handlers do under UNITY_PSP2). Torches, missiles, Mage Light and the sun keep
    public static bool ParkUnityLights = true;
    static readonly System.Collections.Generic.Dictionary<Light, bool> parked = new System.Collections.Generic.Dictionary<Light, bool>();
    static readonly System.Collections.Generic.List<Light> unparked = new System.Collections.Generic.List<Light>();

    static VitaLights instance;

    // In a shader slot now (or fading in): the only lights whose flicker (Light.range) shows (DaggerfallLight.VitaAnimateShownOnly)
    public static bool IsShown(Light light)
    {
        return instance != null && instance.shown.ContainsKey(light);
    }

    public static bool IsEnabled(Light light)
    {
        bool on;
        return parked.TryGetValue(light, out on) ? on : light.enabled;
    }

    public static void SetEnabled(Light light, bool on)
    {
        if (parked.ContainsKey(light))
            parked[light] = on;
        else
            light.enabled = on;
    }

    public static string State = "";
    public static bool Off;
    public static bool TownOff;

    const int townSize = 512;     // 1 MB RGBA32; ~1.6 m a texel over the biggest city, against 18 m lamp ranges
    static readonly int townTexId = Shader.PropertyToID("_VitaTownLightTex"), townRectId = Shader.PropertyToID("_VitaTownLightRect"),
        townScaleId = Shader.PropertyToID("_VitaTownLightScale");
    Texture2D townTex;
    Color32[] townPixels;
    readonly System.Collections.Generic.HashSet<Light> townLights = new System.Collections.Generic.HashSet<Light>();
    readonly System.Collections.Generic.List<Light> townFound = new System.Collections.Generic.List<Light>();
    float townKey, townFade;
    bool townKeywordOn, refreshed;

    Light[] lights = new Light[0];
    float nextRefresh, nextLog;
    bool keywordOn, terrainKeywordOn;
    readonly System.Collections.Generic.Dictionary<Light, float> shown = new System.Collections.Generic.Dictionary<Light, float>();   // fade 0-1
    readonly System.Collections.Generic.List<Light> dropped = new System.Collections.Generic.List<Light>();
    readonly Light[] picked = new Light[count];
    readonly float[] scores = new float[count];

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Create()
    {
        GameObject go = new GameObject("VitaLights");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<VitaLights>();
    }

    void LateUpdate()
    {
        if (Time.unscaledTime >= nextRefresh)
        {
            lights = System.Array.FindAll(FindObjectsOfType<Light>(), l => l.type == LightType.Point);   // the only type used below
            nextRefresh = Time.unscaledTime + refreshSeconds;
            ApplyMask();
            refreshed = true;
        }
        SetFill();
        Camera camera = Camera.main;
        if (camera == null)
            return;
        Vector3 eye = camera.transform.position;
        DaggerfallWorkshop.Game.PlayerEnterExit enterExit = DaggerfallWorkshop.Game.GameManager.HasInstance ?
            DaggerfallWorkshop.Game.GameManager.Instance.PlayerEnterExit : null;
        bool outside = enterExit && !enterExit.IsPlayerInside;
        float pickFactor = outside ? outdoorReachFactor : reachFactor;
        // Pause menu "Fire light": brightness x boost, reach x half the boost (200% = twice as bright, 1.5x as far). Per area so 100% looks
        // like PC DFU everywhere (cmp4, 63 same-save stops vs PC 1.1.1): dungeons x1, building interiors x0.5, outside x0.7
        float area = outside ? 0.7f : enterExit && enterExit.IsPlayerInsideBuilding ? 0.5f : 1f;
        float boost = DaggerfallWorkshop.DaggerfallUnity.Settings.VitaFireLight / 100f * area, reach = 1f + (boost - 1f) * 0.5f;
        Vector2 warm = warmthLevels[Mathf.Clamp(Warmth >= 0 ? Warmth : DaggerfallWorkshop.DaggerfallUnity.Settings.VitaFireColour, 0, warmthLevels.Length - 1)];
        bool townShown = UpdateTown(outside, boost, reach, warm);

        // Keep the strongest at the eye, by Unity's vertex light falloff
        for (int i = 0; i < count; i++)
        {
            picked[i] = null;
            scores[i] = 0;
        }
        for (int i = 0; i < lights.Length; i++)
        {
            // Distance first (2 native reads), the rest only for lights in reach: the Palace's ~236 lights cost ~2.5 ms here with every
            // check up front
            Light light = lights[i];
            if (light == null || townShown && townLights.Contains(light))
                continue;
            float range = light.range * reach, distanceSq = (light.transform.position - eye).sqrMagnitude;
            if (distanceSq > range * range * pickFactor * pickFactor || light.intensity <= 0)
                continue;
            bool lit;
            if (parked.TryGetValue(light, out lit) ? !lit || !light.gameObject.activeInHierarchy : !light.isActiveAndEnabled)
                continue;
            float score = light.intensity / (1f + 25f * distanceSq / (range * range));
            if (shown.ContainsKey(light))
                score *= keepBonus;
            for (int j = 0; j < count; j++)
            {
                if (score <= scores[j])
                    continue;
                for (int k = count - 1; k > j; k--)
                {
                    picked[k] = picked[k - 1];
                    scores[k] = scores[k - 1];
                }
                picked[j] = light;
                scores[j] = score;
                break;
            }
        }

        // Fades: picked lights fade in, the others are dropped (a light only leaves when a stronger one takes its place)
        dropped.Clear();
        foreach (Light light in shown.Keys)
            if (System.Array.IndexOf(picked, light) < 0)
                dropped.Add(light);
        foreach (Light light in dropped)
            shown.Remove(light);
        float step = Time.unscaledDeltaTime / fadeSeconds;
        bool smallLight = false;

        // The daylight fill that follows the player is no fire: the fire boost and colour turned grey walls tan at noon
        DaggerfallWorkshop.Game.SunlightManager sun = DaggerfallWorkshop.Game.GameManager.HasInstance ?
            DaggerfallWorkshop.Game.GameManager.Instance.SunlightManager : null;
        Light indirect = sun ? sun.IndirectLight : null;

        // Position and 1 / range (the shaders scale the light vector per vertex); an empty slot gets colour 0
        for (int i = 0; i < count; i++)
        {
            Vector4 pos = Vector4.zero, color = Vector4.zero;
            if (picked[i] != null)
            {
                float fade;
                shown.TryGetValue(picked[i], out fade);
                fade = Mathf.Min(1f, fade + step);
                shown[picked[i]] = fade;
                pos = picked[i].transform.position;
                bool fire = picked[i] != indirect;
                pos.w = 1f / (picked[i].range * (fire ? reach : 1f));
                Color c = picked[i].color;
                if (fire && c.r >= c.g && c.g >= c.b)
                {
                    c.g *= warm.x;
                    c.b *= warm.y;
                }
                color = c * (picked[i].intensity * (fire ? boost : 1f) * fade);
                if (i < 2 && picked[i].range * reach < smallRange)
                    smallLight = true;
            }
            Shader.SetGlobalVector(posIds[i], pos);
            Shader.SetGlobalVector(colorIds[i], color);
        }

        // Probe: what the shaders get, every 5 s
        if (VitaMemory.Probes && Time.unscaledTime >= nextLog)
        {
            nextLog = Time.unscaledTime + 5f;
            var line = new System.Text.StringBuilder();
            line.AppendFormat("VITALIGHTS {0} lights, ambient {1}, keyword {2}, terrain {3}, warmth {4}:", lights.Length, RenderSettings.ambientLight,
                keywordOn, terrainKeywordOn, Warmth);
            for (int i = 0; i < count; i++)
                if (picked[i] != null)
                    line.AppendFormat(" [{0} d={1:F1} r={2:F1} i={3:F2} c={4}]", picked[i].name, Vector3.Distance(picked[i].transform.position, eye),
                        picked[i].range, picked[i].intensity, picked[i].color);
            Debug.Log(line.ToString());
        }

        bool on = picked[0] != null && !Off;
        if (on != keywordOn)
        {
            keywordOn = on;
            if (on)
                Shader.EnableKeyword("VITA_NEAR_LIGHTS");
            else
                Shader.DisableKeyword("VITA_NEAR_LIGHTS");
        }
        if (VitaMemory.Probes)
        {
            var state = new System.Text.StringBuilder();
            for (int i = 0; i < count; i++)
                if (picked[i] != null)
                    state.AppendFormat("[{0} {1:F2}] ", picked[i].name, shown[picked[i]]);
            State = state.Append(smallLight ? "terrain" : "-").ToString();
        }
        on = smallLight && !Off;
        if (on != terrainKeywordOn)
        {
            terrainKeywordOn = on;
            if (on)
                Shader.EnableKeyword("VITA_PIXEL_TERRAIN_LIGHTS");
            else
                Shader.DisableKeyword("VITA_PIXEL_TERRAIN_LIGHTS");
        }
    }

    // VitaFill.cginc: the 7 palette shaders draw with noambient (Unity's SH cost 16 cycles per pixel there, shaderaudit), and Unity then sets
    // no SH for them. With a flat ambient (the game scene's mode) and no other directional light (SunlightRig's
    public void ApplyMask()
    {
        masked.RemoveWhere(l => l == null);
        unparked.Clear();
        foreach (var kv in parked)
            if (kv.Key == null || !ParkUnityLights)
                unparked.Add(kv.Key);
        foreach (Light light in unparked)
        {
            if (light != null)
                light.enabled = parked[light];
            parked.Remove(light);
        }
        foreach (Light light in lights)
        {
            if (light == null || light.type != LightType.Point)
                continue;
            if (ParkUnityLights && !parked.ContainsKey(light) &&
                (light.GetComponent<DaggerfallWorkshop.DaggerfallLight>() != null || light.GetComponent<DaggerfallWorkshop.Game.DungeonLightHandler>() != null))
            {
                parked[light] = light.enabled;
                light.enabled = false;
            }
            if (MaskUnityLights && light.cullingMask == ~0)
            {
                light.cullingMask = 0;
                masked.Add(light);
            }
            else if (!MaskUnityLights && masked.Remove(light))
                light.cullingMask = ~0;
        }
    }

    // Town lamps: rebakes when the set of lamps changes (a town loads, the floating origin moves) or the colour settings do; shown while
    // outside with the lamps on (fading as the slots do). Inside a building or dungeon the town is inactive, so the bake is kept for the exit
    bool UpdateTown(bool outside, float boost, float reach, Vector2 warm)
    {
        if (outside && refreshed)
        {
            refreshed = false;
            townFound.Clear();
            float key = reach + warm.x * 7f + warm.y * 13f;
            foreach (Light light in lights)
            {
                if (light == null || light.type != LightType.Point || !light.gameObject.activeInHierarchy)
                    continue;
                DaggerfallWorkshop.DaggerfallLight daggerfallLight = light.GetComponent<DaggerfallWorkshop.DaggerfallLight>();
                if (daggerfallLight == null || daggerfallLight.InteriorLight || light.GetComponentInParent<DaggerfallWorkshop.DaggerfallLocation>() == null)
                    continue;
                townFound.Add(light);
                Vector3 p = light.transform.position;
                key += p.x * 0.37f + p.z * 0.61f + 1f;     // not the range: the lamps' flicker animates it
            }
            if (key != townKey)
            {
                townKey = key;
                BakeTown(reach, warm);
            }
        }

        bool on = outside && townTex != null && !Off && !TownOff && DaggerfallWorkshop.DaggerfallUnity.Instance.WorldTime.Now.IsCityLightsOn;
        townFade = Mathf.MoveTowards(townFade, on ? 1f : 0f, Time.unscaledDeltaTime / fadeSeconds);
        bool shown = townFade > 0 && townTex != null && outside;
        Shader.SetGlobalFloat(townScaleId, 2f * boost * townFade);
        if (shown != townKeywordOn)
        {
            townKeywordOn = shown;
            if (shown)
                Shader.EnableKeyword("VITA_TOWN_LIGHTS");
            else
                Shader.DisableKeyword("VITA_TOWN_LIGHTS");
        }
        return shown && on;
    }

    void BakeTown(float reach, Vector2 warm)
    {
        float start = Time.realtimeSinceStartup;
        townLights.Clear();
        if (townTex != null)
            Destroy(townTex);
        townTex = null;
        if (townFound.Count == 0)
            return;

        float minX = float.MaxValue, minZ = float.MaxValue, maxX = float.MinValue, maxZ = float.MinValue;
        foreach (Light light in townFound)
        {
            Vector3 p = light.transform.position;
            float r = light.range * reach;
            minX = Mathf.Min(minX, p.x - r);
            maxX = Mathf.Max(maxX, p.x + r);
            minZ = Mathf.Min(minZ, p.z - r);
            maxZ = Mathf.Max(maxZ, p.z + r);
        }
        // Square, with 2 black texels round the edge so the clamped border outside the town is dark
        float texel = Mathf.Max(maxX - minX, maxZ - minZ) / (townSize - 4), originX = minX - 2 * texel, originZ = minZ - 2 * texel;
        if (townPixels == null)
            townPixels = new Color32[townSize * townSize];
        else
            System.Array.Clear(townPixels, 0, townPixels.Length);

        // full strength under every lamp. So the lamp's height above the ground counts too
        float heightSum = 0;
        foreach (Light light in townFound)
        {
            townLights.Add(light);
            Vector3 p = light.transform.position;
            float r = light.range * reach, invRSq = 1f / (r * r);
            RaycastHit hit;
            float h = Physics.Raycast(p, Vector3.down, out hit, 30f) ? hit.distance : 2.5f, hSq = h * h;
            heightSum += h;
            Color c = light.color;
            if (c.r >= c.g && c.g >= c.b)
            {
                c.g *= warm.x;
                c.b *= warm.y;
            }
            c *= light.intensity * 0.5f * 255f;     // half brightness: the shader's scale has the x2
            int x0 = Mathf.Max(0, (int)((p.x - r - originX) / texel)), x1 = Mathf.Min(townSize - 1, (int)((p.x + r - originX) / texel));
            int z0 = Mathf.Max(0, (int)((p.z - r - originZ) / texel)), z1 = Mathf.Min(townSize - 1, (int)((p.z + r - originZ) / texel));
            for (int z = z0; z <= z1; z++)
            {
                float dz = originZ + (z + 0.5f) * texel - p.z;
                for (int x = x0; x <= x1; x++)
                {
                    float dx = originX + (x + 0.5f) * texel - p.x;
                    float q = (dx * dx + dz * dz + hSq) * invRSq;
                    if (q >= 1f)
                        continue;
                    float diff = Mathf.Clamp01((1f - q) * 5f) / (1f + 25f * q);     // the shaders' falloff
                    int i = z * townSize + x;
                    Color32 t = townPixels[i];
                    townPixels[i] = new Color32((byte)Mathf.Min(255, t.r + (int)(c.r * diff)), (byte)Mathf.Min(255, t.g + (int)(c.g * diff)),
                        (byte)Mathf.Min(255, t.b + (int)(c.b * diff)), 255);
                }
            }
        }

        townTex = new Texture2D(townSize, townSize, TextureFormat.RGBA32, false);
        townTex.wrapMode = TextureWrapMode.Clamp;
        townTex.filterMode = FilterMode.Bilinear;
        townTex.SetPixels32(townPixels);
        townTex.Apply(false, true);
        float scale = 1f / (texel * townSize);
        Shader.SetGlobalTexture(townTexId, townTex);
        Shader.SetGlobalVector(townRectId, new Vector4(scale, scale, -originX * scale, -originZ * scale));
        Debug.LogFormat("VITATOWN baked {0} lamps over {1:F0} m ({2:F2} m a texel), mean height {3:F1} m, in {4:F0} ms", townFound.Count,
            texel * townSize, texel, heightSum / townFound.Count, (Time.realtimeSinceStartup - start) * 1000f);
    }

    void OnDestroy()
    {
        if (townTex != null)
            Destroy(townTex);
    }

    static void SetFill()
    {
        Color ambient = RenderSettings.ambientLight;
        Shader.SetGlobalColor(ambientId, ambient);
        Shader.SetGlobalColor(ambientLinearId, ambient.linear);
    }
}
#endif
