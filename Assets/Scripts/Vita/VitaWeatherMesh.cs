// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Rain and snow without ParticleSystem simulation (trial, revert candidate). The player's Rain_Particles / Snow_Particles systems
// simulate in their own local space (they follow the player), so their motion can be replayed exactly from the time in a vertex
// shader (Resources/VitaFX/VitaWeather): one mesh per system with one quad per live particle, the same rate, box, forces, size over
// lifetime, velocity clamp and screen-size clamp as the system after PlayerWeather's Vita tuning, a splash where each drop meets the
// ground plane at the player's feet (VitaGroundPlane) instead of the splash sub-emitter, and nothing simulated on the CPU.
// The GameObjects stay as they are (PlayerWeather still switches them), only the particle systems stop and their renderers go off.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;

public class VitaWeatherMesh : MonoBehaviour
{
    public static bool Use;         // test bot A/B ("shaderweather" / cityweather); off = Unity's particle systems as before

    ParticleSystem[] systems;
    Mesh mesh;
    Material material;
    MeshRenderer meshRenderer;
    bool snow;
    float groundY;
    bool applied;

    // weather: Rain_Particles or Snow_Particles; groundWorldY: the collision plane's height when the player stands still
    public static void Attach(GameObject weather, bool snow, float groundWorldY)
    {
        if (!weather)
            return;
        VitaWeatherMesh fx = weather.AddComponent<VitaWeatherMesh>();
        fx.snow = snow;
        fx.groundY = weather.transform.InverseTransformPoint(new Vector3(weather.transform.position.x, groundWorldY, weather.transform.position.z)).y;
        fx.systems = weather.GetComponentsInChildren<ParticleSystem>(true);
    }

    // Nothing changes while Use stays off (applied starts false)
    void OnEnable()
    {
        if (applied != Use)
            Apply();
    }

    void Update()
    {
        if (applied != Use)
            Apply();
    }

    void Apply()
    {
        applied = Use;
        if (systems == null || systems.Length == 0)
            return;
        if (Use && !mesh)
            Build();
        foreach (ParticleSystem ps in systems)
        {
            if (ps == systems[0])
            {
                ParticleSystem.MainModule main = ps.main;
                main.playOnAwake = !Use;    // the splash systems are sub-emitters: they only emit when the main one does
            }
            ParticleSystemRenderer r = ps.GetComponent<ParticleSystemRenderer>();
            if (r)
                r.enabled = !Use;
            if (Use)
                ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            else if (ps == systems[0] && isActiveAndEnabled)
                ps.Play(true);
        }
        if (meshRenderer)
            meshRenderer.enabled = Use;
    }

