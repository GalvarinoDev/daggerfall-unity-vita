// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: Gavin Clayton (interkarma@dfworkshop.net)
// Contributors:    Hazelnut, Kirk.O, Numidium
// 
// Notes:
//

using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using DaggerfallConnect;
using DaggerfallConnect.Utility;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Utility.AssetInjection;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Serialization;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallWorkshop.Game.Items;

namespace DaggerfallWorkshop.Game
{
    /// <summary>
    /// Renders first-person weapons and attack animations.
    /// Recommended for this component to be on its own game object.
    /// Cam modify pitch of audio source for different weapon speed effects.
    /// </summary>
    [RequireComponent(typeof(DaggerfallAudioSource))]
    public class FPSWeapon : MonoBehaviour
    {
        class WeaponAtlas
        {
            public string FileName { get; set; }
            public MetalTypes MetalType { get; set; }
            public Texture2D AtlasTexture { get; set; }
            public Rect[] WeaponRects { get; set; }
            public RecordIndex[] WeaponIndices { get; set; }
#if UNITY_PSP2
            public Texture2D PaletteTexture { get; set; }   // set: AtlasTexture holds palette indices (VitaWeaponPack)
#endif
        }

        class CustomWeaponAnimation
        {
            public string FileName { get; set; }
            public MetalTypes MetalType { get; set; }
            public Dictionary<int, Texture2D> Textures { get; set; }
        }

        public bool ShowWeapon = true;
        public bool FlipHorizontal = false;
        public WeaponTypes WeaponType = WeaponTypes.None;
        public MetalTypes MetalType = MetalTypes.None;
        public ItemHands WeaponHands = ItemHands.None;
        public DaggerfallUnityItem SpecificWeapon = null;
        public float Reach = 2.5f;
        public float AttackSpeedScale = 1.0f;
        public float Cooldown = 0.0f;
        public SoundClips DrawWeaponSound = SoundClips.DrawWeapon;
        public SoundClips SwingWeaponSound = SoundClips.SwingMediumPitch;

        WeaponTypes currentWeaponType;
        MetalTypes currentMetalType;

        const int nativeScreenWidth = 320;
        const int nativeScreenHeight = 200;

        readonly byte[] leftUnarmedAnims = { 0, 1, 2, 3, 4, 2, 1, 0 };
        int leftUnarmedAnimIndex = 0;

        DaggerfallUnity dfUnity;
        CifRciFile cifFile;
        WeaponAtlas weaponAtlas;
        readonly WeaponAtlas[] weaponAtlasCache = new WeaponAtlas[2];
        readonly CustomWeaponAnimation[] customWeaponAnimationCache = new CustomWeaponAnimation[2];
        Rect weaponPosition;
        float weaponScaleX;
        float weaponScaleY;

        DaggerfallAudioSource dfAudioSource;
        WeaponAnimation[] weaponAnims;
        WeaponStates weaponState = WeaponStates.Idle;
        int currentFrame = 0;
        int animTicks = 0;
        float animTickTime;
        Rect curAnimRect;
        float weaponOffsetHeight;
        Rect screenRect;

        Dictionary<int, Texture2D> customTextures = new Dictionary<int, Texture2D>();
        Texture2D curCustomTexture;

        // Allows a mod to specify if DFU should use custom HUD animations for weapons
        public static bool moddedWeaponHUDAnimsEnabled = false;

        float lastScreenWidth, lastScreenHeight;
        bool lastLargeHUDSetting, lastLargeHUDDockSetting;
        bool lastSheathed;
        float lastWeaponOffsetHeight;

        #region Properties

        public WeaponStates WeaponState { get { return weaponState; } }

        public Color Tint { get; set; } = Color.white;

        public Vector2 Position { get; set; } = Vector2.zero;   //Moves the sprite in screen distance
        public Vector2 Scale { get; set; } = Vector2.one;
        public Vector2 Offset { get; set; } = Vector2.zero;     //Moves the sprite relative to its own dimensions ("Offset.x = 0.5f" will move "sprite.width * 0.5f"). Applied after Scale.

        #endregion

        void Start()
        {
            dfUnity = DaggerfallUnity.Instance;
            dfAudioSource = GetComponent<DaggerfallAudioSource>();
            StartCoroutine(AnimateWeapon());
        }

