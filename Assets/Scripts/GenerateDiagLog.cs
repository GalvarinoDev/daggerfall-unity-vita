// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Lypyl (lypyl@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using UnityEngine;
using DaggerfallWorkshop;
using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;


namespace DaggerfallWorkshop
{
    public class GenerateDiagLog
    {

#if UNITY_PSP2
        static bool printed;
#endif

        public static void PrintInfo(string path)
        {
#if UNITY_PSP2
            // DaggerfallUnity validates the path once per scene (3x per boot) and each pass took ~4.7 s on hardware: write it once per launch
            if (printed)
                return;
            printed = true;
            // Several random seeds use DateTime.Now.Millisecond: if Now never moves, they always seed the same
            DaggerfallUnity.LogMessage(string.Format("VITACLOCK DateTime.Now {0:yyyy-MM-dd HH:mm:ss.fff}, Vita clock {1:yyyy-MM-dd HH:mm:ss.fff}",
                DateTime.Now, VitaClock.Now), true);
#endif
            string fileName = "DFTFU_Environment.log";
            StringBuilder builder = new StringBuilder();
            bool check = false;


            check = GetDFTFUSettings(builder);
            if (!check)
                builder.AppendLine(string.Format(" !!! error in GetDFTFUSettings !!! ")).AppendLine(Environment.NewLine);

            check = GetSystemAndCultureInfo(builder);

            if (!check)
                builder.AppendLine(string.Format("!!! error in GetSystemAndCultureInfo !!! ")).AppendLine(Environment.NewLine);

            check = GetSystemInfo(builder);

            if (!check)
                builder.AppendLine(string.Format("!!! error in GetSystemInfo !!! ")).AppendLine(Environment.NewLine);

#if UNITY_PSP2
            // Real Vita: listing ARENA2 (1441 files) takes ~100 s per pass (~70 ms per entry), this walk needs several
            DaggerfallUnity.LogMessage("GetDirs skipped on Vita");
#else
            check = GetDirs(path, builder);

            if (!check)
                builder.AppendLine(string.Format("!!! error in GetDirs !!! ")).AppendLine(Environment.NewLine);
#endif


#if UNITY_PSP2
            DaggerfallUnity.LogMessage("Environment:\n" + builder.ToString().Replace(Environment.NewLine + Environment.NewLine, Environment.NewLine));
#endif
            OutPutToFile(builder, fileName);




        }


        private static bool GetDFTFUSettings(StringBuilder builder)
        {
            if (builder == null)
            {
                DaggerfallUnity.LogMessage("Builder was null");
                builder = new StringBuilder();
            }

            builder.Append("### Start DFTFU Settings Manager Properties ### ").AppendLine(Environment.NewLine);

            var dftfuProps = typeof(SettingsManager).GetProperties();
            foreach (var prop in dftfuProps)
            {
                try
                {
                    var value = prop.GetValue(DaggerfallUnity.Settings, null);
#if !UNITY_PSP2   // one block at the end instead: each Debug.Log line cost 7-14 ms on hardware
                    DaggerfallUnity.LogMessage(string.Format("{0} | {1}", prop.Name, value));
#endif
                    builder.AppendLine(string.Format("{0} | {1} ", prop.Name, value)).AppendLine(Environment.NewLine);

                }

                catch (Exception ex)
                {
                    DaggerfallUnity.LogMessage(ex.Message);
                    builder.AppendLine(string.Format("*** Could not read value for: {0} *** ", prop.Name)).AppendLine(Environment.NewLine);

                }

            }


            try
            {

                builder.AppendLine(string.Format("IsReady: | {0} ", DaggerfallUnity.Instance.IsReady)).AppendLine(Environment.NewLine);

                builder.AppendLine(string.Format("Arena2Path: | {0} ", DaggerfallUnity.Instance.Arena2Path)).AppendLine(Environment.NewLine);

                builder.AppendLine(string.Format("IsPathValidated: | {0} ", DaggerfallUnity.Instance.IsPathValidated)).AppendLine(Environment.NewLine);

                builder.AppendLine(string.Format("### End DFTFU Settings Manager Properties ### ")).AppendLine(Environment.NewLine).AppendLine(Environment.NewLine);

            }
            catch (Exception ex)
            {
                DaggerfallUnity.LogMessage(ex.Message);
                builder.AppendLine(string.Format("!!!! Error in GETDFTFUSettings: {0} | {1} | {2} !!!", ex.Message, ex.InnerException, ex.Source)).AppendLine(Environment.NewLine);
                return false;


            }

            return true;


        }

        private static bool GetSystemAndCultureInfo(StringBuilder builder)
        {
            if (builder == null)
            {
                DaggerfallUnity.LogMessage("Builder was null");
                builder = new StringBuilder();

            }


            try
            {
                CultureInfo currentCult = CultureInfo.CurrentCulture;

                var cultProps = typeof(CultureInfo).GetProperties();
                var envProps = typeof(Environment).GetProperties();
                builder.AppendLine(string.Format("### Start Enviornment Info ###")).AppendLine(Environment.NewLine);

                foreach (var prop in envProps)
                {
                    var value = prop.GetValue(currentCult, null);
                    if (value.ToString() == Environment.UserName || value.ToString() == Environment.UserDomainName || prop.Name == "StackTrace")
                        continue;
#if !UNITY_PSP2   // one block at the end instead: each Debug.Log line cost 7-14 ms on hardware
                    DaggerfallUnity.LogMessage(string.Format("{0} | {1}", prop.Name, value));
#endif
                    builder.AppendLine(string.Format("{0} | {1} ", prop.Name, value)).AppendLine(Environment.NewLine);

                }

                builder.Append(string.Format("### Start Culture Info ###")).AppendLine(Environment.NewLine).AppendLine(Environment.NewLine);
                foreach (var prop in cultProps)
                {
#if !UNITY_PSP2   // one block at the end instead: each Debug.Log line cost 7-14 ms on hardware
                    DaggerfallUnity.LogMessage(string.Format("{0} | {1}", prop.Name, prop.GetValue(currentCult, null)));
#endif

                    builder.AppendLine(string.Format("{0} | {1}", prop.Name, prop.GetValue(currentCult, null))).AppendLine(Environment.NewLine);

                }

                builder.AppendLine(string.Format("### End Culture Properties ### ")).AppendLine(Environment.NewLine).AppendLine(Environment.NewLine);

            }
            catch (Exception ex)
            {
                DaggerfallUnity.LogMessage(ex.Message);
                builder.AppendLine(string.Format("!!! Error in GetSystemAndCultureInfo: {0} !!!", ex.Message)).AppendLine(Environment.NewLine);
                return false;
            }
            return true;


        }


