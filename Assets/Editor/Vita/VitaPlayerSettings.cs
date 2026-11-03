#if UNITY_PSP2
// Vita tool, run by vita/ scripts: sets the PS Vita player settings through 2017.4's own API.
using UnityEditor;
using UnityEngine;
public static class VitaPlayerSettings {
    public static void Apply() {
        PlayerSettings.SetScriptingBackend(BuildTargetGroup.PSP2, ScriptingImplementation.IL2CPP); // .NET 4.6 on Vita needs IL2CPP
        PlayerSettings.SetApiCompatibilityLevel(BuildTargetGroup.PSP2, ApiCompatibilityLevel.NET_4_6);
        PlayerSettings.PSVita.memoryExpansionMode = PlayerSettings.PSVita.PSVitaMemoryExpansionMode.ExpandBy109MB; // no browser/store while running
        PlayerSettings.PSVita.powerMode = PlayerSettings.PSVita.PSVitaPowerMode.ModeA; // ModeB = faster GPU but no WLAN
        PlayerSettings.PSVita.shortTitle = "Daggerfall";
        PlayerSettings.PSVita.scriptOptimizationLevel = 2; // IL2CPP C++ built with -O2 (0 = -O0)
        PlayerSettings.colorSpace = ColorSpace.Gamma; // Gxm has no linear rendering: player aborts at init with Linear
        // "Mesh Video Mem": MB of CDRAM for mesh data (rest stays for textures/render targets).
        // 4: starting dungeon has ~45k verts (~2 MB); 32 left dungeon textures failing to fit VRAM
        // Splash: their 960x544 Daggerfall Unity title art as the background (Assets/Vita/VitaSplash.png, git-excluded:
        // Bethesda art) instead of the Unity logo, no darkening overlay, no zoom.
        // Without the file: upstream's logo on black, Unity logo below.
        const string splashPath = "Assets/Vita/VitaSplash.png";
        AssetDatabase.ImportAsset(splashPath);
        var splashImporter = AssetImporter.GetAtPath(splashPath) as TextureImporter;
        PlayerSettings.SplashScreen.backgroundColor = Color.black;
        PlayerSettings.SplashScreen.unityLogoStyle = PlayerSettings.SplashScreen.UnityLogoStyle.LightOnDark;
        PlayerSettings.SplashScreen.drawMode = PlayerSettings.SplashScreen.DrawMode.UnityLogoBelow;
        if (splashImporter != null) {
            splashImporter.textureType = TextureImporterType.Sprite;
            splashImporter.mipmapEnabled = false;
            splashImporter.npotScale = TextureImporterNPOTScale.None;
            var psp2 = splashImporter.GetPlatformTextureSettings("PSP2");
            psp2.overridden = true;
            psp2.format = TextureImporterFormat.RGB16; // NPOT: never compress (Unity would make a POT copy); 1 MB
            splashImporter.SetPlatformTextureSettings(psp2);
            splashImporter.SaveAndReimport();
            PlayerSettings.SplashScreen.background = AssetDatabase.LoadAssetAtPath<Sprite>(splashPath);
            PlayerSettings.SplashScreen.overlayOpacity = 0;
            PlayerSettings.SplashScreen.animationMode = PlayerSettings.SplashScreen.AnimationMode.Static;
            PlayerSettings.SplashScreen.logos = new PlayerSettings.SplashScreenLogo[0];
            PlayerSettings.SplashScreen.showUnityLogo = false;
            // No Unity splash at all: it showed the art washed-out white for ~11 s on hardware. pic0 covers the engine load
            // and VitaBootSplash draws the art from the first scene on.
            PlayerSettings.SplashScreen.show = false;
        } else {
            Sprite splashLogo = AssetDatabase.LoadAssetAtPath<Sprite>("Assets/Resources/workshop_logo_hd.png"); // VitaBuild shrinks it to 512
            PlayerSettings.SplashScreen.logos = new[] { PlayerSettings.SplashScreenLogo.Create(2f, splashLogo) };
        }
        var ps = new SerializedObject(Unsupported.GetSerializedAssetInterfaceSingleton("PlayerSettings"));
        ps.FindProperty("videoMemoryForVertexBuffers").intValue = 4;
        // The Vita player's boot image, shown while the engine loads (Unity's own logo when empty): the same art
        ps.FindProperty("psp2Splashimage").objectReferenceValue = AssetDatabase.LoadAssetAtPath<Texture2D>(splashPath);
        ps.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        Debug.Log("VITASETTINGS backend=" + PlayerSettings.GetScriptingBackend(BuildTargetGroup.PSP2)
            + " api=" + PlayerSettings.GetApiCompatibilityLevel(BuildTargetGroup.PSP2)
            + " mem=" + PlayerSettings.PSVita.memoryExpansionMode
            + " meshVideoMem=" + ps.FindProperty("videoMemoryForVertexBuffers").intValue
            + " splash=" + PlayerSettings.SplashScreen.show + "/unityLogo=" + PlayerSettings.SplashScreen.showUnityLogo + "/" + PlayerSettings.SplashScreen.logos.Length + " logos/background="
            + (PlayerSettings.SplashScreen.background != null ? PlayerSettings.SplashScreen.background.name : "none")
            + " bootImage=" + (ps.FindProperty("psp2Splashimage").objectReferenceValue != null ? ps.FindProperty("psp2Splashimage").objectReferenceValue.name : "none"));
    }
}
#endif