        void OnGUI()
        {
            bool updateWeapon = false;
            GUI.depth = 1;

            if (DaggerfallUI.Instance.CustomScreenRect != null)
                screenRect = DaggerfallUI.Instance.CustomScreenRect.Value;
            else
                screenRect = new Rect(0, 0, Screen.width, Screen.height);

            // Must be ready and not loading the game
            if (!ReadyCheck() || WeaponType == WeaponTypes.None || GameManager.IsGamePaused || SaveLoadManager.Instance.LoadInProgress)
                return;

            // Must have current weapon texture atlas
            if (weaponAtlas == null || WeaponType != currentWeaponType || MetalType != currentMetalType)
            {
                LoadWeaponAtlas();
                if (weaponAtlas == null)
                    return;
                updateWeapon = true;
            }

            // Offset weapon by large HUD height when both large HUD and undocked weapon offset enabled
            // Weapon is forced to offset when using docked HUD else it would appear underneath HUD
            // This helps user avoid such misconfiguration or it might be interpreted as a bug
            weaponOffsetHeight = 0;
#if UNITY_PSP2
            // The Vita shows the large HUD only in Select's full mode (DaggerfallHUD): lift the weapon only while it shows, or the arm
            // floats above the bottom edge and only the blade is seen
            if (DaggerfallUI.Instance.DaggerfallHUD != null &&
                DaggerfallUI.Instance.DaggerfallHUD.LargeHUD.Enabled &&
                (DaggerfallUnity.Settings.LargeHUDUndockedOffsetWeapon || DaggerfallUnity.Settings.LargeHUDDocked))
#else
            if (DaggerfallUI.Instance.DaggerfallHUD != null &&
                DaggerfallUnity.Settings.LargeHUD &&
                (DaggerfallUnity.Settings.LargeHUDUndockedOffsetWeapon || DaggerfallUnity.Settings.LargeHUDDocked))
#endif
            {
                weaponOffsetHeight = (int)DaggerfallUI.Instance.DaggerfallHUD.LargeHUD.ScreenHeight;
            }

            // Update weapon when resolution or large HUD state changes
            if (screenRect.width != lastScreenWidth ||
                screenRect.height != lastScreenHeight ||
                DaggerfallUnity.Settings.LargeHUD != lastLargeHUDSetting ||
                DaggerfallUnity.Settings.LargeHUDDocked != lastLargeHUDDockSetting ||
                GameManager.Instance.WeaponManager.Sheathed != lastSheathed ||
                weaponOffsetHeight != lastWeaponOffsetHeight)
            {
                lastScreenWidth = screenRect.width;
                lastScreenHeight = screenRect.height;
                lastLargeHUDSetting = DaggerfallUnity.Settings.LargeHUD;
                lastLargeHUDDockSetting = DaggerfallUnity.Settings.LargeHUDDocked;
                lastSheathed = GameManager.Instance.WeaponManager.Sheathed;
                lastWeaponOffsetHeight = weaponOffsetHeight;
                updateWeapon = true;
            }

            // Update weapon state only as needed
            if (updateWeapon)
                UpdateWeapon();

            if (Event.current.type.Equals(EventType.Repaint) && ShowWeapon)
            {
                // Draw weapon texture behind other HUD elements
#if UNITY_PSP2
                if (!curCustomTexture && weaponAtlas.PaletteTexture)
                    VitaWeaponPack.Draw(GetWeaponRect(), weaponAtlas.AtlasTexture, weaponAtlas.PaletteTexture, curAnimRect, Tint);
                else
                    DaggerfallUI.DrawTextureWithTexCoords(GetWeaponRect(), curCustomTexture ? curCustomTexture : weaponAtlas.AtlasTexture, curAnimRect, true, Tint);
#else
                Texture2D tex = curCustomTexture ? curCustomTexture : weaponAtlas.AtlasTexture;
                DaggerfallUI.DrawTextureWithTexCoords(GetWeaponRect(), tex, curAnimRect, true, Tint);
#endif
            }
        }

        private void LateUpdate()
        {
            if (Position != Vector2.zero)
                Position = Vector2.zero;
            if (Scale != Vector2.one)
                Scale = Vector2.one;
            if (Offset != Vector2.zero)
                Offset = Vector2.zero;
        }

