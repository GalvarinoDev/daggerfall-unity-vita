// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    Lypyl (lypyldf@gmail.com)
// 
// Notes:
//

using UnityEngine;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using FullSerializer;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Questing;
using DaggerfallWorkshop.Game.Banking;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Player;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Utility.AssetInjection;

namespace DaggerfallWorkshop.Game.Serialization
{
    /// <summary>
    /// Implements save/load logic.
    /// Games are saved in PersistentDataPath\Saves.
    /// Each save game will have a screenshot and multiple files.
    /// </summary>
    public class SaveLoadManager : MonoBehaviour
    {
        #region Fields

        const int latestSaveVersion = 1;

        const string rootSaveFolder = "Saves";
        const string savePrefix = "SAVE";
        const string quickSaveName = "QuickSave";
        const string autoSaveName = "AutoSave";
        const string saveInfoFilename = "SaveInfo.txt";
        const string saveDataFilename = "SaveData.txt";
        const string factionDataFilename = "FactionData.txt";
        const string containerDataFilename = "ContainerData.txt";
        const string questDataFilename = "QuestData.txt";
        const string discoveryDataFilename = "DiscoveryData.txt";
        const string conversationDataFilename = "ConversationData.txt";
        const string notebookDataFilename = "NotebookData.txt";
        const string worldVariationDataFilename = "WorldVariationData.txt";
        const string automapDataFilename = "AutomapData.txt";
        const string questExceptionsFilename = "QuestExceptions.txt";
        const string screenshotFilename = "Screenshot.jpg";
#if UNITY_PSP2
        const string vitaThumbFilename = "Screenshot.vita";     // VitaSaveThumb: raw half-size RGB24, read with no JPG decode

        // The loading window of the save being loaded: LoadGame keeps it up until the world is built (the dungeon builds frames after
        // Load returns), so StartGameBehaviour leaves this one open
        public static VitaLoadingWindow VitaLoadWindow;
#endif
        const string bioFileName = "bio.txt";
        const string notReadyExceptionText = "SaveLoad not ready.";

        // Serializable state manager for stateful game objects
        SerializableStateManager stateManager = new SerializableStateManager();

        // Enumerated save info
        Dictionary<int, string> enumeratedSaveFolders = new Dictionary<int, string>();
        Dictionary<int, SaveInfo_v1> enumeratedSaveInfo = new Dictionary<int, SaveInfo_v1>();
#if UNITY_PSP2
        int vitaSavesListedAt = -1;
#endif
        Dictionary<string, List<int>> enumeratedCharacterSaves = new Dictionary<string, List<int>>();

        string unitySavePath = string.Empty;
        string daggerfallSavePath = string.Empty;
        bool loadInProgress = false;

        string saveDataJsonCache;
        SaveData_v1 saveDataCache;

        #endregion

        #region Properties

        public static SerializableStateManager StateManager
        {
            get { return Instance.stateManager; }
        }

        public int LatestSaveVersion
        {
            get { return latestSaveVersion; }
        }

        public string UnitySavePath
        {
            get { return GetUnitySavePath(); }
        }

        public string DaggerfallSavePath
        {
            get { return GetDaggerfallSavePath(); }
        }

        public int CharacterCount
        {
            get { return enumeratedCharacterSaves.Count; }
        }

        public string[] CharacterNames
        {
            get { return GetCharacterNames(); }
        }

        public bool LoadInProgress
        {
            get { return loadInProgress; }
        }

        public bool IsSavingPrevented
        {
            get { return PreventSaveConditions.Exists(p => p()); }
        }

        #endregion

        #region Singleton

        static SaveLoadManager instance = null;
        public static SaveLoadManager Instance
        {
            get
            {
                if (instance == null)
                {
                    if (!FindSingleton(out instance))
                        return null;
                }
                return instance;
            }
        }

        public static bool HasInstance
        {
            get { return (instance != null); }
        }

        #endregion

        #region Unity

        void Awake()
        {
            sceneUnloaded = false;
        }

        void Start()
        {
            SetupSingleton();

            // Init classic game startup time at startup
            // This will also be modified when deserializing save game data
            DaggerfallUnity.Instance.WorldTime.Now.SetClassicGameStartTime();

            // Update save game enumerations
            GameManager.Instance.SaveLoadManager.EnumerateSaves();

            OnLoad += (_ => {
                saveDataJsonCache = null;
                saveDataCache = null;
            });
        }

        static bool sceneUnloaded = false;
        void OnApplicationQuit()
        {
            sceneUnloaded = true;
        }

        void OnDestroy()
        {
            sceneUnloaded = true;
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Checks if save/load system is ready.
        /// </summary>
        /// <returns>True if ready.</returns>
        public bool IsReady()
        {
            if (!DaggerfallUnity.Instance.IsReady || !DaggerfallUnity.Instance.IsPathValidated)
                return false;

            return true;
        }

        /// <summary>
        /// Updates save game enumerations.
        /// Must call this before working with existing saves.
        /// For example, this is called in save UI every time window pushed to stack.
        /// </summary>
        public void EnumerateSaves()
        {
#if UNITY_PSP2
            // Nothing written or deleted since the last listing: keep it
            if (vitaSavesListedAt == VitaSaveIO.Changes)
                return;
            vitaSavesListedAt = VitaSaveIO.Changes;
            float listStart = Time.realtimeSinceStartup;
#endif
            enumeratedSaveFolders = EnumerateSaveFolders();
            enumeratedSaveInfo = EnumerateSaveInfo(enumeratedSaveFolders);
            enumeratedCharacterSaves = EnumerateCharacterSaves(enumeratedSaveInfo);
#if UNITY_PSP2
            Debug.LogFormat("VITASAVELIST {0} saves in {1:F0} ms", enumeratedSaveInfo.Count, (Time.realtimeSinceStartup - listStart) * 1000);
#endif
        }

        /// <summary>
        /// Gets array of save keys for the specified character.
        /// </summary>
        /// <param name="characterName">Name of character.</param>
        /// <returns>Array of save keys</returns>
        public int[] GetCharacterSaveKeys(string characterName)
        {
            if (!enumeratedCharacterSaves.ContainsKey(characterName))
                return new int[0];

            return enumeratedCharacterSaves[characterName].ToArray();
        }

        public string[] GetCharacterNames()
        {
            List<string> names = new List<string>();
            foreach(var kvp in enumeratedCharacterSaves)
            {
                names.Add(kvp.Key);
            }

            return names.ToArray();
        }

        /// <summary>
        /// Gets folder containing save by key.
        /// </summary>
        /// <param name="key">Save key.</param>
        /// <returns>Path to save folder or empty string if key not found.</returns>
        public string GetSaveFolder(int key)
        {
            if (!enumeratedSaveFolders.ContainsKey(key))
                return string.Empty;

            return enumeratedSaveFolders[key];
        }

        /// <summary>
        /// Gets save information by key.
        /// </summary>
        /// <param name="key">Save key.</param>
        /// <returns>SaveInfo populated with save details, or empty struct if save not found.</returns>
        public SaveInfo_v1 GetSaveInfo(int key)
        {
            if (!enumeratedSaveInfo.ContainsKey(key))
                return new SaveInfo_v1();

            return enumeratedSaveInfo[key];
        }

        public Texture2D GetSaveScreenshot(int key)
        {
            if (!enumeratedSaveFolders.ContainsKey(key))
                return null;

#if UNITY_PSP2
            // The load/save list read and decoded the full-size JPG on every cursor move (slow menus on hardware): the raw thumbnail
            // is the save file's second part, read with the save info and copied
            Dictionary<string, byte[]> parts = VitaSaveIO.ReadSave(GetSaveFolder(key), 2);
            byte[] thumbData;
            return parts != null && parts.TryGetValue(vitaThumbFilename, out thumbData) ? ReadVitaThumb(thumbData) : null;
#else
            string path = Path.Combine(GetSaveFolder(key), screenshotFilename);
            byte[] data = File.ReadAllBytes(path);

            Texture2D screenshot = new Texture2D(0, 0);
            if (screenshot.LoadImage(data))
                return screenshot;

            return null;
#endif
        }

#if UNITY_PSP2
        // 2x2 average to half size
        static Color32[] HalveVitaPixels(Color32[] pixels, int width, int height)
        {
            int w = width / 2, h = height / 2;
            var half = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color32 a = pixels[2 * y * width + 2 * x], b = pixels[2 * y * width + 2 * x + 1];
                    Color32 c = pixels[(2 * y + 1) * width + 2 * x], d = pixels[(2 * y + 1) * width + 2 * x + 1];
                    half[y * w + x] = new Color32((byte)((a.r + b.r + c.r + d.r + 2) >> 2), (byte)((a.g + b.g + c.g + d.g + 2) >> 2),
                        (byte)((a.b + b.b + c.b + d.b + 2) >> 2), 255);
                }
            return half;
        }

        // 8-byte header (width, height) + RGB24 rows, bottom row first as Unity's texture data
        static byte[] VitaThumbBytes(Color32[] pixels, int width, int height)
        {
            byte[] data = new byte[8 + width * height * 3];
            BitConverter.GetBytes(width).CopyTo(data, 0);
            BitConverter.GetBytes(height).CopyTo(data, 4);
            int o = 8;
            foreach (Color32 c in pixels)
            {
                data[o++] = c.r;
                data[o++] = c.g;
                data[o++] = c.b;
            }
            return data;
        }

        static Texture2D ReadVitaThumb(byte[] data)
        {
            int w = data.Length >= 8 ? BitConverter.ToInt32(data, 0) : 0, h = data.Length >= 8 ? BitConverter.ToInt32(data, 4) : 0;
            if (w <= 0 || h <= 0 || data.Length != 8 + w * h * 3)
                return null;
            byte[] rgb = new byte[w * h * 3];
            Buffer.BlockCopy(data, 8, rgb, 0, rgb.Length);
            Texture2D thumb = new Texture2D(w, h, TextureFormat.RGB24, false);
            thumb.LoadRawTextureData(rgb);
            thumb.Apply(false, true);
            return thumb;
        }
#endif

