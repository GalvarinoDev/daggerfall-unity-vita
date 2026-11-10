// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Import settings for Resources/VitaModTextures (VitaModTextures.cs): mod sprites stay as drawn (uncompressed RGBA, point filter,
// no mips, no power-of-two rescale; they are NPOT), and not readable (no second copy in main memory).

using UnityEditor;

public class VitaModTexturesImport : AssetPostprocessor
{
    // Unity re-imports what this processor handled only when this number changes (1: Dragon Rider's transport art readable)
    public override uint GetVersion()
    {
        return 1;
    }

    void OnPreprocessTexture()
    {
        if (!assetPath.Contains("/Resources/VitaModTextures/"))
            return;
        TextureImporter importer = (TextureImporter)assetImporter;
        importer.textureType = TextureImporterType.Default;
        importer.alphaIsTransparency = true;
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.mipmapEnabled = false;
        importer.filterMode = UnityEngine.FilterMode.Point;
        // Dragon Rider's riding frames and egg icon are bilinear in the PC mod (its transport-window art is point)
        if (assetPath.Contains("/DragonRider/dragonhorse_") || assetPath.EndsWith("/4900_0-0.png"))
            importer.filterMode = UnityEngine.FilterMode.Bilinear;
        importer.wrapMode = UnityEngine.TextureWrapMode.Clamp;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        // Dragon Rider's transport window cuts its greyed-out rows from MOVE01I0 with GetPixels (unreadable = exception in Setup, an
        // empty window holding the input on hardware). Both window images are tiny (~58 KB)
        importer.isReadable = assetPath.Contains("/DragonRider/MOVE0");
    }
}