        //Combines the base WeaponPosition Rect with Position, Scale and Offset
        public Rect GetWeaponRect()
        {
            if (Position != Vector2.zero || Scale != Vector2.one || Offset != Vector2.zero)
            {
                Rect weaponPositionOffset = weaponPosition;

                //Adds the Position to the Rect's position
                weaponPositionOffset.x += Position.x;      
                weaponPositionOffset.y += Position.y;

                weaponPositionOffset.width *= Scale.x;
                weaponPositionOffset.height *= Scale.y;

                //Adds the Offset to the Rect's position
                weaponPositionOffset.x += weaponPositionOffset.width * Offset.x;
                weaponPositionOffset.y += weaponPositionOffset.height * Offset.y;

                return weaponPositionOffset;

            } else
                return weaponPosition;
        }

        public void OnAttackDirection(WeaponManager.MouseDirections direction)
        {
            // Get state based on attack direction
            WeaponStates state;

            switch (direction)
            {
                case WeaponManager.MouseDirections.Down:
                    state = WeaponStates.StrikeDown;
                    break;
                case WeaponManager.MouseDirections.DownLeft:
                    state = WeaponStates.StrikeDownLeft;
                    break;
                case WeaponManager.MouseDirections.Left:
                    state = WeaponStates.StrikeLeft;
                    break;
                case WeaponManager.MouseDirections.Right:
                    state = WeaponStates.StrikeRight;
                    break;
                case WeaponManager.MouseDirections.DownRight:
                    state = WeaponStates.StrikeDownRight;
                    break;
                case WeaponManager.MouseDirections.Up:
                case WeaponManager.MouseDirections.UpLeft:
                case WeaponManager.MouseDirections.UpRight:
                    state = WeaponStates.StrikeUp;
                    break;
                default:
                    return;
            }

            // Do not change if already playing attack animation, unless releasing an arrow (bow & state=up->down)
            if (!IsPlayingOneShot() || (WeaponType == WeaponTypes.Bow && weaponState == WeaponStates.StrikeUp && state == WeaponStates.StrikeDown))
                ChangeWeaponState(state);
        }

        public void ChangeWeaponState(WeaponStates state)
        {
            weaponState = state;

            // Only reset frame to 0 for bows if idle state
            if (WeaponType != WeaponTypes.Bow || state == WeaponStates.Idle)
                currentFrame = animTicks = 0;

            UpdateWeapon();
        }

        public bool IsAttacking()
        {
            return IsPlayingOneShot();
        }

        public int GetHitFrame()
        {
            if (WeaponType == WeaponTypes.Bow)
                return 5;
            else
                return 2;
        }

        public int GetCurrentFrame()
        {
            return currentFrame;
        }

        public float GetAnimTime()
        {
            return animTicks * animTickTime;
        }

        public void PlayActivateSound()
        {
            if (dfAudioSource)
            {
                dfAudioSource.AudioSource.pitch = 1f;// *AttackSpeedScale;
                dfAudioSource.PlayOneShot(DrawWeaponSound, 0);
            }
        }

        public void PlaySwingSound()
        {
            if (dfAudioSource)
            {
                dfAudioSource.AudioSource.pitch = 1f * AttackSpeedScale;
                dfAudioSource.PlayOneShot(SwingWeaponSound, 0, 1.1f);
            }
        }

        public void PlayAttackVoice(SoundClips customSound = SoundClips.None)
        {
            if (dfAudioSource)
            {
                if (customSound == SoundClips.None)
                {
                    PlayerEntity playerEntity = GameManager.Instance.PlayerEntity;
                    SoundClips sound = DaggerfallEntity.GetRaceGenderAttackSound(playerEntity.Race, playerEntity.Gender, true);
                    float pitch = dfAudioSource.AudioSource.pitch;
                    dfAudioSource.AudioSource.pitch = pitch + UnityEngine.Random.Range(0, 0.3f);
                    dfAudioSource.PlayOneShot(sound, 0, 1f);
                    dfAudioSource.AudioSource.pitch = pitch;
                }
                else
                {
                    dfAudioSource.PlayOneShot(customSound, 0, 1f);
                }
            }
        }

        public void TryCacheReadiedWeaponAtlas(MetalTypes metalType, WeaponTypes weaponType, bool isRightHand)
        {
            var fileName = WeaponBasics.GetWeaponFilename(weaponType);
            if (GetCachedWeaponAtlas(fileName, metalType) == null)
            {
#if UNITY_PSP2
                CustomWeaponAnimation animation;
                CacheWeaponAtlas(GetWeaponTextureAtlas(fileName, metalType, 2, 2, out animation, true), isRightHand);
#else
                CacheWeaponAtlas(GetWeaponTextureAtlas(fileName, metalType, 2, 2, out var animation, true), isRightHand);
#endif
                CacheCustomWeaponAnimation(animation, isRightHand);
            }
        }

