// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Baked location index. ContentReader.EnumerateMaps loaded all 62 regions from MAPS.BSA
// and read each of the 15,251 locations' LocationId from the card to answer "is there a location at this map pixel": 4.7 s on hardware
//. Assets/Editor/Vita/VitaMapIndexBaker.cs runs DFU's own EnumerateMaps on the PC and
// writes what it builds into StreamingAssets/VitaNative/maps.bin; EnumerateMaps reads that instead.
// Layout: magic, version, count, then per location ID, MapID, RegionIndex, MapIndex, LocationType, DungeonType, Discovered, LocationId
// (8 ints; LocationId -1 = none: upstream skips a colliding one). Added locations (loose "locationnew" files or mods) change the index, so it's only used with no mods loaded.

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Utility;

public static class VitaMapIndex
{
    public const int Magic = 0x50414D56;   // "VMAP"
    public const int Version = 1;
    public static bool Disabled;           // the baker builds the index with DFU's own code

    public static string PackPath
    {
        get { return Path.Combine(Path.Combine(Application.streamingAssetsPath, "VitaNative"), "maps.bin"); }
    }

    public static void Write(string path, Dictionary<int, ContentReader.MapSummary> mapDict, Dictionary<int, int> locationIdToMapIdDict)
    {
        var locationIds = new Dictionary<int, int>();   // map ID -> LocationId
        foreach (KeyValuePair<int, int> pair in locationIdToMapIdDict)
            locationIds[pair.Value] = pair.Key;
        using (var writer = new BinaryWriter(new FileStream(path, FileMode.Create)))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(mapDict.Count);
            foreach (ContentReader.MapSummary s in mapDict.Values)
            {
                writer.Write(s.ID); writer.Write(s.MapID); writer.Write(s.RegionIndex); writer.Write(s.MapIndex);
                writer.Write((int)s.LocationType); writer.Write((int)s.DungeonType); writer.Write(s.Discovered ? 1 : 0);
                int locationId;
                writer.Write(locationIds.TryGetValue(s.ID, out locationId) ? locationId : -1);
            }
        }
    }

    /// <summary>Fills both dictionaries from maps.bin. False (dictionaries left empty) if it's missing or damaged, or mods are loaded.</summary>
    public static bool TryLoad(Dictionary<int, ContentReader.MapSummary> mapDict, Dictionary<int, int> locationIdToMapIdDict, string path = null)
    {
        if (path == null)
            path = PackPath;
        if (Disabled || (DaggerfallWorkshop.Game.Utility.ModSupport.ModManager.Instance != null &&
            DaggerfallWorkshop.Game.Utility.ModSupport.ModManager.Instance.LoadedModCount >
            DaggerfallWorkshop.Game.Utility.ModSupport.ModManager.Instance.VitaBuiltInModCount))   // built-in mods' world data is baked
            return false;
        try
        {
            using (var reader = new BinaryReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536)))
            {
                if (reader.ReadInt32() != Magic || reader.ReadInt32() != Version)
                    throw new IOException("not a version " + Version + " map index");
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    var s = new ContentReader.MapSummary();
                    s.ID = reader.ReadInt32();
                    s.MapID = reader.ReadInt32();
                    s.RegionIndex = reader.ReadInt32();
                    s.MapIndex = reader.ReadInt32();
                    s.LocationType = (DFRegion.LocationTypes)reader.ReadInt32();
                    s.DungeonType = (DFRegion.DungeonTypes)reader.ReadInt32();
                    s.Discovered = reader.ReadInt32() != 0;
                    mapDict.Add(s.ID, s);
                    int locationId = reader.ReadInt32();
                    if (locationId != -1)
                        locationIdToMapIdDict.Add(locationId, s.ID);
                }
            }
            Debug.LogFormat("VitaMapIndex: {0} locations", mapDict.Count);
            return true;
        }
        catch (System.Exception e)
        {
            Debug.LogWarningFormat("VitaMapIndex: {0} not loaded: {1}", path, e.Message);
            mapDict.Clear();
            locationIdToMapIdDict.Clear();
            return false;
        }
    }
}
#endif
