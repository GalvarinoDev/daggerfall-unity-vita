// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    
// 
// Notes:
//

using UnityEngine;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Utility.AssetInjection;

namespace DaggerfallWorkshop.Game.UserInterfaceWindows
{
    /// <summary>
    /// Implements a VID player.
    /// </summary>
    public class DaggerfallVidPlayerWindow : DaggerfallBaseWindow
    {

        DaggerfallVideo video;
        VideoPlayerDrawer customVideo;
        bool useCustomVideo = false;

        bool hideCursor = true;
        bool endOnAnyKey = true;

        public string PlayOnStart { get; set; }

        public bool HideCursor
        {
            get { return hideCursor; }
            set { hideCursor = value; }
        }

        public DaggerfallVideo Video
        {
            get { return video; }
        }

        public VideoPlayerDrawer CustomVideo
        {
            get { return customVideo; }
        }

        public bool UseCustomVideo
        {
            get { return useCustomVideo; }
        }

        public bool IsPlaying
        {
            get { return useCustomVideo ? (customVideo != null && customVideo.IsPlaying) : (video != null && video.Playing); }
        }

        public bool EndOnAnyKey
        {
            get { return endOnAnyKey; }
            set { endOnAnyKey = value; }
        }

        public DaggerfallVidPlayerWindow(IUserInterfaceManager uiManager)
            : base(uiManager)
        {
        }

        public DaggerfallVidPlayerWindow(IUserInterfaceManager uiManager, string playOnStart)
            : base(uiManager)
        {
            PlayOnStart = playOnStart;
        }

        public DaggerfallVidPlayerWindow(IUserInterfaceManager uiManager, string playOnStart, bool endOnAnyKey)
            : base(uiManager)
        {
            PlayOnStart = playOnStart;
            EndOnAnyKey = endOnAnyKey;
        }

        protected override void Setup()
        {
            if (useCustomVideo = VideoReplacement.TryImportMovie(PlayOnStart, out customVideo))
            {
                // Play custom video
                customVideo.Size = new Vector2(Screen.width, Screen.height);
                NativePanel.Components.Add(customVideo);
                DoHideCursor(hideCursor);

                customVideo.Play();
                RaiseOnVideoStartGlobalEvent();
            }
            else
            {
                const int nativeScreenWidth = 320;
                const int nativeScreenHeight = 200;

                // Add video player control
                video = new DaggerfallVideo();
                video.HorizontalAlignment = HorizontalAlignment.Center;
                video.Size = new Vector2(nativeScreenWidth, nativeScreenHeight);
                NativePanel.Components.Add(video);
                DoHideCursor(hideCursor);

                // Start playing
                if (!string.IsNullOrEmpty(PlayOnStart))
                {
                    video.Open(PlayOnStart);
                    video.Playing = true;
                    RaiseOnVideoStartGlobalEvent();
#if UNITY_PSP2
                    StartSubtitles();
#endif
                }
            }
        }

#if UNITY_PSP2
        // Translated builds ship subtitles for the original videos in the Vita_Subtitles string table: key = video file,
        // value = cues "start end text" (seconds from the start) joined by '#'. English has no table, so nothing is drawn.
        string[] subCues;
        TextLabel subLabel;
        double subStart;
        int subShown = -1;

        void StartSubtitles()
        {
            string value;
            if (!DaggerfallWorkshop.Utility.TextProvider.GetVitaStringTable("Vita_Subtitles").TryGetValue(PlayOnStart.ToUpperInvariant(), out value))
                return;
            subCues = value.Split('#');
            subStart = AudioSettings.dspTime;
            subLabel = new TextLabel();
            subLabel.Font = DaggerfallUI.DefaultFont;
            subLabel.MaxWidth = 312;
            subLabel.WrapText = true;
            subLabel.WrapWords = true;
            subLabel.HorizontalTextAlignment = TextLabel.HorizontalTextAlignmentSetting.Center;
            subLabel.HorizontalAlignment = HorizontalAlignment.Center;
            subLabel.VerticalAlignment = VerticalAlignment.Bottom;
            subLabel.ShadowPosition = DaggerfallUI.DaggerfallDefaultShadowPos;
            NativePanel.Components.Add(subLabel);
        }

        void UpdateSubtitles()
        {
            if (subCues == null)
                return;
            double t = AudioSettings.dspTime - subStart;
            int show = -1;
            for (int i = 0; i < subCues.Length; i++)
            {
                string[] p = subCues[i].Split(new char[] { ' ' }, 3);
                if (t >= double.Parse(p[0], System.Globalization.CultureInfo.InvariantCulture) && t < double.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture))
                    show = i;
            }
            if (show == subShown)
                return;
            subShown = show;
            subLabel.Text = show < 0 ? string.Empty : subCues[show].Split(new char[] { ' ' }, 3)[2];
        }
#endif

#if UNITY_PSP2
        public override void OnPush()
        {
            base.OnPush();
            VitaBootMusic.StopTitleSong();
        }

#endif
        public override void Update()
        {
            base.Update();

            // Handle exit any key or end of video
            if (useCustomVideo)
            {
                if (endOnAnyKey && InputManager.Instance.AnyKeyDownIgnoreAxisBinds ||
                    InputManager.Instance.GetBackButtonDown() ||
                    !customVideo.IsPlaying)
                {
                    customVideo.Stop();
                    customVideo.Dispose();
                    customVideo = null;

                    DoHideCursor(false);
                    RaiseOnVideoFinishedHandler();
                    RaiseOnVideoEndGlobalEvent();
                    CloseWindow();
                }
            }
            else
            {
#if UNITY_PSP2
                UpdateSubtitles();
#endif
                if (endOnAnyKey && InputManager.Instance.AnyKeyDownIgnoreAxisBinds ||
                    InputManager.Instance.GetBackButtonDown() ||
                    video.VidFile.EndOfFile && video.Playing)
                {
                    video.Playing = false;
                    video.Dispose();
                    video = null;

                    DoHideCursor(false);
                    RaiseOnVideoFinishedHandler();
                    RaiseOnVideoEndGlobalEvent();
                    CloseWindow();
                }
            }
        }

        private void DoHideCursor(bool hide)
        {
            PlayerMouseLook mLook = GameManager.Instance.PlayerMouseLook;
            if (mLook != null)
                mLook.ForceHideCursor(hide && hideCursor);
        }

        #region Event Handlers

        // OnVideoStart (global)
        public delegate void OnVideoStartEventHandler();
        public static event OnVideoStartEventHandler OnVideoStart;
        protected virtual void RaiseOnVideoStartGlobalEvent()
        {
            if (OnVideoStart != null)
                OnVideoStart();
        }

        // OnVideoEnd (global)
        public delegate void OnVideoEndEventHandler();
        public static event OnVideoEndEventHandler OnVideoEnd;
        protected virtual void RaiseOnVideoEndGlobalEvent()
        {
            if (OnVideoEnd != null)
                OnVideoEnd();
        }

        // OnVideoFinished
        public delegate void OnVideoFinishedHandler();
        public event OnVideoFinishedHandler OnVideoFinished;
        protected virtual void RaiseOnVideoFinishedHandler()
        {
            if (OnVideoFinished != null)
                OnVideoFinished();
        }

        #endregion
    }
}