        #region Private Methods

        private bool IsPlayingOneShot()
        {
            return (weaponState != WeaponStates.Idle);
        }

        private void UpdateWeapon()
        {
            // Do nothing if weapon not ready
            if (weaponAtlas == null || weaponAnims == null ||
                weaponAtlas.WeaponRects == null || weaponAtlas.WeaponIndices == null)
            {
                return;
            }

            // Reset state if weapon not visible
            if (!ShowWeapon || WeaponType == WeaponTypes.None)
            {
                weaponState = WeaponStates.Idle;
                currentFrame = 0;
            }

            // Handle bow with no arrows
            if (!GameManager.Instance.WeaponManager.Sheathed && WeaponType == WeaponTypes.Bow && GameManager.Instance.PlayerEntity.Items.GetItem(Items.ItemGroups.Weapons, (int)Items.Weapons.Arrow, allowQuestItem: false) == null)
            {
                GameManager.Instance.WeaponManager.SheathWeapons();
                DaggerfallUI.SetMidScreenText(TextManager.Instance.GetLocalizedText("youHaveNoArrows"));
            }

            // Store rect and anim
            int weaponAnimRecordIndex;
            if (WeaponType == WeaponTypes.Bow)
                weaponAnimRecordIndex = 0; // Bow has only 1 animation
            else
                weaponAnimRecordIndex = weaponAnims[(int)weaponState].Record;

            try
            {
                bool isImported = customTextures.TryGetValue(MaterialReader.MakeTextureKey(0, (byte)weaponAnimRecordIndex, (byte)currentFrame), out curCustomTexture);
                if (FlipHorizontal && (weaponState == WeaponStates.Idle || weaponState == WeaponStates.StrikeDown || weaponState == WeaponStates.StrikeUp))
                {
                    // Mirror weapon rect
                    if (isImported)
                    {
                        curAnimRect = new Rect(1, 0, -1, 1);
                    }
                    else
                    {
                        Rect rect = weaponAtlas.WeaponRects[weaponAtlas.WeaponIndices[weaponAnimRecordIndex].startIndex + currentFrame];
                        curAnimRect = new Rect(rect.xMax, rect.yMin, -rect.width, rect.height);
                    }
                }
                else
                {
                    curAnimRect = isImported ? new Rect(0, 0, 1, 1) : weaponAtlas.WeaponRects[weaponAtlas.WeaponIndices[weaponAnimRecordIndex].startIndex + currentFrame];
                }
                WeaponAnimation anim = weaponAnims[(int)weaponState];

                // Get weapon dimensions
                int width = weaponAtlas.WeaponIndices[weaponAnimRecordIndex].width;
                int height = weaponAtlas.WeaponIndices[weaponAnimRecordIndex].height;

                // Get weapon scale
                weaponScaleX = (float)screenRect.width / (float)nativeScreenWidth;
                weaponScaleY = (float)screenRect.height / (float)nativeScreenHeight;

                // Adjust scale to be slightly larger when not using point filtering
                // This reduces the effect of filter shrink at edge of display
                if (dfUnity.MaterialReader.MainFilterMode != FilterMode.Point)
                {
                    weaponScaleX *= 1.01f;
                    weaponScaleY *= 1.01f;
                }

                // Source weapon images are designed to overlay a fixed 320x200 display.
                // Some weapons need to align with both top, bottom, and right of display.
                // This means they might be a little stretched on widescreen displays.
                switch (anim.Alignment)
                {
                    case WeaponAlignment.Left:
                        AlignLeft(anim, width, height);
                        break;

                    case WeaponAlignment.Center:
                        AlignCenter(anim, width, height);
                        break;

                    case WeaponAlignment.Right:
                        AlignRight(anim, width, height);
                        break;
                }
                // Set the frame time (attack speed)
                animTickTime = GetAnimTickTime();
            }
            catch (IndexOutOfRangeException)
            {
                DaggerfallUnity.LogMessage("Index out of range exception for weapon animation. Probably due to weapon breaking + being unequipped during animation.");
            }
        }

