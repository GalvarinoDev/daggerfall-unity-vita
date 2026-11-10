// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Vita has no memory to spare: unload unused assets after every scene load and dungeon/interior/exterior
// transition (DaggerfallGC only does it every 180 s), so the previous place's textures are freed.
// Probes then log VITAMEM lines to DFU's Player.log (UserData): textures grouped by
// size/format with an estimated GPU size (Profiler.GetRuntimeMemorySizeLong overcounts on Vita), and meshes.
// VITALEAK lines: which of the previous scene's managers/readers are still alive after the next scene loads.
// Their reference fields are nulled first, so whatever is still alive is held from OUTSIDE the watched set.
// Also watched: everything the title's DaggerfallUI and UI manager reference (persistent windows, the window stack).
// Not #if DEVELOPMENT_BUILD: Unity collects [RuntimeInitializeOnLoadMethod]s from the editor compile, which lacks that define.

#if UNITY_PSP2
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
#if !UNITY_EDITOR
using UnityEngine.PSVita; // player-only API: the editor's UnityEngine has no PSVita namespace
#endif
using UnityEngine.SceneManagement;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using Object = UnityEngine.Object;

public static class VitaMemory
{
    // The development player crashes at startup on consumer Vitas,
    public static readonly bool Probes = Debug.isDebugBuild ||
        System.IO.File.Exists(System.IO.Path.Combine(DaggerfallUnityApplication.PersistentDataPath, "probes.txt"));

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Init()
    {
        SceneManager.sceneLoaded += (scene, mode) => { if (Probes) { CheckLeaks(scene.name); periodicDumps = 12; } Unload("scene " + scene.name); };
        PlayerEnterExit.OnTransitionDungeonInterior += args => Unload("dungeon");
        PlayerEnterExit.OnTransitionInterior += args => Unload("interior");
        PlayerEnterExit.OnTransitionExterior += args => Unload("exterior");
        Debug.Log(heapStepLog); // logged here: BeforeSceneLoad runs before DFU's log file is open
        CheckAudioThreadPin();
        var menuCollect = new GameObject("VitaMenuCollect");
        Object.DontDestroyOnLoad(menuCollect);
        menuCollect.AddComponent<MenuCollect>();
        if (DaggerfallUnity.Settings.VitaSyncGuard)
        {
            var guard = new GameObject("VitaSyncGuard");
            Object.DontDestroyOnLoad(guard);
            guard.AddComponent<SyncGuard>();
        }
        if (Probes)
        {
            Dump("startup");
            DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallVidPlayerWindow.OnVideoEnd += () => Dump("after video");
            int bufferLength, numBuffers;
            AudioSettings.GetDSPBufferSize(out bufferLength, out numBuffers);
            Debug.Log(string.Format("VITAAUDIO outputSampleRate={0} dspBuffer={1}x{2} speakerMode={3}", AudioSettings.outputSampleRate,
                bufferLength, numBuffers, AudioSettings.speakerMode));
            firstScene = SceneManager.GetActiveScene().name;
            CheckLeaks(firstScene); // first scene's sceneLoaded fired before Init
            var go = new GameObject("VitaHeapLog");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<HeapLog>();
        }
    }

    // Real Vita: Unity's sceHeaps grow in 1 MB mspaces, and sooner or later libc's mspace_create returned NULL for a new block,
    // which libheap doesn't check → mspace_malloc(NULL) crash.
    // SceLibc: mspace_create builds the mspace, then, if the mstate has USE_LOCK (bit 2,
    // copied from mparams.default_mflags = 7), creates a kernel LwMutex for it and returns NULL when that fails: the process is out
    // of kernel LwMutexes (IL2CPP, threads and audio use them too, hence the moving crash point). heap.c already locks its own
    // head->lwmtx around every mspace call, so the mspace's lock is redundant: clear USE_LOCK in mparams (libc data, 0x82297880:
    // magic 0x58585858 at +0, default_mflags at +20), so mspaces created from now on need no LwMutex. SceLibc's data segment moves with
    // the player binary (2017: 0x82296000, 2018: 0x825d6000), so it is found by module name: mparams = data + 0x1880.
    static int libcMparams;
    // Heap steps (4 then 16 MB) only delayed the crash and cost up to a step of slack per heap: back at Unity's 1 MB,
    // the write below is kept (heapStep) in case fewer, bigger blocks are wanted again.
    // Set the auto-extend size of Unity's general heaps both in the heap table (+20, used when a heap is created) and in each
    // created sceHeap header (+12, heap.c head->bsize).
    // 2017.4.2 release player: the heap table (PSP2MemoryTransferFMVMemToHeap's static init, 36-byte LowLevelHeap entries, +4 name,
    // +8 sceHeap header) is linked at 0x82353d98, and the data segment loads at 0x82300000 instead of 0x82289000 (next 1 MB after the
    // code) → 0x823cad98. Names are matched by pointer value (code isn't relocated) before anything else is read, so an unknown player
    // binary is left alone (see layouts below).
    // Per player binary (offsets into UnityPlayer's data segment, found by module name; the "Heap" name pointer identifies the binary,
    // HeapBig and HeapMalloc follow at +8/+16). 2017.4.2 release: SceHeaps::sm_Heaps data+0xcad98, m_AttachedThreads data+0x52b58,
    // Thread::mainThreadId data+0x4b3ec. 2018.2.19 release (SymbolFiles nm): sm_Heaps data+0xe3670; its ScopedThreadAttach attaches only
    // if !scripting_thread_is_attached() and its destructor never detaches, so audio threads stay attached and need no pin.
    struct PlayerLayout { public int heapTable, heapName, codeEnd, attachedThreads, mainThreadId; }
    static readonly PlayerLayout[] layouts =
    {
        new PlayerLayout { heapTable = 0xcad98, heapName = unchecked((int)0x8226dd10), codeEnd = unchecked((int)0x82300000), attachedThreads = 0x52b58, mainThreadId = 0x4b3ec },
        new PlayerLayout { heapTable = 0xe3670, heapName = unchecked((int)0x825b08b0), codeEnd = unchecked((int)0x82600000) },
    };
    const int heapStep = 1 << 20;
    static int heapTable, heapName, attachedThreads, mainThreadIdAddr;
    static uint codeEnd;
    static string heapStepLog;
    static bool heapTableKnown;

