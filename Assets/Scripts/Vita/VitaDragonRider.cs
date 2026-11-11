// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Dragon Rider mod (relentlessp0sitivity & SquidKamer, 1.1) built in (scripts in DragonRider/).
// IL2CPP can't load mod DLLs, so this makes the mod's Mod object (Mod.cs Vita ctor: assets from Resources/VitaModTextures/DragonRider,
// egg icon VitaModTextures/4900_0-0, quests in StreamingAssets/Quests), adds it to ModManager (so its save data rides in each save)
// and calls its [Invoke(Start)] entry at the Start state, as ModManager does on PC. settings.ini [Video] VitaDragonRider (on unless
// "False") is read at that point: changing it applies from the next launch. Alternate skin (SquidKamer's) left out: its 16 NPOT
// frames would be ~10 MB uncompressed and the mod's default is off. Pad controls: DragonRider.cs "Vita:" lines + InputManager (Square sinks while levitating).

#if UNITY_PSP2
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Utility.ModSupport;

public static class VitaDragonRider
{
    static bool started;
    static VitaStringTable text;

    /// <summary>A message from the language's DragonRiderModData string table, or the mod's English when that language has no line for it.</summary>
    public static string Text(string key, string english)
    {
        if (text == null)
            text = VitaTextTables.Read("DragonRiderModData") ?? new VitaStringTable();
        string value;
        return text.TryGetValue(key, out value) && !string.IsNullOrEmpty(value) ? value : english;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        // After the startup scene's ModManager subscribed, so ModManager's own Start-state setup runs first
        StateManager.OnStateChange += state =>
        {
            if (state != StateManager.StateTypes.Start || started || !DaggerfallUnity.Settings.VitaDragonRider || !ModManager.Instance)
                return;
            started = true;
            ModInfo info = new ModInfo();
            info.ModTitle = "Dragon Rider";
            info.ModVersion = "1.1";
            info.ModAuthor = "relentlessp0sitivity & SquidKamer";
            info.GUID = "1e2680aa-4eb0-4a22-a50b-2e7ad8b25c69";
            Mod mod = new Mod("dragon rider", info, "VitaModTextures/DragonRider");
            ModManager.Instance.VitaAddBuiltInMod(mod);
            DragonRider.DragonRider.Init(new InitParams(mod, ModManager.Instance.GetModIndex(mod.Title), ModManager.Instance.LoadedModCount));
        };
    }
}
#endif