        private static bool GetSystemInfo(StringBuilder builder)
        {

            try
            {
                SystemInfo sysInfo = new SystemInfo();
                var sysInfoProps = typeof(SystemInfo).GetProperties();
                builder.AppendLine(string.Format("### Start System Info ###")).AppendLine(Environment.NewLine);

                foreach (var prop in sysInfoProps)
                {
                    var value = prop.GetValue(sysInfo, null);
                    if (value.ToString() == SystemInfo.deviceName || value.ToString() == SystemInfo.deviceUniqueIdentifier)
                        continue;
#if !UNITY_PSP2   // one block at the end instead: each Debug.Log line cost 7-14 ms on hardware
                    DaggerfallUnity.LogMessage(string.Format("{0} | {1}", prop.Name, value));
#endif
                    builder.AppendLine(string.Format("{0} | {1} ", prop.Name, value)).AppendLine(Environment.NewLine);
                }

            }
            catch(Exception ex)
            {
                DaggerfallUnity.LogMessage(ex.Message);
                builder.AppendLine(string.Format("!!! Error in GetSystemInfo: {0} !!!", ex.Message)).AppendLine(Environment.NewLine);
            }

            return true;

        }


        private static bool GetDirs(string path, StringBuilder builder)
        {
            if (builder == null)
            {
                DaggerfallUnity.LogMessage("Builder was null");
                builder = new StringBuilder();

            }

            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                builder.AppendLine(string.Format("!!! Path: {0} invalid; trying DaggerfallUnity.Arena2 instead !!! ", path)).AppendLine(Environment.NewLine);
                try
                {
                    path = DaggerfallUnity.Instance.Arena2Path;
                    path = path.Substring(0, path.Length - 6);
                    if (!Directory.Exists(path))
                    {
                        builder.AppendLine(string.Format("!!! No valid path found, stoping !!!")).AppendLine(Environment.NewLine);
                        return false;
                    }

                }
                catch (Exception ex)
                {
                    DaggerfallUnity.LogMessage("Error in GetDirs()");

                    builder.AppendLine(string.Format("!!! Error in GETDirs() : {0} !!!", ex.Message)).AppendLine(Environment.NewLine);
                    return false;

                }


            }

            try
            {
                builder.AppendLine(string.Format("### START FILE CHECK ###")).AppendLine(Environment.NewLine);

                var subDirs = Directory.GetDirectories(path, "*", SearchOption.AllDirectories);
                var files = GetFiles(path);
                PrintDirectoryInfo(path, files, builder);

                foreach (string subDir in subDirs)
                {
                    files = GetFiles(subDir);
                    PrintDirectoryInfo(subDir, files, builder);

                }
            }
            catch (Exception ex)
            {
                DaggerfallUnity.LogMessage("Error in GetDirs()");
                builder.AppendLine(string.Format("!!!! Error in GETDirs() : {0} !!! ", ex.Message)).AppendLine(Environment.NewLine);
                return false;
            }


            builder.AppendLine(string.Format("### END FILE CHECK ###")).AppendLine(Environment.NewLine).AppendLine(Environment.NewLine);
            return true;
        }

        private static string[] GetFiles(string path)
        {
            var files = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly);
            if (files != null)
            {
                return files;
            }
            else
            {
                return null;

            }
        }

        private static void PrintDirectoryInfo(string dir, string[] files, StringBuilder builder)
        {
            if (string.IsNullOrEmpty(dir))
            {
                builder.AppendLine("!!! Invalid directory string !!!").AppendLine(Environment.NewLine);
            }
            else
            {
                builder.AppendLine(Environment.NewLine).AppendLine(string.Format("\t\t *** DIRECTORY PATH: {0} ***", dir));

            }
            if (files == null || files.Length < 1)
            {
                builder.AppendLine("!!! No file names found in directory !!!");
            }

            foreach (string file in files)
            {
                builder.AppendLine(string.Format("File Path: {0} ", file));

            }

        }


        private static void OutPutToFile(StringBuilder builder, string fileName)
        {
            FileStream fs = null;
            StreamWriter sw = null;
            try
            {
                string filePath = Path.Combine(DaggerfallUnity.Settings.PersistentDataPath, fileName);
                fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.ReadWrite);
                sw = new StreamWriter(fs);
                if (File.Exists(filePath))
                {
                    sw.Write(builder.ToString());

                }
                else
                {
                    Debug.Log("No file to write to");

                }



            }
            catch (Exception ex)
            {
                Debug.Log(ex.Message);

            }

            try
            {
                if (sw != null)
                    sw.Close();
                if (fs != null)
                    fs.Close();
            }
            catch
            {

            }
        }

    }
}