    // Blocks (mspaces), arena MB and step KB of every heap table entry (header +0x84 block count, +0x78 arena bytes, +12 step; layout read
    // read as strings while they point into the player's code (0x81000000-0x82300000)
    static string HeapBlocks()
    {
        if (!heapTableKnown)
            return "";
        var sb = new StringBuilder(" heaps:");
#if !UNITY_EDITOR
        int total = 0;
        for (int i = 0; i < 16; i++)
        {
            var entry = new IntPtr(heapTable + 36 * i);
            uint name = (uint)System.Runtime.InteropServices.Marshal.ReadInt32(entry, 4);
            if (name < 0x81000000u || name >= codeEnd)
                break;
            IntPtr head = System.Runtime.InteropServices.Marshal.ReadIntPtr(entry, 8);
            if (head == IntPtr.Zero)
            {
                sb.AppendFormat(" {0}=none", System.Runtime.InteropServices.Marshal.PtrToStringAnsi(new IntPtr((int)name)));
                continue;
            }
            int blocks = System.Runtime.InteropServices.Marshal.ReadInt32(head, 0x84);
            total += blocks;
            sb.AppendFormat(" {0}={1}/{2:F1}MB/{3}KB", System.Runtime.InteropServices.Marshal.PtrToStringAnsi(new IntPtr((int)name)), blocks,
                System.Runtime.InteropServices.Marshal.ReadInt32(head, 120) / 1048576.0, System.Runtime.InteropServices.Marshal.ReadInt32(head, 12) / 1024);
        }
        sb.AppendFormat(" total={0}", total);
#endif
        return sb.ToString();
    }

    // The method must exist in the editor compile too, or Unity never registers it (see the header): only the body is player-only
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void WidenHeapSteps()
    {
#if !UNITY_EDITOR
        int libcData = ModuleData("SceLibc"), playerData = ModuleData("UnityPlayer");
        foreach (var layout in layouts)
        {
            if (playerData == 0 || System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(playerData + layout.heapTable), 4) != layout.heapName)
                continue;
            heapTable = playerData + layout.heapTable;
            heapName = layout.heapName;
            codeEnd = (uint)layout.codeEnd;
            if (layout.mainThreadId != 0)
            {
                attachedThreads = playerData + layout.attachedThreads;
                mainThreadIdAddr = playerData + layout.mainThreadId;
            }
        }
        var sb = new StringBuilder("VITAHEAP mspace lock: ");
        if (libcData != 0)
            libcMparams = libcData + 0x1880;
        int magic = libcMparams == 0 ? 0 : System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(libcMparams), 0);
        int mflags = libcMparams == 0 ? 0 : System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(libcMparams), 20);
        if (magic == 0x58585858 && mflags == 7)
        {
            System.Runtime.InteropServices.Marshal.WriteInt32(new IntPtr(libcMparams), 20, mflags & ~2);
            sb.Append("off (default_mflags 7->5)");
        }
        else
            sb.AppendFormat("left alone (magic {0:x8} default_mflags {1})", magic, mflags);
        sb.Append(", heap steps:");
        for (int i = 0; i < 3; i++)   // Heap, HeapBig, HeapMalloc
        {
            var entry = new IntPtr(heapTable + 36 * i);
            int name = heapTable == 0 ? 0 : System.Runtime.InteropServices.Marshal.ReadInt32(entry, 4);
            if (heapTable == 0 || name != heapName + 8 * i)
            {
                sb.AppendFormat(" [{0}] name {1:x8}: unknown player, skipped", i, name);
                break;
            }
            heapTableKnown = true;
            if (System.Runtime.InteropServices.Marshal.ReadInt32(entry, 20) == 1 << 20)
                System.Runtime.InteropServices.Marshal.WriteInt32(entry, 20, heapStep);
            IntPtr head = System.Runtime.InteropServices.Marshal.ReadIntPtr(entry, 8);
            int old = head == IntPtr.Zero ? 0 : System.Runtime.InteropServices.Marshal.ReadInt32(head, 12);
            if (old == 1 << 20)
                System.Runtime.InteropServices.Marshal.WriteInt32(head, 12, heapStep);
            sb.AppendFormat(" [{0}] table {1}KB header {2}KB->{3}KB", i, System.Runtime.InteropServices.Marshal.ReadInt32(entry, 20) / 1024,
                old / 1024, head == IntPtr.Zero ? 0 : System.Runtime.InteropServices.Marshal.ReadInt32(head, 12) / 1024);
        }
        heapStepLog = sb.ToString() + HeapBlocks();
        // The 26 MB physically contiguous pool is reserved for the hardware video decoder; DFU decodes its VIDs in software, so hand it
        // to Unity's heap (call once, before any PSVitaVideoPlayer use; there is none). For full-size textures outside.
        heapStepLog += string.Format(" | video pool → heap: phycont free {0:F1} MB", Diagnostics.GetFreeMemoryPHYSCONT() / 1048576.0);
        PSVitaVideoPlayer.TransferMemToHeap();
        heapStepLog += string.Format(" → {0:F1} MB", Diagnostics.GetFreeMemoryPHYSCONT() / 1048576.0);
        // Vita tuning (was 16): 8 measured same build vs 16: city tour GC heap max 78.6 vs 82.6 MB, min free main
        // 69 vs 68 MB, collections -21%, loads -7%; castle walk equal. The ~110 MB heap crash above was the 2017.4 player at divisor 3.
        heapStepLog += " | " + SetGcFreeSpaceDivisor(8);
#endif
    }

