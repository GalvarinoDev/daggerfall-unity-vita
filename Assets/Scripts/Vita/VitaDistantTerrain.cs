// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Distant Terrain of the World of Daggerfall 3.69 (Nystul, MaoDeVaca) built in as the reference port.
// Scripts in DistantTerrain/ (C# 6 / Unity 2018.2 fixes marked "Vita:"), shader in Resources/DistantTerrain. Does what the mod's
// _startupMod.InitStart does at the Start state: config at the mod's own modsettings defaults (the config classes' initial values),
// basic mode forced on (the World of Daggerfall height data is not shipped), then the DistantTerrain component with its shader.
// Left out: the 13th Passage spell, the console command and the fly map. settings.ini [Video] VitaDistantTerrain, off unless "True";
// applies from the next launch. The setting now starts the light far mesh (VitaFarTerrain.cs); the reference port runs only
// with VitaDistantTerrainReference = True as well (comparison shots: it ran the wilderness at 1.3-1.6 FPS).

#if UNITY_PSP2
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;

public static class VitaDistantTerrain
{
    static bool started;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        StateManager.OnStateChange += state =>
        {
            if (state != StateManager.StateTypes.Start || started || !DaggerfallUnity.Settings.VitaDistantTerrain)
                return;
            started = true;
            if (!DaggerfallUnity.Settings.VitaDistantTerrainReference)
            {
                new GameObject("VitaFarTerrain").AddComponent<VitaFarTerrain>();
                Debug.Log("VITADT light far mesh started");
                return;
            }
            DistantTerrain.DistantTerrainBasicModeConfig.EnableBasicMode = true;
            GameObject go = new GameObject("DistantTerrain");
            DistantTerrain.DistantTerrain dt = go.AddComponent<DistantTerrain.DistantTerrain>();
            dt.ShaderDistantTerrainTilemap = Resources.Load<Shader>("DistantTerrain/DistantTerrainTilemap");
            Debug.Log("VITADT started, shader " + (dt.ShaderDistantTerrainTilemap != null));
        };
    }
}
#endif
