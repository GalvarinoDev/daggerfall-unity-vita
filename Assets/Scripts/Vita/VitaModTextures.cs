// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Flat sprites from other PC mods that the built-in Beautiful Cities / Villages place (RMB Resource Pack archives 540, 1200, 1210,
// 1230: interior clutter, NPC-style flats). vita/tools/modflats.py copies the records they use into Resources/VitaModTextures
// (imported by VitaModTexturesImport: uncompressed, no mips) with each record's XML (scale). DFU's TextureReplacement and
// XMLManager ask here before their loose-file and mod lookups, so the billboards are made exactly as with the PC mod.
// Those archives are now palette TEXTURE files in StreamingAssets/VitaNative (vita/tools/flatpack.py) and this
// folder is empty: kept for mod art that does not fit a palette.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;

public static class VitaModTextures
{
    const string folder = "VitaModTextures/";
    static Dictionary<string, string> names;   // name -> Resources path

    public static bool TryLoad(string name, out Texture2D texture)
    {
        string path;
        texture = Has(name, out path) ? Resources.Load<Texture2D>(path) : null;
        return texture != null;
    }

    /// <summary>The XML for name ("1210_3-0.xml"), or null.</summary>
    public static string Xml(string name)
    {
        string baseName = name.EndsWith(".xml") ? name.Substring(0, name.Length - 4) : name;
        string path;
        if (!Has(baseName, out path))
            return null;
        TextAsset asset = Resources.Load<TextAsset>(path);
        if (!asset)
            return null;
        string text = asset.text;
        Resources.UnloadAsset(asset);
        return text;
    }

    static bool Has(string name)
    {
        string path;
        return Has(name, out path);
    }

    static bool Has(string name, out string path)
    {
        if (names == null)
        {
            names = new Dictionary<string, string>();
            AddIndex(folder);
            if (VitaRedguards.Enabled)
                AddIndex(folder + "Redguards/");    // Redguards of Hammerfell's talk portraits (vita/tools/rghpack.py)
#if VITA_TRAVELOPTIONS
            // Travel Options' UI images (asked for as "TOcontrolUI.png"). Always indexed: this list is built once, often before the
            // Mods window turns the mod on, and its map window fails on a null button when its images can't be found.
            AddIndex(folder + "TravelOptions/");
#endif
        }
        return names.TryGetValue(name, out path) ||
            (name.EndsWith(".png") && names.TryGetValue(name.Substring(0, name.Length - 4), out path));
    }

    static void AddIndex(string subfolder)
    {
        TextAsset index = Resources.Load<TextAsset>(subfolder + "index");
        if (!index)
            return;
        foreach (string line in index.text.Split('\n'))
            if (line.Trim().Length > 0)
                names[line.Trim()] = subfolder + line.Trim();
        Resources.UnloadAsset(index);
    }
}
#endif