#if !UNITY_EDITOR
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern int sceKernelGetModuleList(byte type, int[] uids, ref int num);
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern int sceKernelGetModuleInfo(int uid, byte[] info);

    // Data segment address of the first loaded module whose name starts with prefix, 0 if none
    static int ModuleData(string prefix)
    {
        var uids = new int[128];
        int count = uids.Length;
        sceKernelGetModuleList(0xff, uids, ref count);
        var info = new byte[0x1b8];
        for (int i = 0; i < count; i++)
        {
            Array.Clear(info, 0, info.Length);
            BitConverter.GetBytes(info.Length).CopyTo(info, 0);
            if (sceKernelGetModuleInfo(uids[i], info) >= 0 && Encoding.ASCII.GetString(info, 12, 28).TrimEnd('\0').StartsWith(prefix))
                return BitConverter.ToInt32(info, 0x154 + 0x18 + 8);   // segments[1].vaddr: data
        }
        return 0;
    }

    // Boehm grows the managed heap by heapsize / GC_free_space_divisor (3) plus what it needs, and the player's psp2_get_mem (memalign)
    // hits a breakpoint when that fails instead of trying a smaller step: in Daggerfall city (~110 MB heap) one ~37 MB request with 36 MB
    // free main = crash. A bigger divisor = smaller steps. The variable is in the IL2CPP module's
    // data segment at an offset that changes every build: vita/tools/pack_vpk.py writes it to StreamingAssets/VitaGC.txt. Written only if
    // the value there is Boehm's default 3.
    // An offset from VitaGC.txt (lines "<symbol> <hex offset>"), -1 if missing
    static int GcOffset(string symbol)
    {
        foreach (string l in System.IO.File.ReadAllLines(System.IO.Path.Combine(Application.streamingAssetsPath, "VitaGC.txt")))
        {
            string[] f = l.Trim().Split(' ');
            if (f.Length == 2 && f[0] == symbol)
                return Convert.ToInt32(f[1], 16);
        }
        return -1;
    }

    // Probes: Boehm's managed heap size and how much of it is wholly free blocks (what unmapping could give back at most), from GC_arrays
    static int gcArrays;            // address (above 2 GB on the Vita, so negative as an int: don't test its sign)
    static int gcArraysState;       // 0 not looked up, 1 found, -1 missing

    static bool FindGcArrays()
    {
        if (gcArraysState == 0)
        {
            int offset = GcOffset("GC_arrays");
            int data = offset < 0 ? 0 : ModuleData("LIBIL2CPP_PRX");
            gcArraysState = data == 0 ? -1 : 1;
            gcArrays = data + offset;
        }
        return gcArraysState > 0;
    }

    // Boehm's bytes allocated since the last collection (GC_arrays._bytes_allocd, word 8 in the player's gc_priv.h), -1 if unknown
    public static int GcBytesSinceCollect()
    {
        try
        {
            return FindGcArrays() ? System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(gcArrays + 32)) : -1;
        }
        catch (Exception)
        {
            gcArraysState = -1;
            return -1;
        }
    }

    static string GcHeap()
    {
        try
        {
            if (!FindGcArrays())
                return "";
            int heap = System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(gcArrays));
            int largeFree = System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(gcArrays + 16));
            return string.Format(" gcHeap={0:F1}MB gcFreeBlocks={1:F1}MB", heap / 1048576.0, largeFree / 1048576.0);
        }
        catch (Exception)
        {
            gcArraysState = -1;
            return "";
        }
    }

    static string SetGcFreeSpaceDivisor(int divisor)
    {
        try
        {
            int offset = GcOffset("GC_free_space_divisor");
            int data = ModuleData("LIBIL2CPP_PRX");
            if (data == 0)
                return "GC_free_space_divisor: LIBIL2CPP_PRX not found";
            var address = new IntPtr(data + offset);
            int old = System.Runtime.InteropServices.Marshal.ReadInt32(address);
            if (old != 3)
                return string.Format("GC_free_space_divisor at {0:x8} is {1}, left alone", address.ToInt32(), old);
            System.Runtime.InteropServices.Marshal.WriteInt32(address, divisor);
            return string.Format("GC_free_space_divisor at {0:x8} 3 -> {1}", address.ToInt32(), divisor);
        }
        catch (Exception e)
        {
            return "GC_free_space_divisor: " + e.Message;
        }
    }
#else
    public static int GcBytesSinceCollect() { return -1; }
#endif

    static long lastMark;
    static int periodicDumps = 4; // VITAMEM dumps left, one per 5 s: the first scene, then the first minute of each new scene
    static string firstScene;

    // Kernel free bytes per pool (exact; Profiler overcounts on Vita)
    public static string FreeMem()
    {
#if UNITY_EDITOR
        return "";
#else
        return string.Format("free main={0:F1}MB cdram={1:F1}MB phycont={2:F1}MB", Diagnostics.GetFreeMemoryLPDDR() / 1048576.0,
            Diagnostics.GetFreeMemoryCDRAM() / 1048576.0, Diagnostics.GetFreeMemoryPHYSCONT() / 1048576.0) + HeapBlocks() + SyncFree() +
            string.Format(" gcHeap={0:F1}MB gcUsed={1:F1}MB", il2cpp_gc_get_heap_size() / 1048576.0, il2cpp_gc_get_used_size() / 1048576.0);
#endif
    }

#if !UNITY_EDITOR
    // Boehm heap reserve / live bytes (Profiler.GetMonoHeapSizeLong reads 0 in release players)
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern long il2cpp_gc_get_heap_size();
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern long il2cpp_gc_get_used_size();
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern int sceKernelCreateLwMutex(IntPtr work, string name, uint attr, int initCount, IntPtr opt);
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern int sceKernelDeleteLwMutex(IntPtr work);
    [System.Runtime.InteropServices.DllImport("__Internal")]
    static extern int sceKernelGetThreadId();

    // Free kernel sync objects (2048 per process, shared by semaphores, lwmutexes, event flags...): creates lwmutexes until one fails
    // or SyncProbeMax, then deletes them. When none are left, Unity's pthread_mutex_init fails (sce error 8002450d in the TTY) and
    // pthread_mutex_lock on that mutex returns without locking. Capped so the probe itself rarely takes the last ones.
    const int SyncProbeMax = 256;
    static IntPtr syncWork;
    static int syncLast = SyncProbeMax;
    static string SyncFree()
    {
        int n = SyncCount(SyncProbeMax);
        return n < SyncProbeMax ? " sync free=" + n : " sync free>=" + n;
    }

    static int SyncCount(int max)
    {
        if (syncWork == IntPtr.Zero)
            syncWork = System.Runtime.InteropServices.Marshal.AllocHGlobal(SyncProbeMax * 32 + 8);
        IntPtr first = new IntPtr((syncWork.ToInt64() + 7) & ~7L);   // SceKernelLwMutexWork: 32 bytes, 8-aligned
        int n = 0;
        while (n < max && sceKernelCreateLwMutex(new IntPtr(first.ToInt64() + n * 32), "VitaSyncProbe", 0, 0, IntPtr.Zero) >= 0)
            n++;
        for (int i = 0; i < n; i++)
            sceKernelDeleteLwMutex(new IntPtr(first.ToInt64() + i * 32));
        syncLast = n;
        return n;
    }

    // No sync objects left: do garbage objects hold them (free again after a full GC + finalizers)? With UserData/syncdump.txt, crash
    // on purpose while they are gone: the core dump names every semaphore/lwmutex and thread at that moment.
    static void SyncExhausted()
    {
        System.GC.Collect();
        System.GC.WaitForPendingFinalizers();
        Debug.Log("VITASYNC none free; after GC + finalizers:" + SyncFree());
        if (System.IO.File.Exists(System.IO.Path.Combine(DaggerfallUnityApplication.PersistentDataPath, "syncdump.txt")))
        {
            Debug.Log("VITASYNC crashing on purpose for a core dump (syncdump.txt)");
            System.Runtime.InteropServices.Marshal.WriteInt32(new IntPtr(8), 0);
        }
    }
