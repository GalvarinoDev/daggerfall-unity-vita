// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// World of Daggerfall 0.4.0 on Location Loader (KABoissonneault fork) built in (scripts in LocationLoader/).
// Like VitaTravelOptions: makes Location Loader's Mod object (its save data and messages go by that title, as on the PC), adds it to
// ModManager and calls its [Invoke(Start)] entry at the Start state. WoD's placements and location prefabs come from the PC bake
// (VitaNative/wodinst.bin, wodprefabs.bin; VitaWodData). settings.ini [Video] VitaWorldOfDaggerfall (off unless "True" until it has
// passed a hardware run), read at start: applies from the next launch.

#if UNITY_PSP2
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;

public static class VitaWorldOfDaggerfall
{
    static bool started;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        StateManager.OnStateChange += state =>
        {
            if (state != StateManager.StateTypes.Start || started || !DaggerfallUnity.Settings.VitaWorldOfDaggerfall || !ModManager.Instance)
                return;
            started = true;
            ModInfo info = new ModInfo();
            info.ModTitle = "Location Loader";
            info.ModVersion = "0.3";
            info.ModAuthor = "kaboissonneault";
            info.GUID = "fc5c0fa6-e80d-4cb1-89fa-c10be8e35bf3";
            Mod mod = new Mod("location loader", info, "VitaModTextures/WorldOfDaggerfall");
            ModManager.Instance.VitaAddBuiltInMod(mod);
            LocationLoader.LocationModLoader.Init(new InitParams(mod, ModManager.Instance.GetModIndex(mod.Title), ModManager.Instance.LoadedModCount));
        };
    }
}
#endif
