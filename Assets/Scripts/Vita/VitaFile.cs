// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// File.Delete sees every file as read-only and throws UnauthorizedAccessException.

#if UNITY_PSP2
using System.Runtime.InteropServices;

public static class VitaFile
{
    [DllImport("__Internal")]
    static extern int sceIoRemove(string filename); // libSceKernel_stub

    // Deletes the file if it exists; a missing file is fine (same as File.Delete).
    public static void Delete(string path)
    {
        sceIoRemove(path);
    }
}
#endif