    // Upstream values from PlayerAdvanced.prefab: rain (5 s lifetime, start size 0.16-1, force local x 5-10 and y -100, stretched
    // billboard x4, max screen size 0.001, splash 0.15 s); snow (5 s, size 0.14-1, gravity 1, world force -5..5 per axis, speed clamped
    // to 7.33, alpha fades out after 81% of the lifetime, max screen size 0.075, splash 0.75 s). Size over lifetime 0.3 -> 1 for both.
    void Build()
    {
        ParticleSystem ps = systems[0];
        ParticleSystem.MainModule main = ps.main;
        ParticleSystem.ShapeModule shape = ps.shape;
        float rate = ps.emission.rateOverTimeMultiplier;
        Vector3 box = shape.scale;
        float sizeMin = main.startSize.constantMin, sizeMax = main.startSize.constantMax;
        float clamp = snow ? 7.33f : 1e4f;
        float splash = snow ? 0.75f : 0.15f;
        float lifetime = main.startLifetime.constant;

        System.Random rnd = new System.Random(snow ? 2 : 1);   // own generator: the game's UnityEngine.Random stays untouched
        var corners = new List<Vector2>();
        var seeds = new List<Vector4>();
        var accels = new List<Vector4>();
        var hits = new List<Vector2>();
        var vertices = new List<Vector3>();
        // Each quad replays one particle after another (a new random spot each cycle); quads = rate x mean cycle, phases spread evenly
        float cycles = 0;
        int count = 0;
        while (count < 8000 && (count < 1 || count < rate * cycles / count))
        {
            Vector3 a = snow ? new Vector3(Rand(rnd, -5, 5), Rand(rnd, -5, 5) - 9.81f, Rand(rnd, -5, 5)) : new Vector3(Rand(rnd, 5, 10), -100, 0);
            float y0 = Rand(rnd, -0.5f, 0.5f) * box.y;
            float hit = Mathf.Min(HitTime(a, clamp, y0 - groundY), lifetime);
            float period = Mathf.Min(hit + splash, lifetime);
            cycles += period;
            Vector4 seed = new Vector4((float)rnd.NextDouble(), period, (float)rnd.NextDouble(), (float)rnd.NextDouble());
            Vector4 accel = new Vector4(a.x, a.y, a.z, Rand(rnd, sizeMin, sizeMax));
            Vector2 h = new Vector2(hit < lifetime ? hit : 1e4f, y0);
            for (int c = 0; c < 4; c++)
            {
                vertices.Add(Vector3.zero);
                corners.Add(new Vector2(c == 1 || c == 2 ? 1 : 0, c >= 2 ? 1 : 0));
                seeds.Add(seed);
                accels.Add(accel);
                hits.Add(h);
            }
            count++;
        }
        int[] indices = new int[count * 6];
        for (int q = 0, i = 0; q < count * 4; q += 4)
        {
            indices[i++] = q; indices[i++] = q + 1; indices[i++] = q + 2;
            indices[i++] = q; indices[i++] = q + 2; indices[i++] = q + 3;
        }
        mesh = new Mesh();
        mesh.name = snow ? "VitaSnow" : "VitaRain";
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, corners);
        mesh.SetUVs(1, seeds);
        mesh.SetUVs(2, accels);
        mesh.SetUVs(3, hits);
        mesh.SetTriangles(indices, 0);
        mesh.bounds = new Bounds(new Vector3(0, groundY * 0.5f, 0), new Vector3(box.x + 30, -groundY + 10, box.z + 30));
        mesh.UploadMeshData(true);

        Shader shader = Resources.Load<Shader>("VitaFX/VitaWeather");
        material = new Material(shader);
        ParticleSystemRenderer psr = ps.GetComponent<ParticleSystemRenderer>();
        if (psr && psr.sharedMaterial)
            material.mainTexture = psr.sharedMaterial.mainTexture;     // Unity's Default-Particle
        material.SetVector("_Box", new Vector4(box.x, box.z, groundY, splash));
        material.SetVector("_Motion", new Vector4(clamp, lifetime, snow || !psr ? 0 : psr.lengthScale, psr ? psr.maxParticleSize : 1));
        material.SetVector("_Fade", snow ? new Vector4(0.809f * lifetime, 1 / (0.191f * lifetime), 0, 0) : new Vector4(1e4f, 1, 0, 0));
        material.SetVector("_Splash", snow ? new Vector4(0.08f, 0, 0, 0) : new Vector4(0.1f, 0.5f, 0, 0));

        GameObject go = new GameObject(mesh.name);
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        meshRenderer = go.AddComponent<MeshRenderer>();
        meshRenderer.sharedMaterial = material;
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        meshRenderer.receiveShadows = false;
        Debug.LogFormat("VitaWeatherMesh {0}: {1} quads, rate {2:F0}/s, box {3:F0} x {4:F0}, ground {5:F1}", mesh.name, count, rate, box.x, box.z, groundY);
    }

    static float Rand(System.Random rnd, float min, float max)
    {
        return min + (float)rnd.NextDouble() * (max - min);
    }

    // Time for a particle starting at rest with acceleration a (speed clamped) to fall drop metres
    static float HitTime(Vector3 a, float clamp, float drop)
    {
        float len = a.magnitude;
        if (a.y >= 0 || drop <= 0)
            return 1e4f;
        float tc = clamp / len;
        float d1 = -0.5f * a.y * tc * tc;
        if (drop <= d1)
            return Mathf.Sqrt(2 * drop / -a.y);
        return tc + (drop - d1) / (-a.y / len * clamp);
    }

    void OnDestroy()
    {
        if (mesh)
            Destroy(mesh);
        if (material)
            Destroy(material);
    }
}
#endif