        /// <summary>
        /// Finds existing save folder.
        /// </summary>
        /// <param name="characterName">Name of character to match.</param>
        /// <param name="saveName">Name of save to match.</param>
        /// <returns>Save key or -1 if save not found.</returns>
        public int FindSaveFolderByNames(string characterName, string saveName)
        {
            int[] saves = GetCharacterSaveKeys(characterName);
            foreach (int key in saves)
            {
                SaveInfo_v1 compareInfo = GetSaveInfo(key);
                if (compareInfo.characterName == characterName &&
                    compareInfo.saveName == saveName)
                {
                    return key;
                }
            }

            return -1;
        }

        /// <summary>
        /// Finds most recent save.
        /// </summary>
        /// <returns>Save key of most recent save, or -1 if no saves found.</returns>
        public int FindMostRecentSave()
        {
            long mostRecentTime = -1;
            int mostRecentKey = -1;
            foreach (var kvp in enumeratedSaveInfo)
            {
                if (kvp.Value.dateAndTime.realTime > mostRecentTime)
                {
                    mostRecentTime = kvp.Value.dateAndTime.realTime;
                    mostRecentKey = kvp.Key;
                }
            }

            return mostRecentKey;
        }

        /// <summary>
        /// Deletes save folder.
        /// </summary>
        /// <param name="key">Save key.</param>
        public void DeleteSaveFolder(int key)
        {
            if (!enumeratedSaveFolders.ContainsKey(key))
                return;

            // For safety only delete known save files - do not perform a recursive delete
            // This way we don't blow up folder if user has placed something custom inside
            string path = GetSaveFolder(key);
#if UNITY_PSP2
            VitaSaveIO.Changes++;
            VitaFile.Delete(path);  // one file per save
            VitaFile.Delete(path + ".tmp");
#else
            File.Delete(Path.Combine(path, saveDataFilename));
            File.Delete(Path.Combine(path, saveInfoFilename));
            File.Delete(Path.Combine(path, screenshotFilename));
            File.Delete(Path.Combine(path, containerDataFilename));
            File.Delete(Path.Combine(path, automapDataFilename));
            File.Delete(Path.Combine(path, questExceptionsFilename));
            File.Delete(Path.Combine(path, conversationDataFilename));
            File.Delete(Path.Combine(path, discoveryDataFilename));
            File.Delete(Path.Combine(path, factionDataFilename));
            File.Delete(Path.Combine(path, questDataFilename));
            File.Delete(Path.Combine(path, bioFileName));
            File.Delete(Path.Combine(path, notebookDataFilename));
            File.Delete(Path.Combine(path, worldVariationDataFilename));
            if (ModManager.Instance != null)
            {
                foreach (Mod mod in ModManager.Instance.GetAllModsWithSaveData())
                    File.Delete(Path.Combine(path, GetModDataFilename(mod)));
            }

            // Check if folder is empty
            if (!Directory.GetFileSystemEntries(path, "*", SearchOption.AllDirectories).Any())
            {
                // Attempt to delete path itself
                // Even if delete fails path should be invalid with save info removed
                // Folder index will be excluded from enumeration and recycled later
                try
                {
                    Directory.Delete(path);
                }
                catch (Exception ex)
                {
                    LogFolderException("save", path, ex);

                    // We could not delete the folder for some reason, attempt to
                    // make a backup.
                    TryBackupDeletedSaveFolder(path);
                }
            }
            else
            {
                // Unexpected files in the folder, attempt to make a backup.
                TryBackupDeletedSaveFolder(path);
            }
#endif

            // Update saves
            EnumerateSaves();
        }

        public void Save(string characterName, string saveName, bool instantReload = false)
        {
            // Must be ready
            if (!IsReady())
                throw new Exception(notReadyExceptionText);

            // Do nothing if load in progress
            if (LoadInProgress)
                return;

            // Save game
            StartCoroutine(SaveGame(characterName, saveName, instantReload));
        }

        public void QuickSave(bool instantReload = false)
        {
            if (!LoadInProgress)
            {
                if (GameManager.Instance.SaveLoadManager.IsSavingPrevented)
                    DaggerfallUI.MessageBox(TextManager.Instance.GetLocalizedText("cannotSaveNow"));
                else
                    Save(GameManager.Instance.PlayerEntity.Name, quickSaveName, instantReload);
            }
        }

        public void Load(int key)
        {
            // Must be ready
            if (!IsReady())
                throw new Exception(notReadyExceptionText);

            // Load must not be in progress
            if (loadInProgress)
                return;

            // Get folder
            string path;
            if (key == -1)
                return;
            else
                path = GetSaveFolder(key);

            // Load game
            loadInProgress = true;
            GameManager.Instance.PauseGame(false);
            StartCoroutine(LoadGame(path));

            // Notify
            DaggerfallUI.Instance.PopupMessage(TextManager.Instance.GetLocalizedText("gameLoaded"));
        }

        public void Load(string characterName, string saveName)
        {
            //// Must be ready
            //if (!IsReady())
            //    throw new Exception(notReadyExceptionText);

            //// Load must not be in progress
            //if (loadInProgress)
            //    return;

            // Look for existing save with this character and name
            int key = FindSaveFolderByNames(characterName, saveName);
            Load(key);

            //// Get folder
            //string path;
            //if (key == -1)
            //    return;
            //else
            //    path = GetSaveFolder(key);

            //// Load game
            //loadInProgress = true;
            //GameManager.Instance.PauseGame(false);
            //StartCoroutine(LoadGame(path));

            //// Notify
            //DaggerfallUI.Instance.PopupMessage(HardStrings.gameLoaded);
        }

        public void QuickLoad()
        {
            Load(GameManager.Instance.PlayerEntity.Name, quickSaveName);
        }

        /// <summary>
        /// Checks if quick save folder exists.
        /// </summary>
        /// <returns>True if quick save exists.</returns>
        public bool HasQuickSave(string characterName)
        {
            // Look for existing save with this character and name
            int key = FindSaveFolderByNames(characterName, quickSaveName);

            // Get folder
            return key != -1;
        }

        /// <summary>
        /// Checks for a mod mismatch in the QuickSave, and displays a warning message before proceeding
        /// </summary>
        /// <param name="characterName">Character's name in the save</param>
        /// <param name="loadGameAction">The steps taken to load a save after finding no mismatches, or after user proceeds to load anyway</param>
        public void PromptQuickLoadGame(string characterName, Action loadGameAction)
        {
            PromptLoadGame(characterName, quickSaveName, loadGameAction);
        }

        /// <summary>
        /// Checks for a mod mismatch in a save, and displays a warning message before proceeding
        /// </summary>
        /// <param name="characterName">Character's name in the save</param>
        /// <param name="saveName">Name of the save to be loaded</param>
        /// <param name="loadGameAction">The steps taken to load a save after finding no mismatches, or after user proceeds to load anyway</param>
        public void PromptLoadGame(string characterName, string saveName, Action loadGameAction)
        {
            string[] modMessage = SaveModConflictMessage(characterName, saveName, out saveDataJsonCache, out saveDataCache);

            if (modMessage != null)
            {
                DaggerfallMessageBox modBox = new DaggerfallMessageBox(DaggerfallUI.UIManager, DaggerfallUI.UIManager.TopWindow);

                modBox.EnableVerticalScrolling(80);
                modBox.SetText(modMessage);
                modBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.Yes, true);
                modBox.AddButton(DaggerfallMessageBox.MessageBoxButtons.No);
                modBox.PauseWhileOpen = true;

                modBox.OnButtonClick += ((s, messageBoxButton) =>
                {
                    s.CloseWindow();
                    if (messageBoxButton == DaggerfallMessageBox.MessageBoxButtons.Yes)
                    {
                        loadGameAction();
                    }
                });

                modBox.Show();
            }
            else
            {
                loadGameAction();
            }
        }

        public void Rename(int key, string newSaveName)
        {
            if (!enumeratedSaveFolders.ContainsKey(key))
                return;

            // Get save info
            SaveInfo_v1 saveInfo = GetSaveInfo(key);

            // Write save info only if save name has been modified
            if (newSaveName != saveInfo.saveName)
            {
                saveInfo.saveName = newSaveName;
#if UNITY_PSP2
                // The save info is the save file's first part: rewrite the file with the new one
                string path = GetSaveFolder(key);
                Dictionary<string, byte[]> parts = VitaSaveIO.ReadSave(path);
                if (parts != null)
                {
                    var list = new List<KeyValuePair<string, byte[]>> { new KeyValuePair<string, byte[]>(saveInfoFilename, VitaSaveIO.Encode(saveInfo)) };
                    foreach (var part in parts)
                        if (part.Key != saveInfoFilename)
                            list.Add(part);
                    VitaSaveIO.WriteSave(path, VitaSaveIO.Pack(list));
                }
#else
                string saveInfoJson = Serialize(saveInfo.GetType(), saveInfo);
                string path = GetSaveFolder(key);
                WriteSaveFile(Path.Combine(path, saveInfoFilename), saveInfoJson);
#endif
            }
        }

        #endregion

        #region Public Static Methods

        public static bool FindSingleton(out SaveLoadManager singletonOut)
        {
            singletonOut = FindObjectOfType<SaveLoadManager>();
            return singletonOut != null;
        }

        /// <summary>
        /// Register ISerializableGameObject with SerializableStateManager.
        /// </summary>
        public static void RegisterSerializableGameObject(ISerializableGameObject serializableObject)
        {
            if (sceneUnloaded)
                return;
            Instance.stateManager.RegisterStatefulGameObject(serializableObject);
        }

        /// <summary>
        /// Deregister ISerializableGameObject from SerializableStateManager.
        /// </summary>
        public static void DeregisterSerializableGameObject(ISerializableGameObject serializableObject)
        {
            if (sceneUnloaded)
                return;
            Instance.stateManager.DeregisterStatefulGameObject(serializableObject);
        }

        /// <summary>
        /// Force deregister all ISerializableGameObject instances from SerializableStateManager.
        /// </summary>
        public static void DeregisterAllSerializableGameObjects(bool keepPlayer = true)
        {
            if (sceneUnloaded)
                return;
            Instance.stateManager.DeregisterAllStatefulGameObjects(keepPlayer);
        }

        /// <summary>
        /// Stores the current scene in the SerializableStateManager cache using the given name.
        /// </summary>
        public static void CacheScene(string sceneName)
        {
            if (!sceneUnloaded)
                Instance.stateManager.CacheScene(sceneName);
        }