        private void AlignLeft(WeaponAnimation anim, int width, int height)
        {
            weaponPosition = new Rect(
                screenRect.x + screenRect.width * anim.Offset,
                screenRect.y + screenRect.height - height * weaponScaleY - weaponOffsetHeight,
                width * weaponScaleX,
                height * weaponScaleY);
        }

        private void AlignCenter(WeaponAnimation anim, int width, int height)
        {
            weaponPosition = new Rect(
                screenRect.x + screenRect.width / 2f - (width * weaponScaleX) / 2f,
                screenRect.y + screenRect.height - height * weaponScaleY - weaponOffsetHeight,
                width * weaponScaleX,
                height * weaponScaleY);
        }

        private void AlignRight(WeaponAnimation anim, int width, int height)
        {
            if (FlipHorizontal && (weaponState == WeaponStates.Idle || weaponState == WeaponStates.StrikeDown || weaponState == WeaponStates.StrikeUp))
            {
                // Flip alignment
                AlignLeft(anim, width, height);
                return;
            }

            weaponPosition = new Rect(
                screenRect.x + screenRect.width * (1f - anim.Offset) - width * weaponScaleX,
                screenRect.y + screenRect.height - height * weaponScaleY - weaponOffsetHeight,
                width * weaponScaleX,
                height * weaponScaleY);
        }

        private bool ReadyCheck()
        {
            // Do nothing if DaggerfallUnity not ready
            if (!dfUnity.IsReady)
            {
                DaggerfallUnity.LogMessage("FPSWeapon: DaggerfallUnity component is not ready. Have you set your Arena2 path?");
                return false;
            }

            // Ensure cif reader is ready
            if (cifFile == null)
            {
                cifFile = new CifRciFile();
                cifFile.Palette.Load(Path.Combine(dfUnity.Arena2Path, cifFile.PaletteName));
            }

            return true;
        }

        IEnumerator AnimateWeapon()
        {
#if UNITY_PSP2
            vitaNextTick = Time.time;
#endif
            while (true)
            {
                if (weaponAnims != null && ShowWeapon)
                {
                    int frameBeforeStepping = currentFrame;

                    // Special animation for unarmed attack to left
                    if ((WeaponType == WeaponTypes.Melee || WeaponType == WeaponTypes.Werecreature)
                        && WeaponState == WeaponStates.StrikeLeft)
                    {
                        // Step frame
                        currentFrame = leftUnarmedAnims[leftUnarmedAnimIndex];
                        leftUnarmedAnimIndex++;
                        if (leftUnarmedAnimIndex >= leftUnarmedAnims.Length)
                        {
                            ChangeWeaponState(WeaponStates.Idle);
                            leftUnarmedAnimIndex = 0;
                        }
                    }
                    else if (WeaponType == WeaponTypes.Bow && weaponState == WeaponStates.StrikeUp)
                    {
                        // Step each frame for drawing the bow until reach frame for ready to release arrow
                        if (currentFrame < weaponAnims[(int)weaponState].NumFrames - 1)
                            currentFrame++;
                        // Record animation ticks for drawing and then holding a bow
                        animTicks++;
                    }
                    else
                    {
                        // Step frame
                        currentFrame++;
                        if (currentFrame >= weaponAnims[(int)weaponState].NumFrames)
                        {
                            if (IsPlayingOneShot())
                            {
                                ChangeWeaponState(WeaponStates.Idle);   // If this is a one-shot anim go to queued weapon state
                                if (WeaponType == WeaponTypes.Bow)
                                    ShowWeapon = false;                 // Immediately hide bow so its idle frame doesn't show before it is hidden for its cooldown
                            }
                            else if (WeaponType == WeaponTypes.Bow && !DaggerfallUnity.Settings.BowDrawback)
                                currentFrame = 3;
                            else
                                currentFrame = 0;                       // Otherwise keep looping frames
                        }
                    }

                    // Only update if the frame actually changed & weapon drawn
                    if (frameBeforeStepping != currentFrame)
                        UpdateWeapon();
                }

#if UNITY_PSP2
                // WaitForSeconds resumes on the first frame after the time is up, so every step was late by up to a frame: at Vita frame
                // rates swings ran ~10-75% slower than on PC (worst at high Speed, where a step is 46 ms). Time each step from the previous
                // step's due time instead, still at most one step per frame (WeaponManager polls for the hit frame each frame); a hitch
                // or pause longer than a step drops the debt rather than rushing the frames after it.
                if (VitaUpstreamTick)
                {
                    yield return new WaitForSeconds(animTickTime);
                    vitaNextTick = Time.time;
                    continue;
                }
                vitaNextTick += animTickTime;
                if (Time.time - vitaNextTick > animTickTime)
                    vitaNextTick = Time.time;
                do
                    yield return null;
                while (Time.time < vitaNextTick);
#else
                yield return new WaitForSeconds(animTickTime);
#endif
            }
        }

#if UNITY_PSP2
        float vitaNextTick;
        public static bool VitaUpstreamTick;   // test switch: upstream's WaitForSeconds steps
        public float VitaAnimTickTime { get { return animTickTime; } }
        public int VitaFrameCount(WeaponStates state) { return weaponAnims != null ? weaponAnims[(int)state].NumFrames : 0; }
#endif

