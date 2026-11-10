// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Redguards of Hammerfell - Replaces 0.6 (WilhelmBlack, Nexus mod 666), built in.
// The PC mod swaps whole records of the vanilla NPC flat archives 182, 183, 334 and 357 plus 6 talk portraits. vita/tools/rghpack.py bakes
// full copies of those archives with the mod's records in as VitaNative/TEXTURE.10182 etc. (palette-exact in ART_PAL, classic world size
// kept as DFU does), and the portraits into Resources/VitaModTextures/Redguards (VitaModTextures). TextureReader reads 10<archive> in place
// of <archive> while the setting is on, so atlases, the atlas pack and the card cache keep both versions apart. Read once per launch: materials
// and atlases are cached, so a change applies from the next launch.

#if UNITY_PSP2
using DaggerfallWorkshop;

public static class VitaRedguards
{
    const int Offset = 10000;
    static int enabled = -1;

    public static bool Enabled
    {
        get
        {
            if (enabled < 0)
                enabled = DaggerfallUnity.Settings.VitaRedguards ? 1 : 0;
            return enabled == 1;
        }
    }

    /// <summary>The archive to read for a flat archive: the baked Redguard copy of 182/183/334/357 while the mod is on.</summary>
    public static int Archive(int archive)
    {
#if !UNITY_EDITOR   // the editor bakers bake both copies by their own numbers (VitaAtlasBaker)
        if ((archive == 182 || archive == 183 || archive == 334 || archive == 357) && Enabled)
            return archive + Offset;
#endif
        return archive;
    }
}
#endif