        /// <summary>
        /// Restores the current scene from the SerializableStateManager cache using the given name.
        /// </summary>
        public static void RestoreCachedScene(string sceneName)
        {
            if (!sceneUnloaded)
                Instance.StartCoroutine(Instance.RestoreCachedSceneNextFrame(sceneName));
        }

        private IEnumerator RestoreCachedSceneNextFrame(string sceneName)
        {
            // Wait another frame so everthing has a chance to register
            yield return new WaitForEndOfFrame();
            // Restore the scene from cache
            stateManager.RestoreCachedScene(sceneName);
        }

        /// <summary>
        /// Clears the SerializableStateManager scene cache.
        /// </summary>
        /// <param name="start">True if starting a new or loaded game, so also clear permanent scene list</param>
        public static void ClearSceneCache(bool start)
        {
            if (!sceneUnloaded)
                Instance.stateManager.ClearSceneCache(start);
        }

        #endregion

        #region Serialization Helpers

        static readonly fsSerializer _serializer = new fsSerializer();

        public static string Serialize(Type type, object value, bool pretty = true)
        {
            // Serialize the data
            fsData data;
            _serializer.TrySerialize(type, value, out data).AssertSuccessWithoutWarnings();

            // Emit the data via JSON
#if UNITY_PSP2
            float printStart = Time.realtimeSinceStartup;
            string json = (pretty) ? fsJsonPrinter.PrettyJson(data) : fsJsonPrinter.CompressedJson(data);
            vitaPrintSeconds += Time.realtimeSinceStartup - printStart;
            return json;
#else
            return (pretty) ? fsJsonPrinter.PrettyJson(data) : fsJsonPrinter.CompressedJson(data);
#endif
        }

        public static object Deserialize(Type type, string serializedState)
        {
            return Deserialize(type, serializedState, false);
        }

        public static object Deserialize(Type type, string serializedState, bool assertSuccess)
        {
            // Step 1: Parse the JSON data
            fsData data = fsJsonParser.Parse(serializedState);

            // Step 2: Deserialize the data
            object deserialized = null;
            if (assertSuccess)
                _serializer.TryDeserialize(data, type, ref deserialized).AssertSuccess();
            else
                _serializer.TryDeserialize(data, type, ref deserialized).AssertSuccessWithoutWarnings();

            return deserialized;
        }

        #endregion

        #region Private Methods

        private void SetupSingleton()
        {
            if (instance == null)
                instance = this;
            else if (instance != this)
            {
                if (Application.isPlaying)
                {
                    DaggerfallUnity.LogMessage("Multiple SaveLoad instances detected in scene!", true);
                    Destroy(gameObject);
                }
            }
        }

        string GetUnitySavePath()
        {
            if (!string.IsNullOrEmpty(unitySavePath))
                return unitySavePath;

            string result = string.Empty;

            // Try settings
            result = DaggerfallUnity.Settings.MyDaggerfallUnitySavePath;
            if (string.IsNullOrEmpty(result) || !Directory.Exists(result))
            {
                // Default to dataPath
                result = Path.Combine(DaggerfallUnity.Settings.PersistentDataPath, rootSaveFolder);
                if (!Directory.Exists(result))
                {
                    // Attempt to create path
                    Directory.CreateDirectory(result);
                }
            }

            // Test result is a valid path
            if (!Directory.Exists(result))
                throw new Exception("Could not locate valid path for Unity save files. Check 'MyDaggerfallUnitySavePath' in settings.ini.");

            // Log result and save path
            DaggerfallUnity.LogMessage(string.Format("Using path '{0}' for Unity saves.", result), true);
            unitySavePath = result;

            return result;
        }

        string GetDaggerfallSavePath()
        {
            if (!string.IsNullOrEmpty(daggerfallSavePath))
                return daggerfallSavePath;

            string result = string.Empty;

            // Test result is a valid path
            result = Path.GetDirectoryName(DaggerfallUnity.Instance.Arena2Path);
            if (!Directory.Exists(result))
                throw new Exception("Could not locate valid path for Daggerfall save files. Check 'MyDaggerfallPath' in settings.ini points to your Daggerfall folder.");

            // Log result and save path
            DaggerfallUnity.LogMessage(string.Format("Using path '{0}' for Daggerfall save importing.", result), true);
            daggerfallSavePath = result;

            return result;
        }

#if UNITY_PSP2
        // Step timing for the VITASAVE / VITALOAD lines: one line per save or load, laps in seconds, JSON sizes in KB
        float vitaLapT;
        StringBuilder vitaLapSb;
        static float vitaPrintSeconds;   // of the last laps, spent printing JSON (the rest is FullSerializer's reflection walk)

        void VitaLapStart()
        {
            vitaLapSb = new StringBuilder();
            vitaLapT = Time.realtimeSinceStartup;
        }

        void VitaLap(string name, string json = null)
        {
            float t = Time.realtimeSinceStartup;
            vitaLapSb.AppendFormat(", {0} {1:F2}", name, t - vitaLapT);
            if (json != null)
                vitaLapSb.AppendFormat(" ({0} KB, print {1:F2})", json.Length / 1024, vitaPrintSeconds);
            if (loadInProgress)     // stuck loading window: each load step as it happens
                Debug.LogFormat("VITALAP {0} {1:F2}", name, t - vitaLapT);
            vitaPrintSeconds = 0;
            vitaLapT = t;
            // automap 5 s); the world build moves it from "window" to "respawn"
            float done;
            VitaLoadingWindow window = loadInProgress ? DaggerfallUI.UIManager.TopWindow as VitaLoadingWindow : null;
            if (window != null && vitaLoadBar.TryGetValue(name, out done))
            {
                window.Progress = done;
                window.WorldFrom = 0.35f;
                window.WorldTo = 0.92f;
            }
        }

        static readonly Dictionary<string, float> vitaLoadBar = new Dictionary<string, float> {
            { "read", 0.02f }, { "dSave", 0.05f }, { "dFaction", 0.1f }, { "dQuest", 0.13f }, { "dTalk", 0.33f }, { "window", 0.35f },
            { "respawn", 0.92f }, { "automap", 1f },
        };
#endif

#if UNITY_PSP2
        Dictionary<string, byte[]> vitaParts;   // the native save being loaded (VitaSaveIO), null for a JSON save

        string VitaReadSaveFile(string folder, string file)
        {
            return vitaParts != null ? null : ReadSaveFile(Path.Combine(folder, file));
        }

        bool VitaHasPart(string file, string json)
        {
            return vitaParts != null ? vitaParts.ContainsKey(file) : !string.IsNullOrEmpty(json);
        }

        // The Vita's save (one file per save, VitaSaveIO): everything SaveGame writes, as parts of Saves/SAVEn.vita. The game state is
        // encoded before the screenshot frames, as SaveGame serializes it before them. No JPG: the save list shows the raw thumbnail.
        IEnumerator VitaSaveGame(string characterName, string saveName, bool instantReload)
        {
            float saveStart = Time.realtimeSinceStartup;
            VitaLapStart();
            // Collect first: the save's 1-2 MB buffers (pixels, parts, file) otherwise made Boehm grow the heap for good when its
            // free blocks were fragmented
            System.GC.Collect();
            VitaLap("gc");
            string vitaHeapBefore = VitaMemory.GcHeapForLog();   // probes: does a save grow the managed heap?

            // Look for existing save with this character and name, else the first free SAVEn
            int key = FindSaveFolderByNames(characterName, saveName);
            string path;
            if (key == -1)
            {
                int free = 0;
                while (enumeratedSaveFolders.ContainsKey(free))
                    free++;
                path = Path.Combine(UnitySavePath, savePrefix + free + VitaSaveIO.Extension);
            }
            else
                path = GetSaveFolder(key);

            SaveData_v1 saveData = BuildSaveData();
            SaveInfo_v1 saveInfo = new SaveInfo_v1();
            saveInfo.saveVersion = LatestSaveVersion;
            saveInfo.saveName = saveName;
            saveInfo.characterName = saveData.playerData.playerEntity.name;
            saveInfo.dateAndTime = saveData.dateAndTime;
            saveInfo.dfuVersion = VersionInfo.DaggerfallUnityVersion;
            VitaLap("build");

            // Save info first and the thumbnail second (the save list reads only those), then the rest
            var parts = new List<KeyValuePair<string, byte[]>>();
            try
            {
                var values = new List<KeyValuePair<string, object>>
                {
                    new KeyValuePair<string, object>(saveInfoFilename, saveInfo),
                    new KeyValuePair<string, object>(saveDataFilename, saveData),
                    new KeyValuePair<string, object>(factionDataFilename, stateManager.GetPlayerFactionData()),
                    new KeyValuePair<string, object>(questDataFilename, QuestMachine.Instance.GetSaveData()),
                    new KeyValuePair<string, object>(discoveryDataFilename, GameManager.Instance.PlayerGPS.GetDiscoverySaveData()),
                    new KeyValuePair<string, object>(conversationDataFilename, GameManager.Instance.TalkManager.GetConversationSaveData()),
                    new KeyValuePair<string, object>(notebookDataFilename, GameManager.Instance.PlayerEntity.Notebook.GetNotebookSaveData()),
                    new KeyValuePair<string, object>(worldVariationDataFilename, WorldDataVariants.GetWorldVariationSaveData()),
                    new KeyValuePair<string, object>(questExceptionsFilename, QuestMachine.Instance.GetStoredExceptions()),
                    new KeyValuePair<string, object>(bioFileName, string.Join("\n", GameManager.Instance.PlayerEntity.BackStory.ToArray())),
                };
                try
                {
                    values.Add(new KeyValuePair<string, object>(automapDataFilename, GameManager.Instance.InteriorAutomap.GetState()));
                }
                catch (Exception ex)
                {
                    Debug.Log(string.Format("Failed to save automap state. Message: {0}", ex.Message));
                }
                if (ModManager.Instance != null)
                    foreach (Mod mod in ModManager.Instance.GetAllModsWithSaveData())
                    {
                        try
                        {
                            object modData = mod.SaveDataInterface.GetSaveData();
                            if (modData != null)
                                values.Add(new KeyValuePair<string, object>(GetModDataFilename(mod), Serialize(modData.GetType(), modData)));
                        }
                        catch (Exception ex)
                        {
                            DaggerfallUI.AddHUDText(string.Format("Failed to save mod data for `{0}`. Check log for errors.", mod.ModInfo.ModTitle), 3);
                            DaggerfallUnity.LogMessage(string.Format("Failed to save mod data for `{0}`. Exception: {1}", mod.ModInfo.ModTitle, ex.Message), true);
                        }
                    }
                foreach (var value in values)
                    if (value.Value != null)
                        parts.Add(new KeyValuePair<string, byte[]>(value.Key, VitaSaveIO.Encode(value.Value)));
            }
            catch (Exception ex)
            {
                // The last save in this slot stays as it was
                Debug.LogError("VITASAVE encode failed: " + ex);
                DaggerfallUI.AddHUDText("Save failed. Check log for errors.", 3);
                yield break;
            }
            VitaLap("encode");

            // Screenshot: read back on the CPU only, channels fixed, half size (see SaveGame)
            VitaMenuNav.HideCursor = true; // the D-pad arrow ended up in save pictures
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
            Texture2D screenshot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false);
            screenshot.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
            VitaMenuNav.HideCursor = false;
            Color32[] pixels = screenshot.GetPixels32();
            int fullWidth = screenshot.width;
            Destroy(screenshot);
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                pixels[i] = new Color32(c.g, c.b, c.a, 255);
            }
            pixels = HalveVitaPixels(pixels, fullWidth, Screen.height);
            parts.Insert(1, new KeyValuePair<string, byte[]>(vitaThumbFilename, VitaThumbBytes(pixels, fullWidth / 2, Screen.height / 2)));
            VitaLap("shot");

            long fileLength = VitaSaveIO.WriteSave(path, parts);
            VitaLap("write");
            Debug.LogFormat("VITASAVE {0} steps:{1}, file {2} KB, total {3:F2} s | before{4} | after{5}", saveName, vitaLapSb, fileLength / 1024,
                Time.realtimeSinceStartup - saveStart, vitaHeapBefore, VitaMemory.GcHeapForLog());

            RaiseOnSaveEvent(saveData);
            if (key == -1)
                EnumerateSaves();
            DaggerfallUI.Instance.PopupMessage(TextManager.Instance.GetLocalizedText("gameSaved"));
            if (instantReload)
                Load(saveData.playerData.playerEntity.name, saveName);
        }

        object VitaDeserialize(Type type, string file, string json, bool assertSuccess = false)
        {
            byte[] data;
            if (vitaParts != null)
                return vitaParts.TryGetValue(file, out data) ? VitaSaveCodec.Read(type, data) : null;
            return Deserialize(type, json, assertSuccess);
        }