        private float GetAnimTickTime()
        {
            PlayerEntity player = GameManager.Instance.PlayerEntity;
            if (WeaponType == WeaponTypes.Bow || player == null)
                return GameManager.classicUpdateInterval;
            else
                return FormulaHelper.GetMeleeWeaponAnimTime(player, WeaponType, WeaponHands);
        }

        private void LoadWeaponAtlas()
        {
            // Get weapon filename
            string filename = WeaponBasics.GetWeaponFilename(WeaponType);

            // Load the weapon texture atlas
            // Texture is dilated into a transparent coloured border to remove dark edges when filtered
            // Important to use returned UV rects when drawing to get right dimensions
#if UNITY_PSP2
            CustomWeaponAnimation customAnimation;
            WeaponAtlas oldAtlas = weaponAtlas;
            weaponAtlas = GetWeaponTextureAtlas(filename, MetalType, 2, 2, out customAnimation, true);
            VitaReleaseAtlas(oldAtlas);
#else
            weaponAtlas = GetWeaponTextureAtlas(filename, MetalType, 2, 2, out var customAnimation, true);
#endif
            if (customAnimation != null)
                customTextures = customAnimation.Textures;
            else
                customTextures = new Dictionary<int, Texture2D>();
            weaponAtlas.AtlasTexture.filterMode = dfUnity.MaterialReader.MainFilterMode;

            // Get weapon anims
            weaponAnims = (WeaponAnimation[])WeaponBasics.GetWeaponAnims(WeaponType).Clone();

            // Store current weapon
            currentWeaponType = WeaponType;
            currentMetalType = MetalType;
            animTickTime = GetAnimTickTime();
        }

        #endregion

        #region Texture Loading

