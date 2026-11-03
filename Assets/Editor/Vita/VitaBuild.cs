#if UNITY_PSP2
// Vita tool, run by vita/build.sh: PC Hosted PS Vita build (the folder vita/tools/pack_vpk.py turns into a VPK).
using System.Linq;
using UnityEditor;
using UnityEngine;
public static class VitaBuild {
    public static void Build() {
        EditorUserBuildSettings.psp2BuildSubtarget = PSP2BuildSubtarget.PCHosted;
        // Title-screen images: full-size RGB24/RGBA32 cost ~16 MB of VRAM; 16-bit (uncompressed: they're NPOT) at <=1024 is plenty on 960x544
        Shrink("Assets/Resources/StartupBackground.jpg", 1024, TextureImporterFormat.RGB16);
        Shrink("Assets/Resources/StartupBackground2.jpg", 1024, TextureImporterFormat.RGB16);
        Shrink("Assets/Resources/workshop_logo_hd.png", 512, TextureImporterFormat.RGBA16);
        Shrink("Assets/Resources/Daggerfall Unity Splash 2.png", 1024, TextureImporterFormat.RGB16); // splash background: stays loaded all game (16 MB at 2048x1152 ARGB32)
        Shrink("Assets/Resources/Vita/StartupBackground2.png", 1024, TextureImporterFormat.RGB16); // already 960x544: only the format changes
        // VitaBoot (empty, index 0) draws the splash art before the startup scene's blocking setup (VitaBootMusic.cs); added here,
        // not in EditorBuildSettings, so other platforms keep their scene list. SceneControl shifts its indices to match.
        var scenes = new[] { "Assets/Scenes/VitaBoot.unity" }.Concat(EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path)).ToArray();
        // VITA_DEV=1 vita/build.sh: Development player
        var options = System.Environment.GetEnvironmentVariable("VITA_DEV") == "1" ? BuildOptions.Development : BuildOptions.None;
        PlayerSettings.graphicsJobs = System.Environment.GetEnvironmentVariable("VITA_GFXJOBS") == "1";
        Debug.Log("VITABUILD graphicsJobs=" + PlayerSettings.graphicsJobs);
        var report = BuildPipeline.BuildPlayer(scenes, "vita/build/DFU", BuildTarget.PSP2, options);
#if UNITY_2018_1_OR_NEWER
        // 2018: BuildPlayer returns a BuildReport, not an error string
        var result = report.summary.result;
        Debug.Log("VITABUILD " + (result == UnityEditor.Build.Reporting.BuildResult.Succeeded ? "ok" : "failed: " + result));
#else
        Debug.Log("VITABUILD " + (string.IsNullOrEmpty(report) ? "ok" : "failed: " + report));
#endif
    }

    // PSP2 import override (written to the .meta by Unity); reimports only when it changes
    static void Shrink(string path, int maxSize, TextureImporterFormat format) {
        var importer = (TextureImporter)AssetImporter.GetAtPath(path);
        var s = importer.GetPlatformTextureSettings("PSP2");
        if (s.overridden && s.maxTextureSize == maxSize && s.format == format)
            return;
        s.overridden = true;
        s.maxTextureSize = maxSize;
        s.format = format;
        importer.SetPlatformTextureSettings(s);
        importer.SaveAndReimport();
    }
}
#endif