#endif

        void WriteSaveFile(string path, string json)
        {
#if UNITY_PSP2
            VitaFile.Delete(path);
#endif
            File.WriteAllText(path, json);
        }

        string ReadSaveFile(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch(Exception ex)
            {
                DaggerfallUnity.LogMessage(ex.Message);
                return string.Empty;
            }
        }

        Dictionary<int, string> EnumerateSaveFolders()
        {
#if UNITY_PSP2
            // One file per save (VitaSaveIO): one listing of the save folder, no folder per save to open. A .tmp alone is a save killed
            // between delete and move; ReadSave falls back to it
            Dictionary<int, string> saveFolders = new Dictionary<int, string>();
            foreach (string file in Directory.GetFiles(UnitySavePath, savePrefix + "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".tmp"))
                    name = name.Substring(0, name.Length - 4);
                int key;
                if (name.EndsWith(VitaSaveIO.Extension) && int.TryParse(name.Substring(savePrefix.Length, name.Length - savePrefix.Length - VitaSaveIO.Extension.Length), out key) &&
                    !saveFolders.ContainsKey(key))
                    saveFolders.Add(key, Path.Combine(UnitySavePath, name));
            }
            return saveFolders;
#else
            // Get directories in save path matching prefix
            string[] directories = Directory.GetDirectories(UnitySavePath, savePrefix + "*", SearchOption.TopDirectoryOnly);

            // Build dictionary keyed by save index
            Dictionary<int, string> saveFolders = new Dictionary<int, string>();
            foreach (string directory in directories)
            {
                // Get everything right of prefix in folder name (should be a number)
                int key;
                string indexStr = Path.GetFileName(directory).Substring(savePrefix.Length);
                if (int.TryParse(indexStr, out key))
                {
                    // Must contain a save info file to be a valid save folder
                    if (File.Exists(Path.Combine(directory, saveInfoFilename)))
                        saveFolders.Add(key, directory);
                }
            }

            return saveFolders;
#endif
        }

        Dictionary<int, SaveInfo_v1> EnumerateSaveInfo(Dictionary<int, string> saveFolders)
        {
            Dictionary<int, SaveInfo_v1> saveInfoDict = new Dictionary<int, SaveInfo_v1>();
            foreach (var kvp in saveFolders)
            {
                try
                {
                    SaveInfo_v1 saveInfo = ReadSaveInfo(kvp.Value);
                    saveInfoDict.Add(kvp.Key, saveInfo);
                }
                catch (Exception ex)
                {
                    DaggerfallUnity.LogMessage(string.Format("Failed to read {0} in save folder {1}. Exception.Message={2}", saveInfoFilename, kvp.Value, ex.Message));
                }
            }

            return saveInfoDict;
        }

        Dictionary<string, List<int>> EnumerateCharacterSaves(Dictionary<int, SaveInfo_v1> saveInfo)
        {
            Dictionary<string, List<int>> characterSaves = new Dictionary<string, List<int>>();
            foreach (var kvp in saveInfo)
            {
                // Add character to name dictionary
                if (!characterSaves.ContainsKey(kvp.Value.characterName))
                {
                    characterSaves.Add(kvp.Value.characterName, new List<int>());
                }

                // Add save key to character save list
                characterSaves[kvp.Value.characterName].Add(kvp.Key);
            }

            return characterSaves;
        }

        SaveInfo_v1 ReadSaveInfo(string saveFolder)
        {
#if UNITY_PSP2
            Dictionary<string, byte[]> parts = VitaSaveIO.ReadSave(saveFolder, 1);
            return (SaveInfo_v1)VitaSaveCodec.Read(typeof(SaveInfo_v1), parts[saveInfoFilename]);
#endif
            string saveInfoJson = ReadSaveFile(Path.Combine(saveFolder, saveInfoFilename));
            SaveInfo_v1 saveInfo = Deserialize(typeof(SaveInfo_v1), saveInfoJson) as SaveInfo_v1;

            return saveInfo;
        }

        /// <summary>
        /// Checks if save folder exists.
        /// </summary>
        /// <param name="folderName">Folder name of save.</param>
        /// <returns>True if folder exists.</returns>
        bool HasSaveFolder(string folderName)
        {
            return Directory.Exists(Path.Combine(UnitySavePath, folderName));
        }

        private void TryBackupDeletedSaveFolder(string path)
        {
            try
            {
                BackupDeletedSaveFolder(path);
            }
            catch (Exception ex)
            {
                LogFolderException("backup", path, ex);
            }
        }

        private void BackupDeletedSaveFolder(string path)
        {
            var saveName = Path.GetFileName(path);
            var basePath = Path.GetDirectoryName(path);
            var backupBasePath = Path.Combine(basePath, "DELETED");

            if (!Directory.Exists(backupBasePath))
            {
                Directory.CreateDirectory(backupBasePath);
            }

            string dstPath = Path.Combine(backupBasePath, saveName);

            var dstFolderSuffix = 2;

            while (Directory.Exists(dstPath))
            {
                dstPath = Path.Combine(backupBasePath, $"{saveName}_{dstFolderSuffix++}");
            }

            DaggerfallUnity.LogMessage($"Moving '{saveName}' to backup '{dstPath}'.");
            Directory.Move(path, dstPath);
        }

        private void LogFolderException(string action, string path, Exception ex) =>
            DaggerfallUnity.LogMessage($"Could not {action} save folder '{path}'. Exception message: {ex.Message}");

        #endregion

        #region Saving

        SaveData_v1 BuildSaveData()
        {
            SaveData_v1 saveData = new SaveData_v1();
            saveData.header = new SaveDataDescription_v1();
            saveData.currentUID = DaggerfallUnity.CurrentUID;
            saveData.dateAndTime = GetDateTimeData();
            saveData.playerData = stateManager.GetPlayerData();
            saveData.dungeonData = GetDungeonData();
            saveData.enemyData = stateManager.GetEnemyData();
            saveData.lootContainers = stateManager.GetLootContainerData();
            saveData.bankAccounts = GetBankAccountData();
            saveData.bankDeeds = GetBankDeedData();
            saveData.escortingFaces = DaggerfallUI.Instance.DaggerfallHUD.EscortingFaces.GetSaveData();
            saveData.sceneCache = stateManager.GetSceneCache();
            saveData.travelMapData = DaggerfallUI.Instance.DfTravelMapWindow.GetTravelMapSaveData();
            saveData.advancedClimbingState = GameManager.Instance.ClimbingMotor.GetSaveData();
            saveData.modInfoData = GetModInfoData();

            return saveData;
        }

        ModInfo_v1[] GetModInfoData()
        {
            if (ModManager.Instance == null)
                return null;

            List<ModInfo_v1> records = new List<ModInfo_v1>();
            foreach (var mod in ModManager.Instance.Mods)
            {
                if (mod.Enabled)
                {
                    var record = new ModInfo_v1();
                    record.fileName = mod.FileName;
                    record.title = mod.Title;
                    record.guid = mod.GUID;
                    record.version = mod.ModInfo.ModVersion;
                    record.loadPriority = mod.LoadPriority;

                    records.Add(record);
                }
            }
            return records.ToArray();
        }

        DateAndTime_v1 GetDateTimeData()
        {
            DateAndTime_v1 data = new DateAndTime_v1();
            data.gameTime = DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.ToSeconds();
#if UNITY_PSP2
            data.realTime = VitaClock.Now.Ticks; // DateTime.Now reads 1601-01-01 on Vita (dates, save order, most recent save)
#else
            data.realTime = DateTime.Now.Ticks;
#endif

            return data;
        }

        DungeonData_v1 GetDungeonData()
        {
            DungeonData_v1 data = new DungeonData_v1();
            data.actionDoors = stateManager.GetActionDoorData();
            data.actionObjects = stateManager.GetActionObjectData();

            return data;
        }

        BankRecordData_v1[] GetBankAccountData()
        {
            List<BankRecordData_v1> records = new List<BankRecordData_v1>();

            foreach (var record in DaggerfallBankManager.BankAccounts)
            {
                if (record == null)
                    continue;
                else if (record.accountGold == 0 && record.loanTotal == 0 && record.loanDueDate == 0)
                    continue;
                else
                    records.Add(record);
            }

            return records.ToArray();
        }

        BankDeedData_v1 GetBankDeedData()
        {
            return new BankDeedData_v1() {
                shipType = (int) DaggerfallBankManager.OwnedShip,
                houses = GetHousesData(),
            };
        }

        HouseData_v1[] GetHousesData()
        {
            List<HouseData_v1> records = new List<HouseData_v1>();
            foreach (var record in DaggerfallBankManager.Houses)
            {
                if (record == null)
                    continue;
                else if (record.mapID == 0 && record.buildingKey == 0)
                    continue;
                else
                    records.Add(record);
            }
            return records.ToArray();
        }

        /// <summary>
        /// Gets a specific save path.
        /// </summary>
        /// <param name="folderName">Folder name of save.</param>
        /// <param name="create">Creates folder if it does not exist.</param>
        /// <returns>Save path.</returns>
        string GetSavePath(string folderName, bool create)
        {
            // Compose folder path
            string path = Path.Combine(UnitySavePath, folderName);

            // Create directory if it does not exist
            if (create && !Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            return path;
        }

        /// <summary>
        /// Creates a new indexed save path.
        /// </summary>
        /// <param name="saveFolders">Save folder enumeration.</param>
        /// <returns>Save path.</returns>
        string CreateNewSavePath(Dictionary<int, string> saveFolders)
        {
            // Find first available save index in dictionary
            int key = 0;
            while (saveFolders.ContainsKey(key))
            {
                key++;
            }

            return GetSavePath(savePrefix + key, true);
        }

        #endregion

        #region Loading

        void RestoreSaveData(SaveData_v1 saveData)
        {
#if UNITY_PSP2
            // One step at a time: a NullReferenceException in one of them skipped every step after it,
            // and the release player gives no stack trace, so log the step's name and go on with the others
            DaggerfallUnity.CurrentUID = saveData.currentUID;
            VitaRestoreStep("dateTime", () => RestoreDateTimeData(saveData.dateAndTime));
            VitaRestoreStep("player", () => stateManager.RestorePlayerData(saveData.playerData));
            VitaRestoreStep("dungeon", () => RestoreDungeonData(saveData.dungeonData));
            VitaRestoreStep("enemies", () => stateManager.RestoreEnemyData(saveData.enemyData));
            VitaRestoreStep("loot", () => stateManager.RestoreLootContainerData(saveData.lootContainers));
            VitaRestoreStep("bank", () => RestoreBankData(saveData.bankAccounts));
            VitaRestoreStep("deeds", () => RestoreBankDeedData(saveData.bankDeeds));
            VitaRestoreStep("escorts", () => RestoreEscortingFacesData(saveData.escortingFaces));
            VitaRestoreStep("sceneCache", () => stateManager.RestoreSceneCache(saveData.sceneCache));
        }

        static void VitaRestoreStep(string name, Action step)
        {
            try
            {
                step();
            }
            catch (Exception ex)
            {
                Debug.LogFormat("VITALOAD restore step {0} failed: {1}", name, ex);
            }
        }

        void RestoreSaveDataUpstream(SaveData_v1 saveData)
        {
#endif
            DaggerfallUnity.CurrentUID = saveData.currentUID;
            RestoreDateTimeData(saveData.dateAndTime);
            stateManager.RestorePlayerData(saveData.playerData);
            RestoreDungeonData(saveData.dungeonData);
            stateManager.RestoreEnemyData(saveData.enemyData);
            stateManager.RestoreLootContainerData(saveData.lootContainers);
            RestoreBankData(saveData.bankAccounts);
            RestoreBankDeedData(saveData.bankDeeds);
            RestoreEscortingFacesData(saveData.escortingFaces);
            stateManager.RestoreSceneCache(saveData.sceneCache);
        }

        void RestoreDateTimeData(DateAndTime_v1 dateTimeData)
        {
            if (dateTimeData == null)
                return;

            DaggerfallUnity.Instance.WorldTime.DaggerfallDateTime.FromSeconds(dateTimeData.gameTime);
        }

        void RestoreDungeonData(DungeonData_v1 dungeonData)
        {
            if (dungeonData == null)
                return;

            stateManager.RestoreActionDoorData(dungeonData.actionDoors);
            stateManager.RestoreActionObjectData(dungeonData.actionObjects);
        }

        void RestoreBankData(BankRecordData_v1[] bankData)
        {
            DaggerfallBankManager.SetupAccounts();
            DaggerfallBankManager.SetupHouses();    // Covers case when loading old save with no house data

            if (bankData == null)
                return;

            for (int i = 0; i < bankData.Length; i++)
            {
                if (bankData[i].regionIndex < 0 || bankData[i].regionIndex >= DaggerfallBankManager.BankAccounts.Length)
                    continue;

                DaggerfallBankManager.BankAccounts[bankData[i].regionIndex] = bankData[i];
            }
        }

        void RestoreBankDeedData(BankDeedData_v1 deedData)
        {
            DaggerfallBankManager.OwnedShip = (deedData == null) ? ShipType.None : (ShipType) deedData.shipType;
            if (deedData != null)
                RestoreHousesData(deedData.houses);
        }

        void RestoreHousesData(HouseData_v1[] housesData)
        {
            DaggerfallBankManager.SetupHouses();

            if (housesData == null)
                return;

            for (int i = 0; i < housesData.Length; i++)
            {
                if (housesData[i].regionIndex < 0 || housesData[i].regionIndex >= DaggerfallBankManager.Houses.Length)
                    continue;

                DaggerfallBankManager.Houses[housesData[i].regionIndex] = housesData[i];
            }
        }

        void RestoreEscortingFacesData(FaceDetails[] escortingFaces)
        {
            if (DaggerfallUI.Instance.DaggerfallHUD == null)
                return;

            if (escortingFaces == null)
                DaggerfallUI.Instance.DaggerfallHUD.EscortingFaces.ClearFaces();
            else
                DaggerfallUI.Instance.DaggerfallHUD.EscortingFaces.RestoreSaveData(escortingFaces);
        }

        #endregion

        #region Utility

        IEnumerator SaveGame(string characterName, string saveName, bool instantReload = false)
        {
#if UNITY_PSP2
            yield return StartCoroutine(VitaSaveGame(characterName, saveName, instantReload));
            yield break;
#endif
#if UNITY_PSP2
            float saveStart = Time.realtimeSinceStartup;
            VitaLapStart();
            // Collect first: the save's 1-2 MB buffers (pixels, parts, file) otherwise made Boehm grow the heap for good when its
            // free blocks were fragmented
            System.GC.Collect();
            VitaLap("gc");
            string vitaHeapBefore = VitaMemory.GcHeapForLog();   // probes: does a save grow the managed heap?
#endif
            // Look for existing save with this character and name
            int key = FindSaveFolderByNames(characterName, saveName);

            // Get or create folder
            string path;
            if (key == -1)
                path = CreateNewSavePath(enumeratedSaveFolders);
            else
                path = GetSaveFolder(key);

            // Build save data
            SaveData_v1 saveData = BuildSaveData();
#if UNITY_PSP2
            VitaLap("build");
#endif

            // Build save info
            SaveInfo_v1 saveInfo = new SaveInfo_v1();
            saveInfo.saveVersion = LatestSaveVersion;
            saveInfo.saveName = saveName;
            saveInfo.characterName = saveData.playerData.playerEntity.name;
            saveInfo.dateAndTime = saveData.dateAndTime;
            saveInfo.dfuVersion = VersionInfo.DaggerfallUnityVersion;

            // Build faction data
            FactionData_v2 factionData = stateManager.GetPlayerFactionData();
#if UNITY_PSP2
            VitaLap("getFaction");
#endif

            // Build quest data
            QuestMachine.QuestMachineData_v1 questData = QuestMachine.Instance.GetSaveData();
#if UNITY_PSP2
            VitaLap("getQuest");
#endif

            // Get discovery data
            Dictionary<int, PlayerGPS.DiscoveredLocation> discoveryData = GameManager.Instance.PlayerGPS.GetDiscoverySaveData();
#if UNITY_PSP2
            VitaLap("getDisc");
#endif

            // Get conversation data
            TalkManager.SaveDataConversation conversationData = GameManager.Instance.TalkManager.GetConversationSaveData();
#if UNITY_PSP2
            VitaLap("getTalk");
#endif

            // Get notebook data
            PlayerNotebook.NotebookData_v1 notebookData = GameManager.Instance.PlayerEntity.Notebook.GetNotebookSaveData();
#if UNITY_PSP2
            VitaLap("getNote");
#endif

            // Get WorldData Variants data
            WorldDataVariants.WorldVariationData_v1 worldVariationData = WorldDataVariants.GetWorldVariationSaveData();
#if UNITY_PSP2
            VitaLap("getWorldVar");
#endif

            // Serialize save data to JSON strings
            string saveDataJson = Serialize(saveData.GetType(), saveData);
#if UNITY_PSP2
            VitaLap("sSave", saveDataJson);
#endif
            string saveInfoJson = Serialize(saveInfo.GetType(), saveInfo);
#if UNITY_PSP2
            VitaLap("sInfo", saveInfoJson);
#endif
            string factionDataJson = Serialize(factionData.GetType(), factionData);
#if UNITY_PSP2
            VitaLap("sFaction", factionDataJson);
#endif
            string questDataJson = Serialize(questData.GetType(), questData);
#if UNITY_PSP2
            VitaLap("sQuest", questDataJson);
#endif
            string discoveryDataJson = Serialize(discoveryData.GetType(), discoveryData);
#if UNITY_PSP2
            VitaLap("sDisc", discoveryDataJson);
#endif
            string conversationDataJson = Serialize(conversationData.GetType(), conversationData);
#if UNITY_PSP2
            VitaLap("sTalk", conversationDataJson);
#endif
            string notebookDataJson = Serialize(notebookData.GetType(), notebookData);
#if UNITY_PSP2
            VitaLap("sNote", notebookDataJson);
#endif
            string worldVariationDataJson = Serialize(worldVariationData.GetType(), worldVariationData);
#if UNITY_PSP2
            VitaLap("sWorldVar", worldVariationDataJson);
#endif

            //// Attempt to hide UI for screenshot
            //bool rawImageEnabled = false;
            //UnityEngine.UI.RawImage rawImage = GUI.GetDiegeticCanvasRawImage();
            //if (rawImage)
            //{
            //    rawImageEnabled = rawImage.enabled;
            //    rawImage.enabled = false;
            //}

            // Create screenshot for save
            // TODO: Hide UI for screenshot or use a different method
#if UNITY_PSP2
            VitaMenuNav.HideCursor = true; // the D-pad arrow ended up in save pictures
#endif
            yield return new WaitForEndOfFrame();
            yield return new WaitForEndOfFrame();
#if UNITY_PSP2
            // Only read on the CPU (JPG + thumb): no mips, no GPU upload (RGBA32 stays: the channel fix below reads alpha). The mipmapped NPOT texture made Apply
            // pad it to POT and ran out of memory saving in the Wayrest palace
            Texture2D screenshot = new Texture2D(Screen.width, Screen.height, TextureFormat.RGBA32, false);
#else
            Texture2D screenshot = new Texture2D(Screen.width, Screen.height);
#endif
            screenshot.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
#if UNITY_PSP2
            VitaMenuNav.HideCursor = false;
            // The Vita's backbuffer read lands each pixel one channel over: alpha in red, red in green, green in blue, blue in alpha
            // (save pictures came out red: red 255 everywhere). Shift them back before the JPG.
            Color32[] pixels = screenshot.GetPixels32();
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 c = pixels[i];
                pixels[i] = new Color32(c.g, c.b, c.a, 255);
            }
            // Keep the picture at half size (the save list shows it at about that): a quarter of the pixels to encode, write and read back
            int fullWidth = screenshot.width;
            Destroy(screenshot);
            pixels = HalveVitaPixels(pixels, fullWidth, Screen.height);
            screenshot = new Texture2D(fullWidth / 2, Screen.height / 2, TextureFormat.RGBA32, false);
            screenshot.SetPixels32(pixels);
#else
            screenshot.Apply();
#endif

            //// Restore UI after screenshot
            //if (rawImageEnabled)
            //{
            //    rawImage.enabled = true;
            //}

#if UNITY_PSP2
            VitaLap("shot");
#endif
            // Save data to files
            WriteSaveFile(Path.Combine(path, saveDataFilename), saveDataJson);
            WriteSaveFile(Path.Combine(path, saveInfoFilename), saveInfoJson);
            WriteSaveFile(Path.Combine(path, factionDataFilename), factionDataJson);
            WriteSaveFile(Path.Combine(path, questDataFilename), questDataJson);
            WriteSaveFile(Path.Combine(path, discoveryDataFilename), discoveryDataJson);
            WriteSaveFile(Path.Combine(path, conversationDataFilename), conversationDataJson);
            WriteSaveFile(Path.Combine(path, notebookDataFilename), notebookDataJson);
            WriteSaveFile(Path.Combine(path, worldVariationDataFilename), worldVariationDataJson);
#if UNITY_PSP2
            VitaLap("write");
#endif

            // Save quest exceptions
            QuestMachine.StoredException[] storedExceptions = QuestMachine.Instance.GetStoredExceptions();
            string questExceptionsJson = Serialize(storedExceptions.GetType(), storedExceptions);
            WriteSaveFile(Path.Combine(path, questExceptionsFilename), questExceptionsJson);
#if UNITY_PSP2
            VitaLap("exceptions", questExceptionsJson);
#endif

            // Save backstory text
            if (!File.Exists(Path.Combine(path, bioFileName)))
            {
                StreamWriter file = new StreamWriter(Path.Combine(path, bioFileName).ToString());
                foreach (string line in GameManager.Instance.PlayerEntity.BackStory)
                {
                    file.WriteLine(line);
                }
                file.Close();
            }

#if UNITY_PSP2
            VitaLap("bio");
#endif
            // Save automap state
            try
            {
                Dictionary<string, Automap.AutomapDungeonState> automapState = GameManager.Instance.InteriorAutomap.GetState();
                string automapDataJson = Serialize(automapState.GetType(), automapState);
#if UNITY_PSP2
                VitaLap("automapSer", automapDataJson);
#endif
                WriteSaveFile(Path.Combine(path, automapDataFilename), automapDataJson);
            }
            catch(Exception ex)
            {
                string message = string.Format("Failed to save automap state. Message: {0}", ex.Message);
                Debug.Log(message);
            }

#if UNITY_PSP2
            VitaLap("automapWrite");
#endif
            // Save mod data
            if (ModManager.Instance != null)
            {
                foreach (Mod mod in ModManager.Instance.GetAllModsWithSaveData())
                {
                    try
                    {
                        object modData = mod.SaveDataInterface.GetSaveData();
                        if (modData != null)
                        {
                            string modDataJson = Serialize(modData.GetType(), modData);
                            WriteSaveFile(Path.Combine(path, GetModDataFilename(mod)), modDataJson);
                        }
                        else
                        {
                            File.Delete(Path.Combine(path, GetModDataFilename(mod)));
                        }
                    }
                    catch (Exception ex)
                    {
                        DaggerfallUI.AddHUDText(string.Format("Failed to save mod data for `{0}`. Check log for errors.", mod.ModInfo.ModTitle), 3);
                        DaggerfallUnity.LogMessage(string.Format("Failed to save mod data for `{0}`. Exception: {1}", mod.ModInfo.ModTitle, ex.Message), true);
                    }
                }
            }

#if UNITY_PSP2
            VitaLap("mods");
#endif
            // Save screenshot
            byte[] bytes = screenshot.EncodeToJPG();
            File.WriteAllBytes(Path.Combine(path, screenshotFilename), bytes);
#if UNITY_PSP2
            File.WriteAllBytes(Path.Combine(path, vitaThumbFilename), VitaThumbBytes(pixels, screenshot.width, screenshot.height));
            Destroy(screenshot); // nothing else holds it, every save leaked one until the next UnloadUnusedAssets
            VitaLap("jpg");
            Debug.LogFormat("VITASAVE {0} steps:{1}, JPG {2} KB, total {3:F2} s | before{4} | after{5}", saveName, vitaLapSb, bytes.Length / 1024,
                Time.realtimeSinceStartup - saveStart, vitaHeapBefore, VitaMemory.GcHeapForLog());
#endif

            // Raise OnSaveEvent
            RaiseOnSaveEvent(saveData);

            // Update saves if needed
            if (key == -1)
                EnumerateSaves();

            // Notify
            DaggerfallUI.Instance.PopupMessage(TextManager.Instance.GetLocalizedText("gameSaved"));

            // Reload this save instantly if requested
            if (instantReload)
                Load(saveData.playerData.playerEntity.name, saveName);
        }

        string[] SaveModConflictMessage(string characterName, string saveName, out string saveDataJson, out SaveData_v1 saveData)
        {
            // Init 'out' params as null so they don't carry over into LoadGame() if there is an error (!isReady, loadInProgress, key is -1)
            saveDataJson = null;
            saveData = null;

            if (ModManager.Instance == null)
                return null;

            int key = FindSaveFolderByNames(characterName, saveName);

            // Must be ready
            if (!IsReady())
                throw new Exception(notReadyExceptionText);

            // Load must not be in progress
            if (loadInProgress)
                return null;

            // Get folder
            string path;
            if (key == -1)
                return null;
            else
                path = GetSaveFolder(key);

            // Set 'out' params and read save game data
#if UNITY_PSP2
            vitaParts = VitaSaveIO.ReadSave(path);
            saveDataJson = vitaParts != null ? null : ReadSaveFile(Path.Combine(path, saveDataFilename));
            saveData = VitaDeserialize(typeof(SaveData_v1), saveDataFilename, saveDataJson) as SaveData_v1;
            vitaParts = null;
#else
            saveDataJson = ReadSaveFile(Path.Combine(path, saveDataFilename));
            saveData = Deserialize(typeof(SaveData_v1), saveDataJson) as SaveData_v1;
#endif

            // Use dictionary for faster indexing
            Dictionary<string, Mod> dict = ModManager.Instance.Mods.ToDictionary(m => m.GUID);
            // Need to use a string collection because of MessageBox's SetText method
            List<string> message = new List<string>();

            if (saveData.modInfoData != null && saveData.modInfoData.Length > 0)
            {
                Mod mod;

                // Verify that every mod recorded in the save is included in the current mod list, or has a newer version loaded
                // Otherwise add a warning for each missing mod or version conflict
                foreach (ModInfo_v1 record in saveData.modInfoData)
                {
                    if (dict.TryGetValue(record.guid, out mod))
                    {
                        if (mod.ModInfo.ModVersion != record.version)
                        {
                            bool? comp = ModManager.IsVersionLowerOrEqual(record.version, mod.ModInfo.ModVersion);

                            if (comp == false)
                            {
                                message.Add("- " + record.title + " (v. " + record.version + ")");
                                message.Add("Incoming version is older: '" + mod.Title + " (v. " + mod.ModInfo.ModVersion + ")'");
                                message.Add(String.Empty);
                            }
                            else if (comp == null)
                            {
                                message.Add("- " + record.title + " (v. " + record.version + ")");
                                message.Add("Incoming version is different: '" + mod.Title + " (v. " + mod.ModInfo.ModVersion + ")'");
                                message.Add(String.Empty);
                            }
                        }
                    }
                    else
                    {
                        message.Add("- " + record.title + " (v. " + record.version + ")");
                        message.Add("Mod is either not loaded or has been altered");
                        message.Add(String.Empty);
                    }
                }

                if (message.Count > 0)
                {
                    message.Insert(0, "The currently used mods do not match the ones used by this save:");
                    message.Insert(1, String.Empty);

                    message.Add("Errors may occur during gameplay. Proceed?");
                    message.Add(String.Empty);

                    return message.ToArray();
                }
            }

            return null;
        }

        IEnumerator LoadGame(string path)
        {
#if UNITY_PSP2
            float loadStart = Time.realtimeSinceStartup;
            VitaLapStart();
#endif
            GameManager.Instance.PlayerDeath.ClearDeathAnimation();
            GameManager.Instance.PlayerMotor.CancelMovement = true;
            InputManager.Instance.ClearAllActions();
            QuestMachine.Instance.ClearState();
            stateManager.ClearSceneCache();
            PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            playerEntity.Reset();

#if UNITY_PSP2
            VitaLap("reset");
#endif
            // Read save data from files
#if UNITY_PSP2
            // Native save (VitaSaveIO): one binary file read by generated code; older saves still load from their JSON files
            vitaParts = VitaSaveIO.ReadSave(path);
            string saveDataJson = saveDataJsonCache ?? VitaReadSaveFile(path, saveDataFilename);
            string factionDataJson = VitaReadSaveFile(path, factionDataFilename);
            string questDataJson = VitaReadSaveFile(path, questDataFilename);
            string discoveryDataJson = VitaReadSaveFile(path, discoveryDataFilename);
            string conversationDataJson = VitaReadSaveFile(path, conversationDataFilename);
            string notebookDataJson = VitaReadSaveFile(path, notebookDataFilename);
            string worldVariantsDataJson = VitaReadSaveFile(path, worldVariationDataFilename);

            // Read quest exceptions
            if (vitaParts != null ? vitaParts.ContainsKey(questExceptionsFilename) : File.Exists(Path.Combine(path, questExceptionsFilename)))
            {
                string questExceptionsJson = VitaReadSaveFile(path, questExceptionsFilename);
                QuestMachine.StoredException[] storedExceptions = VitaDeserialize(typeof(QuestMachine.StoredException[]), questExceptionsFilename, questExceptionsJson) as QuestMachine.StoredException[];
#else
            string saveDataJson = saveDataJsonCache ?? ReadSaveFile(Path.Combine(path, saveDataFilename));
            string factionDataJson = ReadSaveFile(Path.Combine(path, factionDataFilename));
            string questDataJson = ReadSaveFile(Path.Combine(path, questDataFilename));
            string discoveryDataJson = ReadSaveFile(Path.Combine(path, discoveryDataFilename));
            string conversationDataJson = ReadSaveFile(Path.Combine(path, conversationDataFilename));
            string notebookDataJson = ReadSaveFile(Path.Combine(path, notebookDataFilename));
            string worldVariantsDataJson = ReadSaveFile(Path.Combine(path, worldVariationDataFilename));

            // Read quest exceptions
            if (File.Exists(Path.Combine(path, questExceptionsFilename)))
            {
                string questExceptionsJson = ReadSaveFile(Path.Combine(path, questExceptionsFilename));
                QuestMachine.StoredException[] storedExceptions = Deserialize(typeof(QuestMachine.StoredException[]), questExceptionsJson) as QuestMachine.StoredException[];
#endif
                QuestMachine.Instance.SetStoredExceptions(storedExceptions);
            }

            // Load backstory text
            playerEntity.BackStory = new List<string>();
#if UNITY_PSP2
            byte[] vitaBio;
            if (vitaParts != null && vitaParts.TryGetValue(bioFileName, out vitaBio))
                playerEntity.BackStory.AddRange(System.Text.Encoding.UTF8.GetString(vitaBio).Split('\n'));
            else
#endif
            if (File.Exists(Path.Combine(path, bioFileName)))
            {
                StreamReader file = new StreamReader(Path.Combine(path, bioFileName).ToString());
                string line;
                while ((line = file.ReadLine()) != null)
                {
                    playerEntity.BackStory.Add(line);
                }
                file.Close();
            }

#if UNITY_PSP2
            VitaLap("read", saveDataJson + factionDataJson + questDataJson + discoveryDataJson + conversationDataJson + notebookDataJson + worldVariantsDataJson);
#endif
            // Deserialize JSON strings
#if UNITY_PSP2
            SaveData_v1 saveData = saveDataCache ?? VitaDeserialize(typeof(SaveData_v1), saveDataFilename, saveDataJson) as SaveData_v1;
#else
            SaveData_v1 saveData = saveDataCache ?? Deserialize(typeof(SaveData_v1), saveDataJson) as SaveData_v1;
#endif
#if UNITY_PSP2
            VitaLap("dSave", saveDataJson);
#endif

            // Must have a serializable player
            if (!stateManager.SerializablePlayer)
                yield break;

            // Call start load event
            RaiseOnStartLoadEvent(saveData);

            // Immediately set date so world is loaded with correct season
            RestoreDateTimeData(saveData.dateAndTime);

            // When loading an interior save, restore world compensation height early before initworld
            // Ensures exterior world level is aligned with building height at time of save
            // Only works with floating origin v3 saves and above with both serialized world compensation and context
            if (saveData.playerData.playerPosition.worldContext == WorldContext.Interior)
                GameManager.Instance.StreamingWorld.RestoreWorldCompensationHeight(saveData.playerData.playerPosition.worldCompensation.y);
            else
                GameManager.Instance.StreamingWorld.RestoreWorldCompensationHeight(0);

            // Restore discovery data
#if UNITY_PSP2
            if (VitaHasPart(discoveryDataFilename, discoveryDataJson))
            {
                Dictionary<int, PlayerGPS.DiscoveredLocation> discoveryData =
                    VitaDeserialize(typeof(Dictionary<int, PlayerGPS.DiscoveredLocation>), discoveryDataFilename, discoveryDataJson) as Dictionary<int, PlayerGPS.DiscoveredLocation>;
#else
            if (!string.IsNullOrEmpty(discoveryDataJson))
            {
                Dictionary<int, PlayerGPS.DiscoveredLocation> discoveryData =
                    Deserialize(typeof(Dictionary<int, PlayerGPS.DiscoveredLocation>), discoveryDataJson) as Dictionary<int, PlayerGPS.DiscoveredLocation>;
#endif
#if UNITY_PSP2
                VitaLap("dDisc", discoveryDataJson);
#endif
                GameManager.Instance.PlayerGPS.RestoreDiscoveryData(discoveryData);
#if UNITY_PSP2
                VitaLap("rDisc");
#endif
            }
            else
            {
                // Clear discovery data when not in save, or live state will be retained from previous session
                GameManager.Instance.PlayerGPS.ClearDiscoveryData();
            }

            // Must have PlayerEnterExit to respawn player at saved location
            PlayerEnterExit playerEnterExit = stateManager.SerializablePlayer.GetComponent<PlayerEnterExit>();
            if (!playerEnterExit)
                yield break;

            // Restore building summary, house ownership, and guild membership early for interior layout code
            if (saveData.playerData.playerPosition.insideBuilding)
            {
                playerEnterExit.BuildingDiscoveryData = saveData.playerData.playerPosition.buildingDiscoveryData;
                playerEnterExit.IsPlayerInsideOpenShop = saveData.playerData.playerPosition.insideOpenShop;
                if (saveData.bankDeeds != null)
                    RestoreHousesData(saveData.bankDeeds.houses);
                GameManager.Instance.GuildManager.RestoreMembershipData(saveData.playerData.guildMemberships);
                GameManager.Instance.GuildManager.RestoreMembershipData(saveData.playerData.vampireMemberships, true);
            }

            // Restore faction data to player entity
            // This is done early as later objects may require faction information on restore
#if UNITY_PSP2
            if (VitaHasPart(factionDataFilename, factionDataJson))
            {
                FactionData_v2 factionData = VitaDeserialize(typeof(FactionData_v2), factionDataFilename, factionDataJson) as FactionData_v2;
#else
            if (!string.IsNullOrEmpty(factionDataJson))
            {
                FactionData_v2 factionData = Deserialize(typeof(FactionData_v2), factionDataJson) as FactionData_v2;
#endif
#if UNITY_PSP2
                VitaLap("dFaction", factionDataJson);
#endif
                stateManager.RestoreFactionData(factionData);
#if UNITY_PSP2
                VitaLap("rFaction");
#endif
                Debug.Log("LoadGame() restored faction state from save.");
            }
            else
            {
                Debug.Log("LoadGame() did not find saved faction data. Player will resume with default faction state.");
            }

            // Restore quest machine state
#if UNITY_PSP2
            if (VitaHasPart(questDataFilename, questDataJson))
            {
                QuestMachine.QuestMachineData_v1 questData = VitaDeserialize(typeof(QuestMachine.QuestMachineData_v1), questDataFilename, questDataJson, true) as QuestMachine.QuestMachineData_v1;
#else
            if (!string.IsNullOrEmpty(questDataJson))
            {
                QuestMachine.QuestMachineData_v1 questData = Deserialize(typeof(QuestMachine.QuestMachineData_v1), questDataJson, true) as QuestMachine.QuestMachineData_v1;
#endif
#if UNITY_PSP2
                VitaLap("dQuest", questDataJson);
#endif
                QuestMachine.Instance.RestoreSaveData(questData);
#if UNITY_PSP2
                VitaLap("rQuest");
#endif
            }

            // Restore conversation data (must be done after quest data restoration)
#if UNITY_PSP2
            if (VitaHasPart(conversationDataFilename, conversationDataJson))
            {
                TalkManager.SaveDataConversation conversationData = VitaDeserialize(typeof(TalkManager.SaveDataConversation), conversationDataFilename, conversationDataJson) as TalkManager.SaveDataConversation;
#else
            if (!string.IsNullOrEmpty(conversationDataJson))
            {
                TalkManager.SaveDataConversation conversationData = Deserialize(typeof(TalkManager.SaveDataConversation), conversationDataJson) as TalkManager.SaveDataConversation;
#endif
#if UNITY_PSP2
                VitaLap("dTalk", conversationDataJson);
#endif
                GameManager.Instance.TalkManager.RestoreConversationData(conversationData);
#if UNITY_PSP2
                VitaLap("rTalk");
#endif
            }
            else
            {
                GameManager.Instance.TalkManager.RestoreConversationData(null);
            }

            // Restore notebook data
#if UNITY_PSP2
            if (VitaHasPart(notebookDataFilename, notebookDataJson))
            {
                PlayerNotebook.NotebookData_v1 notebookData = VitaDeserialize(typeof(PlayerNotebook.NotebookData_v1), notebookDataFilename, notebookDataJson) as PlayerNotebook.NotebookData_v1;
#else
            if (!string.IsNullOrEmpty(notebookDataJson))
            {
                PlayerNotebook.NotebookData_v1 notebookData = Deserialize(typeof(PlayerNotebook.NotebookData_v1), notebookDataJson) as PlayerNotebook.NotebookData_v1;
#endif
#if UNITY_PSP2
                VitaLap("dNote", notebookDataJson);
#endif
                playerEntity.Notebook.RestoreNotebookData(notebookData);
#if UNITY_PSP2
                VitaLap("rNote");
#endif
            }

            // Try to restore WorldData variants data
            // If this fails for some reason then whole game will fail loading
            // Handle exception, display an informational message, and try to keep loading
            try
            {
#if UNITY_PSP2
                if (VitaHasPart(worldVariationDataFilename, worldVariantsDataJson))
                {
                    WorldDataVariants.WorldVariationData_v1 worldVariantsData = VitaDeserialize(typeof(WorldDataVariants.WorldVariationData_v1), worldVariationDataFilename, worldVariantsDataJson) as WorldDataVariants.WorldVariationData_v1;
#else
                if (!string.IsNullOrEmpty(worldVariantsDataJson))
                {
                    WorldDataVariants.WorldVariationData_v1 worldVariantsData = Deserialize(typeof(WorldDataVariants.WorldVariationData_v1), worldVariantsDataJson) as WorldDataVariants.WorldVariationData_v1;
#endif
                    WorldDataVariants.RestoreWorldVariationData(worldVariantsData);
#if UNITY_PSP2
                    VitaLap("worldVar", worldVariantsDataJson);
#endif
                }
            }
            catch (Exception ex)
            {
                DaggerfallUI.AddHUDText("Failed to load `WorldVariationData`. Load will try to continue without world variants.", 3);
                DaggerfallUnity.LogMessage(string.Format("Failed to load world variants. `WorldVariationData.txt` may be corrupt or variants failed to restore. Exception: {0}", ex.Message), true);
            }

#if UNITY_PSP2
            // Loading window over the whole respawn: a load from the title already shows one, a load from the pause menu doesn't.
            // Two drawn frames before the world is torn down and rebuilt (the dungeon builds in one long frame)
            VitaLoadWindow = DaggerfallUI.UIManager.TopWindow as VitaLoadingWindow;
            if (VitaLoadWindow == null)
            {
                yield return null;      // the save window calls Load, then PopToHUD: push after that
                VitaLoadWindow = new VitaLoadingWindow(DaggerfallUI.UIManager, saveData.playerData.playerPosition.insideDungeon ? "Dungeon" : null);
                DaggerfallUI.UIManager.PushWindow(VitaLoadWindow);
                yield return null;
                yield return null;
            }
#endif

#if UNITY_PSP2
            VitaLap("window");
#endif
            // Restore player position to world
            playerEnterExit.RestorePositionHelper(saveData.playerData.playerPosition, true, false);

            //Restore Travel Map settings
            DaggerfallUI.Instance.DfTravelMapWindow.SetTravelMapFromSaveData(saveData.travelMapData);

            // Restore climbing state
            GameManager.Instance.ClimbingMotor.RestoreSaveData(saveData.advancedClimbingState);

            // Smash to black while respawning
            DaggerfallUI.Instance.FadeBehaviour.SmashHUDToBlack();

            // Keep yielding frames until world is ready again
            while (playerEnterExit.IsRespawning)
            {
                yield return new WaitForEndOfFrame();
            }

            // Wait another frame so everthing has a chance to register
            yield return new WaitForEndOfFrame();

#if UNITY_PSP2
            VitaLap("respawn");
#endif
            // Restore save data to objects in newly spawned world
#if UNITY_PSP2
            // An exception here ended this coroutine without a log line anywhere:
            // log it and finish the load
            try
            {
                RestoreSaveData(saveData);
            }
            catch (Exception ex)
            {
                Debug.LogFormat("VITALOAD RestoreSaveData failed: {0}", ex);
            }
#else
            RestoreSaveData(saveData);
#endif
#if UNITY_PSP2
            VitaLap("restoreObjects");
#endif

            // Load automap state
            try
            {
#if UNITY_PSP2
                string automapDataJson = VitaReadSaveFile(path, automapDataFilename);
                Dictionary<string, Automap.AutomapDungeonState> automapState = null;

                if (VitaHasPart(automapDataFilename, automapDataJson))
                    automapState = VitaDeserialize(typeof(Dictionary<string, Automap.AutomapDungeonState>), automapDataFilename, automapDataJson) as Dictionary<string, Automap.AutomapDungeonState>;
#else
                string automapDataJson = ReadSaveFile(Path.Combine(path, automapDataFilename));
                Dictionary<string, Automap.AutomapDungeonState> automapState = null;

                if (!string.IsNullOrEmpty(automapDataJson))
                    automapState = Deserialize(typeof(Dictionary<string, Automap.AutomapDungeonState>), automapDataJson) as Dictionary<string, Automap.AutomapDungeonState>;
#endif

                if (automapState != null)
                    GameManager.Instance.InteriorAutomap.SetState(automapState);
            }
            catch (Exception ex)
            {
                string message = string.Format("Failed to load automap state. Message: {0}", ex.Message);
                Debug.Log(message);
            }

#if UNITY_PSP2
            VitaLap("automap");
#endif
            // Clear any orphaned quest items
            RemoveAllOrphanedItems();
#if UNITY_PSP2
            VitaLap("orphans");
#endif

            // Check mod manager is available
            if (ModManager.Instance != null)
            {
                // Restore mod data
                foreach (Mod mod in ModManager.Instance.GetAllModsWithSaveData())
                {
                    try
                    {
                        string modDataPath = Path.Combine(path, GetModDataFilename(mod));
                        object modData;
#if UNITY_PSP2
                        byte[] vitaModData;
                        if (vitaParts != null && vitaParts.TryGetValue(GetModDataFilename(mod), out vitaModData))
                            modData = Deserialize(mod.SaveDataInterface.SaveDataType, System.Text.Encoding.UTF8.GetString(vitaModData));
                        else
#endif
                        if (File.Exists(modDataPath))
                            modData = Deserialize(mod.SaveDataInterface.SaveDataType, ReadSaveFile(modDataPath));
                        else
                            modData = mod.SaveDataInterface.NewSaveData();
                        mod.SaveDataInterface.RestoreSaveData(modData);
                    }
                    catch (Exception ex)
                    {
                        DaggerfallUI.AddHUDText(string.Format("Failed to load mod data for `{0}`. Check log for errors.", mod.ModInfo.ModTitle), 3);
                        DaggerfallUnity.LogMessage(string.Format("Failed to load mod data for `{0}`. Exception: {1}", mod.ModInfo.ModTitle, ex.Message), true);
                    }
                }
            }

#if UNITY_PSP2
            VitaLap("mods");
#endif
#if UNITY_PSP2
            vitaParts = null;   // the save file's bytes: done with them
#endif
            // Clamp legal reputation
            playerEntity.ClampLegalReputations();

            // Lower load in progress flag
            loadInProgress = false;

#if UNITY_PSP2
            // Outside, locations keep building after the respawn: wait for them (60 s at most), as the dungeon exit does
            float vitaStart = Time.realtimeSinceStartup;
            while (GameManager.Instance.StreamingWorld.VitaIsBuilding && Time.realtimeSinceStartup - vitaStart < 60)
                yield return null;
            if (VitaLoadWindow != null && DaggerfallUI.UIManager.TopWindow == VitaLoadWindow)
                VitaLoadWindow.CloseWindow();
            VitaLoadWindow = null;
            VitaLap("buildWait");
            Debug.LogFormat("VITALOAD steps:{0}, total {1:F2} s", vitaLapSb, Time.realtimeSinceStartup - loadStart);
#endif

            // Fade out from black
            DaggerfallUI.Instance.FadeBehaviour.FadeHUDFromBlack(1.0f);

            // Raise OnLoad event
            RaiseOnLoadEvent(saveData);
        }

        /// <summary>
        /// Looks for orphaned items (e.g. quest no longer active or invalid template) remaining in player item collections.
        /// </summary>
        void RemoveAllOrphanedItems()
        {
            int count = 0;
            Entity.PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
            count += playerEntity.Items.RemoveOrphanedItems();
            count += playerEntity.WagonItems.RemoveOrphanedItems();
            count += playerEntity.OtherItems.RemoveOrphanedItems();
            if (count > 0)
            {
                Debug.LogFormat("Removed {0} orphaned items.", count);
            }
        }

        private static string GetModDataFilename(Mod mod)
        {
            // Use filename because title may contains invalid path chars.
            return string.Format("mod_{0}.txt", mod.FileName);
        }

        #endregion

        #region Events

        // OnSave
        public delegate void OnSaveEventHandler(SaveData_v1 saveData);
        public static event OnSaveEventHandler OnSave;
        protected virtual void RaiseOnSaveEvent(SaveData_v1 saveData)
        {
            if (OnSave != null)
                OnSave(saveData);
        }

        // OnStartLoad
        public delegate void OnStartLoadEventHandler(SaveData_v1 saveData);
        public static event OnStartLoadEventHandler OnStartLoad;
        protected virtual void RaiseOnStartLoadEvent(SaveData_v1 saveData)
        {
            if (OnStartLoad != null)
                OnStartLoad(saveData);
        }

        // OnLoad
        public delegate void OnLoadEventHandler(SaveData_v1 saveData);
        public static event OnLoadEventHandler OnLoad;
        protected virtual void RaiseOnLoadEvent(SaveData_v1 saveData)
        {
            if (OnLoad != null)
                OnLoad(saveData);
        }

        // List of conditions that could prevent saving
        private List<Func<bool>> PreventSaveConditions = new List<Func<bool>>();
        public void RegisterPreventSaveCondition(Func<bool> handler)
        {
            PreventSaveConditions.Add(handler);
        }

        public void UnregisterPreventSaveCondition(Func<bool> handler)
        {
            PreventSaveConditions.Remove(handler);
        }

        #endregion
    }
}