        private WeaponAtlas GetWeaponTextureAtlas(
            string filename,
            MetalTypes metalType,
            int padding,
            int border,
            out CustomWeaponAnimation customWeaponAnimation,
            bool dilate = false)
        {
            // Check caches
            var cachedAtlas = GetCachedWeaponAtlas(filename, metalType);
            var cachedAnimation = GetCachedCustomWeaponAnimation(filename, metalType);
            customWeaponAnimation = null;
            var modTextures = new Dictionary<int, Texture2D>();
            if (cachedAnimation != null)
                modTextures = cachedAnimation.Textures;
            // Nothing to load if both vanilla atlas and custom texture are currently cached.
            if (cachedAtlas != null && cachedAnimation != null)
            {
                customWeaponAnimation = new CustomWeaponAnimation() { FileName = filename, MetalType = metalType, Textures = modTextures };
                return cachedAtlas;
            }

            // Load texture file
            cifFile.Load(Path.Combine(dfUnity.Arena2Path, filename), FileUsage.UseMemory, true);
#if UNITY_PSP2
            // Baked index atlas + metal palette (VitaWeaponPack) instead of decoding, tinting and packing ARGB32 frames
            Texture2D packedTexture = null;
            Rect[] packedRects = null;
            if (cachedAtlas == null)
            {
                int frameCount = 0;
                for (int record = 0; record < cifFile.RecordCount; record++)
                    frameCount += cifFile.GetFrameCount(record);
                packedTexture = VitaWeaponPack.Load(filename, frameCount, out packedRects);
            }
#endif

            // Read every image in archive
            Rect rect;
            List<Texture2D> textures = new List<Texture2D>();
            List<RecordIndex> indices = new List<RecordIndex>();
#if UNITY_PSP2
            int startIndex = 0;   // textures stays empty with the baked pack, so count frames instead of textures.Count (otherwise every record read record 0's frames)
#endif
            for (int record = 0; record < cifFile.RecordCount; record++)
            {
                int frames = cifFile.GetFrameCount(record);
                DFSize size = cifFile.GetSize(record);
                RecordIndex ri = new RecordIndex()
                {
#if UNITY_PSP2
                    startIndex = startIndex,
#else
                    startIndex = textures.Count,
#endif
                    frameCount = frames,
                    width = size.Width,
                    height = size.Height,
                };
                indices.Add(ri);
#if UNITY_PSP2
                startIndex += frames;
                if (cachedAtlas == null && packedTexture == null) // Load atlas if not already cached.
#else
                if (cachedAtlas == null) // Load atlas if not already cached.
#endif
                {
                    for (int frame = 0; frame < frames; frame++)
                        textures.Add(GetWeaponTexture2D(filename, record, frame, metalType, out rect, border, dilate));
                }

                if (cachedAnimation != null) // No need to load frames if cached.
                    continue;
                for (int frame = 0; frame < frames; frame++)
                {
                    string moddedFileName = filename;
                    if (moddedWeaponHUDAnimsEnabled && SpecificWeapon != null)
                    {
                        moddedFileName = WeaponBasics.GetModdedWeaponFilename(SpecificWeapon);

                        if (string.IsNullOrEmpty(moddedFileName))
                            moddedFileName = WeaponBasics.GetWeaponFilename(WeaponType); // Possibly make support for custom weapon types for the HUD in the future.
                    }

                    Texture2D tex;
                    if (TextureReplacement.TryImportCifRci(moddedFileName, record, frame, metalType, true, out tex))
                    {
                        tex.filterMode = dfUnity.MaterialReader.MainFilterMode;
                        tex.wrapMode = TextureWrapMode.Mirror;
                        modTextures.Add(MaterialReader.MakeTextureKey(0, (byte)record, (byte)frame), tex);
                    }
                }
            }

            if (cachedAnimation == null && modTextures.Count > 0)
                customWeaponAnimation = new CustomWeaponAnimation() { FileName = filename, MetalType = metalType, Textures = modTextures };
            if (cachedAtlas != null)
                return cachedAtlas;
#if UNITY_PSP2
            if (packedTexture != null)
            {
                return new WeaponAtlas()
                {
                    FileName = filename,
                    MetalType = metalType,
                    AtlasTexture = packedTexture,
                    PaletteTexture = VitaWeaponPack.Palette(cifFile.Palette, metalType),
                    WeaponRects = packedRects,
                    WeaponIndices = indices.ToArray()
                };
            }
#endif

            // Pack textures into atlas
#if UNITY_PSP2
            // PackTextures resizes the atlas to what the frames need; a 2048x2048 ARGB32 start is a 16 MB block that failed to allocate on
            // the Vita when a weapon was readied after the inventory
            Texture2D atlasTexture = new Texture2D(4, 4, TextureFormat.ARGB32, false);
#else
            Texture2D atlasTexture = new Texture2D(2048, 2048, TextureFormat.ARGB32, false);
#endif
            Rect[] rectsOut = atlasTexture.PackTextures(textures.ToArray(), padding, 2048);
#if UNITY_PSP2
            // The frames were only atlas sources and nothing else holds them: free them (each was a leaked texture per weapon change)
            foreach (Texture2D texture in textures)
                Destroy(texture);
#endif
            RecordIndex[] indicesOut = indices.ToArray();

            // Shrink UV rect to compensate for internal border
            float ru = 1f / atlasTexture.width;
            float rv = 1f / atlasTexture.height;
            for (int i = 0; i < rectsOut.Length; i++)
            {
                Rect rct = rectsOut[i];
                rct.xMin += border * ru;
                rct.xMax -= border * ru;
                rct.yMin += border * rv;
                rct.yMax -= border * rv;
                rectsOut[i] = rct;
            }

            return new WeaponAtlas()
            {
                FileName = filename,
                MetalType = metalType,
                AtlasTexture = atlasTexture,
                WeaponRects = rectsOut,
                WeaponIndices = indicesOut
            };
        }

