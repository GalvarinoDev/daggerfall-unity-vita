// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Development builds (VITA_DEV=1) under Wine: UnityLinker rewrites the script DLLs' .pdb symbols through Windows' native PDB writer
// (a COM component Wine doesn't have) and dies with a NullReferenceException in Mono.Cecil.Pdb.SymWriter.Initialize. IL2CPP never
// reads them, so they are deleted from the staging folder after the script DLLs are built and before the linker runs. Editor only.

#if UNITY_2018_1_OR_NEWER
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

public class VitaStripPdb : IPostBuildPlayerScriptDLLs
{
    public int callbackOrder { get { return 0; } }

    public void OnPostBuildPlayerScriptDLLs(BuildReport report)
    {
        if (report.summary.platform != BuildTarget.PSP2 || (report.summary.options & BuildOptions.Development) == 0)
            return;
        string managed = Path.Combine("Temp", Path.Combine("StagingArea", Path.Combine("Data", "Managed")));
        if (!Directory.Exists(managed))
        {
            Debug.Log("VITABUILD pdb strip: no " + managed);
            return;
        }
        int count = 0;
        foreach (string pdb in Directory.GetFiles(managed, "*.pdb"))
        {
            File.Delete(pdb);
            count++;
        }
        Debug.Log("VITABUILD pdb strip: removed " + count + " .pdb from " + managed);
    }
}
#endif
