// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Travel Options mod (Hazelnut, 1.11) built in (scripts in TravelOptions/). Like VitaDragonRider: makes the mod's Mod
// object, adds it to ModManager and calls its [Invoke(Start)] entry at the Start state. Settings are its defaults, written out in
// TravelOptions/TravelSettings.cs, text from StreamingAssets/Text/TravelOptionsModData.csv, UI images
// from Resources/VitaModTextures/TravelOptions. Travel speed is capped and held at 1x while the world builds tiles
// (TravelOptionsMod.VitaLimitTimeScale). settings.ini [Video] VitaTravelOptions (on unless "False"), read at start: applies from the next launch.

#if UNITY_PSP2
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;

public static class VitaTravelOptions
{
    static bool started;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        StateManager.OnStateChange += state =>
        {
            if (state != StateManager.StateTypes.Start || started || !DaggerfallUnity.Settings.VitaTravelOptions || !ModManager.Instance)
                return;
            started = true;
            ModInfo info = new ModInfo();
            info.ModTitle = "Travel Options";
            info.ModVersion = "1.11";
            info.ModAuthor = "Hazelnut";
            info.GUID = "93f3ad1c-83cc-40ac-b762-96d2f47f2e05";
            Mod mod = new Mod("traveloptions", info, "VitaModTextures/TravelOptions");
            ModManager.Instance.VitaAddBuiltInMod(mod);
            TravelOptions.TravelOptionsMod.Init(new InitParams(mod, ModManager.Instance.GetModIndex(mod.Title), ModManager.Instance.LoadedModCount));
        };
    }
}
#endif