        private Texture2D GetWeaponTexture2D(
            string filename,
            int record,
            int frame,
            MetalTypes metalType,
            out Rect rectOut,
            int border = 0,
            bool dilate = false)
        {
            // Get source bitmap
            DFBitmap dfBitmap = cifFile.GetDFBitmap(record, frame);

            // Tint based on metal type
            // But not for steel as that is default colour in files
            if (metalType != MetalTypes.Steel && metalType != MetalTypes.None)
                dfBitmap = ImageProcessing.ChangeDye(dfBitmap, ImageProcessing.GetMetalDyeColor(metalType), DyeTargets.WeaponsAndArmor);

            // Get Color32 array
            DFSize sz;
            Color32[] colors = cifFile.GetColor32(dfBitmap, 0, border, out sz);

            // Dilate edges
            if (border > 0 && dilate)
                ImageProcessing.DilateColors(ref colors, sz);

            // Create Texture2D
            Texture2D texture = new Texture2D(sz.Width, sz.Height, TextureFormat.ARGB32, false);
            texture.SetPixels32(colors);
            texture.Apply(true);

            // Shrink UV rect to compensate for internal border
            float ru = 1f / sz.Width;
            float rv = 1f / sz.Height;
            rectOut = new Rect(border * ru, border * rv, (sz.Width - border * 2) * ru, (sz.Height - border * 2) * rv);

            return texture;
        }

        private WeaponAtlas GetCachedWeaponAtlas(string fileName, MetalTypes metalType)
        {
            foreach (var atlas in weaponAtlasCache)
            {
                if (atlas != null && atlas.FileName == fileName && atlas.MetalType == metalType)
                    return atlas;
            }

            return null;
        }

        private void CacheWeaponAtlas(WeaponAtlas weaponAtlas, bool isRightHand)
        {
#if UNITY_PSP2
            WeaponAtlas old = weaponAtlasCache[isRightHand ? 0 : 1];
            weaponAtlasCache[isRightHand ? 0 : 1] = weaponAtlas;
            VitaReleaseAtlas(old);
#else
            weaponAtlasCache[isRightHand ? 0 : 1] = weaponAtlas;
#endif
        }

#if UNITY_PSP2
        // An atlas texture is owned by this weapon while the current atlas or a cache slot holds it; replaced ones were never destroyed
        void VitaReleaseAtlas(WeaponAtlas atlas)
        {
            if (atlas == null || atlas == weaponAtlas || atlas == weaponAtlasCache[0] || atlas == weaponAtlasCache[1] || !atlas.AtlasTexture)
                return;
            Destroy(atlas.AtlasTexture);
        }

        /// <summary>Rebuilds the current atlas and drops the cached ones.</summary>
        public void VitaFlushAtlases()
        {
            WeaponAtlas right = weaponAtlasCache[0], left = weaponAtlasCache[1];
            weaponAtlasCache[0] = weaponAtlasCache[1] = null;
            VitaReleaseAtlas(right);
            VitaReleaseAtlas(left);
            if (weaponAtlas != null)
            {
                LoadWeaponAtlas();
                UpdateWeapon();   // frame rects belong to the atlas layout
            }
        }

        public int VitaShowFrame(WeaponStates state, int frame)
        {
            if (weaponAnims == null)
                return 0;
            weaponState = state;
            currentFrame = frame;
            UpdateWeapon();
            return weaponAnims[(int)state].NumFrames;
        }

        public string VitaAtlasInfo()
        {
            if (weaponAtlas == null || !weaponAtlas.AtlasTexture)
                return "no atlas";
            Texture2D t = weaponAtlas.AtlasTexture;
            return string.Format("{0} {1} {2}x{3} {4} palette={5} rect={6} custom={7} tint={8} weaponRect={9}", weaponAtlas.FileName, t.format, t.width, t.height,
                t.filterMode, weaponAtlas.PaletteTexture != null, curAnimRect, curCustomTexture != null, Tint, GetWeaponRect());
        }
#endif

        private CustomWeaponAnimation GetCachedCustomWeaponAnimation(string fileName, MetalTypes metalType)
        {
            foreach (var animation in customWeaponAnimationCache)
            {
                if (animation != null && animation.FileName == fileName && animation.MetalType == metalType)
                    return animation;
            }

            return null;
        }

        private void CacheCustomWeaponAnimation(CustomWeaponAnimation animation, bool isRightHand)
        {
            customWeaponAnimationCache[isRightHand ? 0 : 1] = animation;
        }

        #endregion
    }
}