#endif

    static readonly List<KeyValuePair<string, WeakReference>> watched = new List<KeyValuePair<string, WeakReference>>();

    // Report which of the previous scene's objects survived this scene load, then watch this scene's
    static void CheckLeaks(string sceneName)
    {
        if (watched.Count > 0)
        {
            // Objects the new scene still uses (the shared ContentReader, DaggerfallUnity.vitaSharedReader) must not be severed: nulling
            // its fields broke the live reader (HasLocation crashed at game start whenever probes were on)
            var dfuNow = Object.FindObjectOfType<DaggerfallUnity>();
            var inUse = dfuNow ? new object[] { dfuNow.MeshReader, dfuNow.MaterialReader, dfuNow.SoundReader, dfuNow.ContentReader,
                dfuNow.WorldTime, dfuNow.TextProvider } : new object[0];
            foreach (var w in watched)
                if (!inUse.Any(u => ReferenceEquals(u, w.Value.Target)))
                    Sever(w.Value.Target);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            foreach (var w in watched.Where(w => w.Value.IsAlive))
                Debug.Log(string.Format("VITALEAK {0} alive (now in scene {1}){2}", w.Key, sceneName, StaticHolders(w.Value.Target)));
            foreach (var gm in Object.FindObjectsOfType<GameManager>())
            {
                // private field, not the StateManager property: that one creates a StateManager (and fires OnStateChange)
                var sm = typeof(GameManager).GetField("stateManager", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(gm) as StateManager;
                Debug.Log(string.Format("VITALEAK GameManager in scene '{0}' state={1}", gm.gameObject.scene.name, sm != null ? sm.CurrentState.ToString() : "(no StateManager yet)"));
            }
            Debug.Log(string.Format("VITALEAK {0} of {1} watched objects freed", watched.Count(w => !w.Value.IsAlive), watched.Count));
            watched.Clear();
        }
        // FindObjectOfType, not .Instance: the Instance getters create the object when it's missing
        var dfu = Object.FindObjectOfType<DaggerfallUnity>();
        if (dfu)
        {
            Watch("DaggerfallUnity", dfu);
            Watch("MeshReader", dfu.MeshReader);
            Watch("MaterialReader", dfu.MaterialReader);
            Watch("SoundReader", dfu.SoundReader);
            Watch("ContentReader", dfu.ContentReader);
            Watch("WorldTime", dfu.WorldTime);
            Watch("TextProvider", dfu.TextProvider);
        }
        Watch("DaggerfallUI", Object.FindObjectOfType<DaggerfallUI>());
        Watch("InputManager", Object.FindObjectOfType<InputManager>());
        Watch("TextManager", Object.FindObjectOfType<TextManager>());
        WatchUI();
        foreach (var song in Object.FindObjectsOfType<DaggerfallSongPlayer>())
            Watch("DaggerfallSongPlayer '" + song.name + "'", song);
    }

    // Null every reference field (the object is already destroyed/unused) so it no longer keeps the other watched objects alive
    static void Sever(object o)
    {
        for (var t = o != null ? o.GetType() : null; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                if (!f.FieldType.IsValueType)
                    try { f.SetValue(o, null); } catch (Exception) { }
    }

    // Persistent windows appear after the first UI message, so this also runs from HeapLog while in the first scene
    static void WatchUI()
    {
        var ui = Object.FindObjectOfType<DaggerfallUI>();
        if (!ui)
            return;
        WatchFields("DaggerfallUI", ui);
        WatchFields("UIManager", ui.UserInterfaceManager);
    }

    // Watch each plain C# object a field references, and the items of collection fields (window stack, lists)
    static void WatchFields(string path, object o)
    {
        for (var t = o != null ? o.GetType() : null; t != null && t != typeof(MonoBehaviour) && t != typeof(object); t = t.BaseType)
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (f.FieldType.IsValueType)
                    continue;
                object v;
                try { v = f.GetValue(o); } catch (Exception) { continue; }
                if (v == null || v is string || v is Object)
                    continue;
                Watch(path + "." + f.Name + " " + v.GetType().Name, v);
                var items = v as System.Collections.IEnumerable;
                if (items == null || v is System.Collections.IDictionary || v is Array)
                    continue;
                int i = 0;
                foreach (var item in items)
                {
                    if (item != null && !(item is string) && !(item is Object) && !item.GetType().IsValueType)
                        Watch(path + "." + f.Name + "[" + i + "] " + item.GetType().Name, item);
                    if (++i >= 32)
                        break;
                }
            }
    }

    // Static fields and static event invocation lists in game code that point at o, directly or through one object
    // (a closure, a list, or any object whose fields/event handlers reference o)
    static string StaticHolders(object o)
    {
        var sb = new StringBuilder();
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var t in typeof(DaggerfallUnity).Assembly.GetTypes())
        {
            if (t.IsGenericTypeDefinition)
                continue;
            foreach (var f in t.GetFields(flags))
            {
                if (f.FieldType.IsValueType || f.IsLiteral)
                    continue;
                object v;
                try { v = f.GetValue(null); } catch (Exception) { continue; }
                if (v == null)
                    continue;
                string via = Via(v, o);
                if (via != null)
                    sb.AppendFormat(" held-by {0}.{1}{2}", t.Name, f.Name, via);
                else
                    foreach (var inner in Children(v))
                    {
                        via = Via(inner, o);
                        if (via != null)
                            sb.AppendFormat(" held-by {0}.{1} -> {2}{3}", t.Name, f.Name, inner.GetType().Name, via);
                    }
            }
        }
        return sb.ToString();
    }

    // "" if v is o, " (handler)" if v is a delegate targeting o, null otherwise
    static string Via(object v, object o)
    {
        if (ReferenceEquals(v, o))
            return "";
        var d = v as Delegate;
        if (d != null && d.GetInvocationList().Any(h => ReferenceEquals(h.Target, o)))
            return " (handler)";
        return null;
    }

    // Objects v points to: delegate targets, collection items, reference fields (incl. event backing fields)
    static IEnumerable<object> Children(object v)
    {
        var d = v as Delegate;
        if (d != null)
        {
            foreach (var h in d.GetInvocationList())
                if (h.Target != null)
                    yield return h.Target;
            yield break;
        }
        if (v is string || v is Type || (v is Object && !(v is MonoBehaviour)))
            yield break;
        var items = v as System.Collections.IEnumerable;
        if (items != null)
        {
            int i = 0;
            foreach (var item in items)
            {
                if (item != null && !item.GetType().IsValueType)
                    yield return item;
                if (++i >= 4096)
                    break;
            }
        }
        for (var t = v.GetType(); t != null && t != typeof(object) && t != typeof(MonoBehaviour); t = t.BaseType)
            foreach (var f in t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
            {
                if (f.FieldType.IsValueType)
                    continue;
                object c = null;
                try { c = f.GetValue(v); } catch (Exception) { }
                if (c != null)
                    yield return c;
            }
    }

    // Sizes of the caches that live for the whole game
    static void LogCaches()
    {
        var sb = new StringBuilder("VITACACHE");
        const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        Func<Type, string, object, object> get = (t, name, o) =>
        {
            var f = t.GetField(name, any);
            try { return f != null ? f.GetValue(o) : null; } catch (Exception) { return null; }
        };
        Action<string, object> count = (label, c) => sb.AppendFormat(" {0}={1}", label, c is System.Collections.ICollection ? ((System.Collections.ICollection)c).Count.ToString() : "?");
        Type wdr = typeof(DaggerfallWorkshop.Utility.AssetInjection.WorldDataReplacement);
        count("wdrBlocks", get(wdr, "blocks", null));
        count("wdrLocations", get(wdr, "locations", null));
        count("wdrBuildings", get(wdr, "buildings", null));
        count("wdrRegions", get(wdr, "regions", null));
        Type fp = typeof(DaggerfallConnect.Utility.FileProxy);
        sb.AppendFormat(" fileCacheKB={0} levelCacheKB={1}", Convert.ToInt32(get(fp, "vitaFileCacheBytes", null) ?? 0) / 1024,
            Convert.ToInt32(get(fp, "vitaLevelCacheBytes", null) ?? 0) / 1024);
        count("textureVitaRecords", get(typeof(DaggerfallConnect.Arena2.TextureFile), "vitaRecords", null));
        count("cityMeshes", get(typeof(VitaAtlas), "cityMeshes", null));
        count("cullingTowns", get(typeof(VitaCulling), "towns", null));
        var dfu = Object.FindObjectOfType<DaggerfallUnity>();
        if (dfu)
        {
            var models = get(typeof(MeshReader), "modelDict", dfu.MeshReader) as Dictionary<int, ModelData>;
            int verts = 0;
            if (models != null)
                foreach (ModelData m in models.Values)
                    verts += m.Vertices != null ? m.Vertices.Length : 0;
            sb.AppendFormat(" meshReaderModels={0} ({1} verts)", models != null ? models.Count : -1, verts);
            count("materials", get(typeof(MaterialReader), "materialDict", dfu.MaterialReader));
            var blocksFile = dfu.ContentReader != null ? dfu.ContentReader.BlockFileReader : null;
            var records = blocksFile != null ? get(blocksFile.GetType(), "blocks", blocksFile) as Array : null;
            int parsed = 0;
            long bytes = 0;
            if (records != null)
                foreach (object r in records)
                {
                    var proxy = get(r.GetType(), "MemoryFile", r) as DaggerfallConnect.Utility.FileProxy;
                    if (proxy == null)
                        continue;
                    parsed++;
                    bytes += proxy.Length;
                }
            sb.AppendFormat(" bsaBlocks={0} ({1} KB raw)", parsed, bytes / 1024);
        }
        Debug.Log(sb.ToString());
    }

    static void Watch(string name, object o)
    {
        if (o != null && !watched.Any(w => ReferenceEquals(w.Value.Target, o)))
            watched.Add(new KeyValuePair<string, WeakReference>(name, new WeakReference(o)));
    }

    // Probes: Boehm heap size / wholly free blocks for other log lines (VITASAVE), "" otherwise
    public static string GcHeapForLog()
    {
#if UNITY_EDITOR
        return "";
#else
        return Probes ? GcHeap() : "";
#endif
    }

    // Development builds: live C# memory (after a full GC) at a load step, and the change since the previous mark
    public static void Mark(string step)
    {
        if (!Probes)
            return;
        long live = System.GC.GetTotalMemory(true);
        Debug.Log(string.Format("VITAHEAP mark '{0}' live={1:F1}MB delta={2:+0.0;-0.0}MB{3} {4}", step, live / 1048576.0, (live - lastMark) / 1048576.0, 
#if UNITY_EDITOR
            "",
#else
            GcHeap(),
#endif
            FreeMem()));
        LogCaches();
        if (step == "before ARCH3D") // who loads 3D models (and world textures) on the startup screen?
            Debug.Log("VITAHEAP ARCH3D caller:\n" + System.Environment.StackTrace);
        lastMark = live;
    }

    // Load-time collection: Boehm takes multi-MB arrays (atlas data, terrain job arrays, pack records) from its large-object path,
    // which grows the heap when no free block fits instead of collecting: the Daggerfall city load grew it 90 -> 134 MB with one GC
    //, and the next 5.6 MB texture allocation failed. While the loading screen is up,
    // collect whenever 8 MB has been allocated since the last collection, so the garbage is reused instead of the heap growing.
    static long loadCollectLive;
    public static int LoadCollections;
    public static void LoadCollect()
    {
        if (!VitaLoadingWindow.Showing)
            return;
        if (System.GC.GetTotalMemory(false) - loadCollectLive < (8L << 20))
            return;
        System.GC.Collect();
        loadCollectLive = System.GC.GetTotalMemory(false);
        LoadCollections++;
    }

    // Collections where nobody sees them (no gameplay change). Boehm's full collection is a 120-180 ms stop (it scans the
    // whole live heap), and in play it comes every 20-140 s wherever the garbage tips it over. A paused menu (inventory, map, spellbook,
    // rest, talk) or the end of a loading screen already holds the picture still, so collect there once enough garbage has built up:
    // the collector's count restarts at 0 and the next stop in play moves out by the same amount. Only timing changes, never what lives.
    public const int IdleCollectMinBytes = 512 << 10;
    public static int IdleCollections;
    public static bool IdleCollectOff;  // test harness A/B
    public static bool IdleCollect(string where)
    {
        if (IdleCollectOff)
            return false;
        int bytes = GcBytesSinceCollect();
        if (bytes >= 0 && bytes < IdleCollectMinBytes)
            return false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        System.GC.Collect();
        IdleCollections++;
        if (Probes)
            Debug.Log(string.Format("VITAIDLEGC {0} t={1:F1}s garbage {2} KB, {3} ms, #{4}", where, Time.realtimeSinceStartup, bytes >> 10,
                sw.ElapsedMilliseconds, IdleCollections));
        return true;
    }

    // Once per paused window, on its second drawn frame (the first shows it before the stop). Not the loading screen (it collects
    // when it closes) or videos (a stop would show as a skipped frame there).
    class MenuCollect : MonoBehaviour
    {
        object window;
        int framesShown;
        void LateUpdate()
        {
            if (!GameManager.HasInstance || !GameManager.IsGamePaused || DaggerfallUI.Instance == null)
            {
                window = null;
                return;
            }
            DaggerfallWorkshop.Game.UserInterface.IUserInterfaceWindow top = DaggerfallUI.Instance.UserInterfaceManager.TopWindow;
            if (top == null || top is VitaLoadingWindow || top is DaggerfallWorkshop.Game.UserInterfaceWindows.DaggerfallVidPlayerWindow)
                return;
            if (top != window)
            {
                window = top;
                framesShown = 0;
            }
            if (++framesShown == 2)
                IdleCollect(top.GetType().Name);
        }
    }

    /// <summary>Free main memory (LPDDR) in bytes; long.MaxValue in the editor.</summary>
    public static long FreeMain()
    {
#if !UNITY_EDITOR
        return (long)Diagnostics.GetFreeMemoryLPDDR();
#else
        return long.MaxValue;
#endif
    }

    // Probes: cheap per-step trace of a load (no forced GC): live+garbage C# bytes, GCs so far, and the change in free system memory
    // (main + phycont: the heap also takes the video pool) since the previous step. Where free memory drops = where the pools grew.
    static long lastStepFree, lastStepGc, lastStepCdram;
    public static void Step(string step)
    {
        LoadCollect();
#if !UNITY_EDITOR
        if (!Probes)
            return;
        long free = (long)Diagnostics.GetFreeMemoryLPDDR() + (long)Diagnostics.GetFreeMemoryPHYSCONT();
        long gc = System.GC.GetTotalMemory(false);
        long cdram = (long)Diagnostics.GetFreeMemoryCDRAM();
        Debug.Log(string.Format("VITASTEP {0} gc={1:F1}MB ({2:+0.0;-0.0}) gcs={3} free={4:F1}MB ({5:+0.0;-0.0}) cdram={6:F1}MB ({7:+0.0;-0.0})", step, gc / 1048576.0,
            (gc - lastStepGc) / 1048576.0, System.GC.CollectionCount(0), free / 1048576.0, (free - lastStepFree) / 1048576.0,
            cdram / 1048576.0, (cdram - lastStepCdram) / 1048576.0) + SyncFree() + GcHeap());
        lastStepFree = free;
        lastStepGc = gc;
        lastStepCdram = cdram;
#endif
    }

    // and the silent streamed clip's PCM reader) run on FMOD threads inside the player's ScopedThreadAttach: it attaches the thread to
    // IL2CPP, and when its refcount (m_AttachedThreads entry {thread id, scripting thread, count}) drops back to 0 it detaches it again.
    // Every attach builds IL2CPP thread objects whose kernel semaphores come back only when the GC collects them, so the process runs
    // out of sync objects within seconds outside and
    // Unity's pthread locks fail open (8002450d). Pin: the first callback on a thread adds one count to its own entry, so it stays
    // attached and later callbacks attach nothing. 2017.4.2 release player (disassembly of SymbolFiles): m_AttachedThreads linked at
    // 0x822dbb58 (a pointer to {data, ?, size, capacity}), Thread::mainThreadId at 0x822d43ec; the data segment loads 0x77000 higher
    // (see heapTable; addresses now from the layouts table). Only enabled when mainThreadId there equals the main thread's id.
    static bool pinAudioThreads;
    [ThreadStatic] static bool threadPinChecked;
    static int pinnedThreads;

    static void CheckAudioThreadPin()
    {
#if !UNITY_EDITOR
        if (!DaggerfallUnity.Settings.VitaPinAudioThread)
            return;
        if (mainThreadIdAddr == 0)
        {
            Debug.Log("VITASYNC audio thread pin: off, " + (heapTable != 0 ? "not needed (this player keeps audio threads attached)" : "unknown player"));
            return;
        }
        int stored = System.Runtime.InteropServices.Marshal.ReadInt32(new IntPtr(mainThreadIdAddr)), mine = sceKernelGetThreadId();
        pinAudioThreads = stored == mine;
        Debug.Log(string.Format("VITASYNC audio thread pin: {0} (mainThreadId {1:x8}, this thread {2:x8})",
            pinAudioThreads ? "on" : "off, unknown player", stored, mine));
#endif
    }

    /// <summary>Call first thing in every C# audio callback (FMOD threads): keeps that thread attached to IL2CPP (see above).</summary>
    public static void PinAudioThread()
    {
#if !UNITY_EDITOR
        if (!pinAudioThreads || threadPinChecked)
            return;
        threadPinChecked = true;    // per attach: a thread that got pinned keeps it, one that didn't tries again on its next attach
        IntPtr vector = System.Runtime.InteropServices.Marshal.ReadIntPtr(new IntPtr(attachedThreads));
        if (vector == IntPtr.Zero)
            return;
        IntPtr data = System.Runtime.InteropServices.Marshal.ReadIntPtr(vector, 0);
        int size = System.Runtime.InteropServices.Marshal.ReadInt32(vector, 8), tid = sceKernelGetThreadId();
        for (int i = 0; i < size && i < 64; i++)
        {
            var entry = new IntPtr(data.ToInt64() + 12 * i);
            if (System.Runtime.InteropServices.Marshal.ReadInt32(entry, 0) != tid)
                continue;
            if (System.Runtime.InteropServices.Marshal.ReadInt32(entry, 8) == 1)
            {
                System.Runtime.InteropServices.Marshal.WriteInt32(entry, 8, 2);
                System.Threading.Interlocked.Increment(ref pinnedThreads);
            }
            return;
        }
#endif
    }

    // doesn't hold): the process runs out of kernel sync objects within seconds between GCs
    // synth's OnAudioFilterRead), and then Unity's pthread locks fail open (8002450d), a likely cause of freezes.
    // Every 0.25 s count free ones up to GuardMin; below it, collect the dead thread objects that hold them.
    class SyncGuard : MonoBehaviour
    {
        const int GuardMin = 128;   // > what the bursts take in 0.25 s
        float next;
        void Update()
        {
#if !UNITY_EDITOR
            if (Time.realtimeSinceStartup < next)
                return;
            next = Time.realtimeSinceStartup + 0.25f;
            int before = SyncCount(GuardMin);
            if (before >= GuardMin)
                return;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            System.GC.Collect();
            System.GC.WaitForPendingFinalizers();
            if (Probes)
                Debug.Log(string.Format("VITASYNC guard t={0:F1}s free {1} -> GC {2} ms -> free {3}", Time.realtimeSinceStartup, before,
                    sw.ElapsedMilliseconds, SyncCount(GuardMin)));
#endif
        }
    }

    // Every 5 s: is the C# heap growing because live data grows (leak) or because the GC doesn't run?
    class HeapLog : MonoBehaviour
    {
        float next;
        void Update()
        {
            if (Time.realtimeSinceStartup < next)
                return;
            next = Time.realtimeSinceStartup + 5;
            if (SceneManager.GetActiveScene().name == firstScene)
                WatchUI();
            if (periodicDumps > 0)
            {
                periodicDumps--;
                Dump(SceneManager.GetActiveScene().name + " t=" + Time.realtimeSinceStartup.ToString("F0") + "s"); // what fills VRAM (intro videos, main menu)
            }
            if (pinAudioThreads)
                Debug.Log("VITASYNC pinned audio threads " + pinnedThreads);
            Debug.Log(string.Format("VITAHEAP t={0:F0}s heap={1:F1}MB used={2:F1}MB gcTotal={3:F1}MB collections={4} frame={5} {6}",
                Time.realtimeSinceStartup, Profiler.GetMonoHeapSizeLong() / 1048576.0, Profiler.GetMonoUsedSizeLong() / 1048576.0,
                System.GC.GetTotalMemory(false) / 1048576.0, System.GC.CollectionCount(0), Time.frameCount, FreeMem()));
#if !UNITY_EDITOR
            if (syncLast == 0)
                SyncExhausted();
#endif
        }
    }

    // Development builds: every Daggerfall file DFU opens (FileProxy.Load), after the open/read, with its size and the time it took, so a log
    // shows what loaded right before a spike or crash and what loads cost. (No stat first: that would be one more ~90 ms ARENA2 lookup
    public static void LogFile(string path, DaggerfallConnect.FileUsage usage, long bytes, double ms)
    {
        if (!Probes)
            return;
        Debug.Log(string.Format("VITAFILE t={0:F1}s {1} {2} {3}KB {4:F1}ms", Time.realtimeSinceStartup, System.IO.Path.GetFileName(path), usage, bytes / 1024, ms));
    }

    static readonly Dictionary<string, int> createdTextures = new Dictionary<string, int>();
    static int createdTotal, createdReadable;

    // Development builds: every Daggerfall texture TextureReader creates (archive_record_frame), reported per transition
    public static void CountTexture(int archive, int record, int frame, bool readable)
    {
        if (!Probes)
            return;
        string key = archive + "_" + record + "_" + frame;
        int n;
        createdTextures.TryGetValue(key, out n);
        createdTextures[key] = n + 1;
        createdTotal++;
        if (readable)
            createdReadable++;
    }

    static void ReportTextures(string tag)
    {
        var sb = new StringBuilder();
        sb.AppendFormat("VITATEX [{0}] created={1} unique={2} readable={3}\n", tag, createdTotal, createdTextures.Count, createdReadable);
        foreach (var a in createdTextures.GroupBy(kv => kv.Key.Substring(0, kv.Key.IndexOf('_')))
            .Select(g => new { archive = g.Key, total = g.Sum(kv => kv.Value), unique = g.Count() }).OrderByDescending(a => a.total).Take(15))
            sb.AppendFormat("VITATEX   archive {0}: created {1}, unique {2}\n", a.archive, a.total, a.unique);
        foreach (var kv in createdTextures.Where(kv => kv.Value > 1).OrderByDescending(kv => kv.Value).Take(15))
            sb.AppendFormat("VITATEX   duplicate {0} x{1}\n", kv.Key, kv.Value);
        Debug.Log(sb.ToString());
        createdTextures.Clear();
        createdTotal = createdReadable = 0;
    }

    internal static void Unload(string tag)
    {
        ActiveGameObjectDatabase.VitaClearEnemyList();
        if (Probes)
            ReportTextures(tag);
        Mark(tag);
        if (Probes)
            Dump(tag + " before unload");
        Resources.UnloadUnusedAssets().completed += op => { if (Probes) Dump(tag); };
    }

    public static void Dump(string tag)
    {
        var textures = Resources.FindObjectsOfTypeAll<Texture>();
        var groups = textures.GroupBy(Describe)
            .Select(g => new { key = g.Key, count = g.Count(), bytes = g.Sum(t => EstimateBytes(t)), profiler = g.Sum(t => Profiler.GetRuntimeMemorySizeLong(t)) })
            .OrderByDescending(g => g.bytes).ToArray();
        var sb = new StringBuilder();
        sb.AppendFormat("VITAMEM [{0}] screen={1}x{2} quality={3} {4}\n", tag, Screen.width, Screen.height, QualitySettings.names[QualitySettings.GetQualityLevel()], FreeMem());
        sb.AppendFormat("VITAMEM [{0}] Texture: count={1} estimated={2:F1}MB profiler={3:F1}MB\n", tag, textures.Length,
            groups.Sum(g => g.bytes) / 1048576.0, textures.Sum(t => Profiler.GetRuntimeMemorySizeLong(t)) / 1048576.0);
        foreach (var g in groups.Take(25))
            sb.AppendFormat("VITAMEM   {0,8:F0}KB (profiler {1,8:F0}KB) {2,4}x {3}\n", g.bytes / 1024.0, g.profiler / 1024.0, g.count, g.key);
        // Who renders each big unnamed texture
        var big = textures.Where(t => t is Texture2D && t.name.Length == 0 && EstimateBytes(t) >= 1048576).ToList();
        if (big.Count > 0)
        {
            var users = new Dictionary<Texture, string>();
            foreach (var r in Resources.FindObjectsOfTypeAll<Renderer>())
                foreach (var m in r.sharedMaterials)
                    if (m && m.HasProperty("_MainTex") && m.mainTexture && !users.ContainsKey(m.mainTexture))   // mainTexture logs a Unity error without it
                        users[m.mainTexture] = "material '" + m.name + "' shader " + m.shader.name + " on " + OwnerPath(r.transform);
            foreach (var t in big)
                sb.AppendFormat("VITAMEM   owner {0} <- {1}\n", Describe(t), users.ContainsKey(t) ? users[t] : "(no renderer)");
        }
        var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
        sb.AppendFormat("VITAMEM [{0}] Mesh: count={1} verts={2} profiler={3:F1}MB\n", tag, meshes.Length,
            meshes.Sum(m => (long)m.vertexCount), meshes.Sum(m => Profiler.GetRuntimeMemorySizeLong(m)) / 1048576.0);
        sb.AppendFormat("VITAMEM [{0}] billboard batches={1}\n", tag, Resources.FindObjectsOfTypeAll<DaggerfallWorkshop.DaggerfallBillboardBatch>().Length);
        // After a transition's unload: who still holds meshes
        if (tag.Contains("unloaded"))
        {
            var owner = new Dictionary<Mesh, string>();
            foreach (var f in Resources.FindObjectsOfTypeAll<MeshFilter>())
                if (f.sharedMesh && !owner.ContainsKey(f.sharedMesh))
                    owner[f.sharedMesh] = OwnerPath(f.transform);
            foreach (var g in meshes.GroupBy(m => (m.name.Length > 0 ? m.name : "(unnamed)") + " <- " + (owner.ContainsKey(m) ? owner[m] : "(no MeshFilter)"))
                .OrderByDescending(g => g.Count()).Take(15))
                sb.AppendFormat("VITAMEM   mesh {0,5}x {1,7} verts {2}\n", g.Count(), g.Sum(m => m.vertexCount), g.Key);
        }
        Debug.Log(sb.ToString());
    }

    // Root and the first two levels below it (DungeonParent/<dungeon>/...), active state of the root
    static string OwnerPath(Transform t)
    {
        var parts = new List<string>();
        for (Transform p = t; p != null; p = p.parent)
            parts.Insert(0, p.name);
        Transform root = t.root;
        return string.Join("/", parts.Take(3).ToArray()) + (root.gameObject.activeInHierarchy ? "" : " (inactive)");
    }

    static string Describe(Texture t)
    {
        var t2 = t as Texture2D;
        var rt = t as RenderTexture;
        string format = t2 ? t2.format.ToString() : rt ? rt.format + " depth" + rt.depth + (rt.IsCreated() ? "" : " (not created)") : "";
        int mips = t2 ? t2.mipmapCount : 1;
        return string.Format("{0} {1}x{2} {3}{4} '{5}'", t.GetType().Name, t.width, t.height, format, mips > 1 ? " mips" : "",
            t.name.Length > 0 ? t.name : "");
    }

    static long EstimateBytes(Texture t)
    {
        var t2 = t as Texture2D;
        var rt = t as RenderTexture;
        if (rt && !rt.IsCreated())
            return 0; // no GPU memory until first use
        string format = t2 ? t2.format.ToString() : rt ? rt.format.ToString() : "";
        double bpp;
        switch (format)
        {
            case "Alpha8": case "R8": bpp = 1; break;
            case "R16": case "RGB565": case "ARGB4444": case "RGBA4444": case "RHalf": bpp = 2; break;
            case "RGB24": bpp = 3; break;
            case "DXT1": case "ETC_RGB4": case "PVRTC_RGB4": case "PVRTC_RGBA4": bpp = 0.5; break;
            case "DXT5": bpp = 1; break;
            case "RGBAUShort": case "ARGBHalf": case "RGBAHalf": bpp = 8; break;
            case "ARGBFloat": case "RGBAFloat": bpp = 16; break;
            default: bpp = 4; break;
        }
        double pixels = (double)t.width * t.height;
        double bytes = pixels * bpp * (t2 && t2.mipmapCount > 1 ? 4.0 / 3.0 : 1.0);
        if (rt)
            bytes += pixels * rt.depth / 8.0;
        return (long)bytes;
    }
}
#endif
