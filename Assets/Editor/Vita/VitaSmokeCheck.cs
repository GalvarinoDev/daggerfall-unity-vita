// PS Vita port: checks that Resources/VitaFX/WodSmoke.prefab (RMB Resource Pack 52990, YAML downgraded from 2019) imports with the mod's values.
//   vita/unity2017.sh VitaSmokeCheck.Run smokecheck   (log: VITASMOKE lines)
using UnityEngine;

public static class VitaSmokeCheck
{
    public static void Run()
    {
        GameObject go = Resources.Load<GameObject>("VitaFX/WodSmoke");
        ParticleSystem ps = go ? go.GetComponent<ParticleSystem>() : null;
        if (!ps)
        {
            Debug.Log("VITASMOKE missing prefab or ParticleSystem");
            return;
        }
        var main = ps.main;
        var r = go.GetComponent<ParticleSystemRenderer>();
        Debug.LogFormat("VITASMOKE duration {0} loop {1} prewarm {2} simSpeed {3} lifetime {4}/{5} speed {6}/{7} size {8}/{9} max {10} gravity {11} space {12}",
            main.duration, main.loop, main.prewarm, main.simulationSpeed, main.startLifetime.constantMin, main.startLifetime.constantMax,
            main.startSpeed.constantMin, main.startSpeed.constantMax, main.startSize.constantMin, main.startSize.constantMax, main.maxParticles,
            main.gravityModifier.constant, main.simulationSpace);
        Debug.LogFormat("VITASMOKE color {0} / {1} mode {2}", main.startColor.colorMin, main.startColor.colorMax, main.startColor.mode);
        Debug.LogFormat("VITASMOKE emission {0} rate {1} shape {2} {3} angle {4} radius {5} size {6} rotation {7} colorLife {8} velocity {9} noise {10} rotBySpeed {11}",
            ps.emission.enabled, ps.emission.rateOverTime.constant, ps.shape.enabled, ps.shape.shapeType, ps.shape.angle, ps.shape.radius,
            ps.sizeOverLifetime.enabled, ps.rotationOverLifetime.enabled, ps.colorOverLifetime.enabled, ps.velocityOverLifetime.enabled,
            ps.noise.enabled, ps.rotationBySpeed.enabled);
        Debug.LogFormat("VITASMOKE renderer {0} material {1} shader {2} tex {3}", r ? r.renderMode.ToString() : "none",
            r && r.sharedMaterial ? r.sharedMaterial.name : "none", r && r.sharedMaterial ? r.sharedMaterial.shader.name : "none",
            r && r.sharedMaterial && r.sharedMaterial.mainTexture ? r.sharedMaterial.mainTexture.name : "none");
    }
}
