// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// BasicRoads mod (Hazelnut) built in: roads and dirt tracks painted onto the terrain (BasicRoads/BasicRoadsTexturing.cs).
// settings.ini [Video] VitaBasicRoads = True turns it on (default off until it's measured on hardware). Read each time the game
// scene loads, so the setup wizard's Options page applies it without a restart. Off = upstream's DefaultTerrainTexturing and
// no path data loaded. Cost when on: 1 MB main (road + track maps, read from VitaMods/basicroads.pak), CPU only for terrain tiles a path crosses.

#if UNITY_PSP2
using UnityEngine;
using UnityEngine.SceneManagement;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.BasicRoads;

public static class VitaBasicRoads
{
    // The mod's defaults (modsettings.json): light smoothing of road surfaces on, rivers and streams off
    const bool smoothRoads = true;
    const bool riversAndStreams = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        SceneManager.sceneLoaded += (scene, mode) => { if (scene.buildIndex == SceneControl.GameSceneIndex) Apply(); };
    }

    static void Apply()
    {
        DaggerfallUnity dfUnity = DaggerfallUnity.Instance;
        bool installed = dfUnity.TerrainTexturing is BasicRoadsTexturing;
        if (DaggerfallUnity.Settings.VitaBasicRoads && !installed)
        {
            dfUnity.TerrainTexturing = new BasicRoadsTexturing(smoothRoads, riversAndStreams);
            Debug.Log("VitaBasicRoads: on");
        }
        else if (!DaggerfallUnity.Settings.VitaBasicRoads && installed)
        {
            dfUnity.TerrainTexturing = new DefaultTerrainTexturing();
            BasicRoadsTexturing.ReleasePathData();
            Debug.Log("VitaBasicRoads: off");
        }
    }
}
#endif
