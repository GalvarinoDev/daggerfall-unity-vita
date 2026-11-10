// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2023 Daggerfall Workshop
// Web Site:        http://www.dfworkshop.net
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Source Code:     https://github.com/Interkarma/daggerfall-unity
// Original Author: jefetienne
// Contributors:    
// 
// Notes:
//

using DaggerfallConnect.Utility;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Game.Utility.ModSupport;
using DaggerfallWorkshop.Game.Utility.ModSupport.ModSettings;
using DaggerfallWorkshop.Utility;
using DaggerfallWorkshop.Utility.AssetInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DaggerfallWorkshop.Game.UserInterface
{
    public class PauseOptionsDropdown : Panel
    {
        #region Fields

        // Red up/down arrows
        private const string redArrowsTextureName = "INVE07I0.IMG";
        private const int unitsDisplayed = 10;

        private readonly Rect upArrowRect = new Rect(0, 0, 9, 16);
        private readonly Rect downArrowRect = new Rect(0, 136, 9, 16);
        private readonly DFSize arrowsFullSize = new DFSize(9, 152);
        private Texture2D closedMenuIcon;
        private Texture2D openMenuIcon;

        private List<ListBox.ListItem> listItems;
        private List<Action> clickHandlers;
        private int maxTextWidth = 0;
#if UNITY_PSP2
        float lastUse = -1;
        static readonly float[] dungeonLightSteps = { 0.3f, 0.5f, 0.7f, 1f };
#endif

        private Panel dropdownPanel;
        private ListBox dropdownList;
        private Button dropDownToggleButton;
        private IUserInterfaceManager uiManager;
        private VerticalScrollBar dropdownScroller;

        #endregion

        #region Constructor and Setup

        public PauseOptionsDropdown(IUserInterfaceManager _uiManager)
            : base()
        {
            uiManager = _uiManager;
            clickHandlers = new List<Action>();
            Setup();
        }

        void Setup()
        {
            closedMenuIcon = Resources.Load<Texture2D>("hamburger_button");
            openMenuIcon = Resources.Load<Texture2D>("hamburger_button");

            // Drop down button
            dropDownToggleButton = DaggerfallUI.AddButton(new Rect(0, 0, 7, 7), this);
            dropDownToggleButton.BackgroundColor = new Color(0.2f, 0.2f, 0.2f, 0.5f);//0.5f);
            dropDownToggleButton.OnMouseClick += DropdownButton_OnMouseClick;
            dropDownToggleButton.Hotkey = DaggerfallShortcut.GetBinding(DaggerfallShortcut.Buttons.OptionsDropdown);
            dropDownToggleButton.BackgroundTexture = closedMenuIcon;

            // Dropdown options panel
            dropdownPanel = new Panel();
            dropdownPanel.Position = new Vector2(0, dropDownToggleButton.Position.y + dropDownToggleButton.Size.y);
            dropdownPanel.Size = new Vector2(50, 30);
            dropdownPanel.BackgroundColor = Color.black;
            dropdownPanel.Enabled = false;
            dropdownPanel.Outline.Enabled = true;
            this.Components.Add(dropdownPanel);

            dropdownList = new ListBox();
            dropdownList.Position = new Vector2(2, 2);
            dropdownList.ShadowPosition = Vector2.zero;
            dropdownList.RowsDisplayed = unitsDisplayed;
            SetBackground(dropdownList, Color.black, "pauseDropdownListBackgroundColor");
#if UNITY_PSP2
            // D-pad up/down selects rows (OnSelectItem) without running them; Cross (double click) runs the row
            dropdownList.OnSelectItem += () => { if (!VitaMenuNav.Moving) DropdownList_OnUseSelectedItem(); };
            dropdownList.OnUseSelectedItem += DropdownList_OnUseSelectedItem;
#else
            dropdownList.OnSelectItem += DropdownList_OnUseSelectedItem;
#endif
            dropdownList.SelectedTextColor = dropdownList.TextColor;
            dropdownList.OnScroll += DropdownList_OnScroll;
            dropdownPanel.Components.Add(dropdownList);

            AddOptions();
            var height = dropdownList.HeightContent();
            dropdownList.Size = new Vector2(maxTextWidth, height > 80 ? 80 : height);
            dropdownPanel.Size = new Vector2(dropdownList.Size.x + 6, dropdownList.Size.y + 3);

            dropdownScroller = new VerticalScrollBar();
            dropdownScroller.Position = new Vector2(maxTextWidth, 2);
            dropdownScroller.Size = new Vector2(5, dropdownPanel.Size.y - 3);
            dropdownScroller.DisplayUnits = unitsDisplayed;
            dropdownScroller.TotalUnits = dropdownList.Count;
            dropdownScroller.OnScroll += SavesScroller_OnScroll;
            dropdownPanel.Components.Add(dropdownScroller);
        }

        #endregion

        #region Overrides

        public override void Update()
        {
            if (Enabled)
                base.Update();

            // Adjust scaling, size and positioning when set in the ParentPanel
            float sx = Scale.x;
            float sy = Scale.y;

#if UNITY_PSP2
            dropDownToggleButton.Size = Vector2.Scale(new Vector2(7, 7), Scale);
#else
            dropDownToggleButton.Size = new Vector2(7, 7) * Scale;
#endif

            dropdownList.Scale = Scale;
            var height = dropdownList.HeightContent();
            dropdownList.Size = new Vector2(maxTextWidth * sx, height > 70 * sy ? 70 * sy : height);
#if UNITY_PSP2
            dropdownList.Position = Vector2.Scale(new Vector2(2, 2), Scale);
#else
            dropdownList.Position = new Vector2(2, 2) * Scale;
#endif

            dropdownPanel.Size = new Vector2(dropdownList.Size.x + 6 * sx, dropdownList.Size.y + 3 * sy);
            dropdownPanel.Position = new Vector2(0, dropDownToggleButton.Position.y + dropDownToggleButton.Size.y);

            dropdownScroller.Size = new Vector2(5 * sx, dropdownPanel.Size.y - 3 * sy);
#if UNITY_PSP2
            dropdownScroller.Position = Vector2.Scale(new Vector2(maxTextWidth, 2), Scale);
#else
            dropdownScroller.Position = new Vector2(maxTextWidth, 2) * Scale;
#endif

            for (int i = 0; i < listItems.Count; i++)
                listItems[i].textLabel.TextScale = sx;
        }

        #endregion

        #region Public Methods

        public void SetDropdownExpand(bool expand)
        {
            dropdownPanel.Enabled = expand;

            if (dropdownPanel.Enabled)
                dropDownToggleButton.BackgroundTexture = openMenuIcon;
            else
                dropDownToggleButton.BackgroundTexture = closedMenuIcon;

            HideOverlappingHUDElements(expand);
        }

        #endregion

        #region Private Methods

        void HideOverlappingHUDElements(bool hide)
        {
            if (DaggerfallUI.Instance.DaggerfallHUD == null)
                return;

            // Spell icons can overlap pause options dropdown - disable this HUD element while dropdown is open
            DaggerfallUI.Instance.DaggerfallHUD.ActiveSpells.Enabled = !hide;
        }

        private bool HasApplicableMods()
        {
            if (ModManager.Instance == null)
                return false;

            foreach (var m in ModManager.Instance.Mods)
            {
                if (m.HasSettings && m.LoadSettingsCallback != null)
                    return true;
            }

            return false;
        }

        private void SetBackground(BaseScreenComponent panel, Color color, string textureName)
        {
#if UNITY_PSP2
            Texture2D tex;
            if (TextureReplacement.TryImportTexture(textureName, true, out tex))
#else
            if (TextureReplacement.TryImportTexture(textureName, true, out Texture2D tex))
#endif
            {
                panel.BackgroundTexture = tex;
                TextureReplacement.LogLegacyUICustomizationMessage(textureName);
            }
            else
                panel.BackgroundColor = color;
        }

        private void AddOptions()
        {
#if !UNITY_PSP2     // no-mods alpha: no Mod Settings entry
            // Add mod settings option and set that as the current max text width
#if UNITY_PSP2
            ListBox.ListItem modSettings;
            dropdownList.AddItem(TextManager.Instance.GetLocalizedText("modSettings"), out modSettings);
#else
            dropdownList.AddItem(TextManager.Instance.GetLocalizedText("modSettings"), out ListBox.ListItem modSettings);
#endif
            UpdateMaxWidth(modSettings.textLabel.TextWidth);
            modSettings.Enabled = HasApplicableMods();
            clickHandlers.Add(ModSettingsWindowOption_OnClick);
#endif

            // Add game effects  option
#if UNITY_PSP2
            ListBox.ListItem gameEffectsSettings;
            dropdownList.AddItem(TextManager.Instance.GetLocalizedText("gameEffectsSettings"), out gameEffectsSettings);
#else
            dropdownList.AddItem(TextManager.Instance.GetLocalizedText("gameEffectsSettings"), out ListBox.ListItem gameEffectsSettings);
#endif
            UpdateMaxWidth(gameEffectsSettings.textLabel.TextWidth);
            gameEffectsSettings.Enabled = true;
            clickHandlers.Add(GameEffectsWindowOption_OnClick);

#if UNITY_PSP2
            // Vita: Rest has no pad button, so it lives here: close the pause window, then the usual rest window (its own checks)
            ListBox.ListItem rest;
            dropdownList.AddItem(TextManager.Instance.GetLocalizedText("vitaRest"), out rest);
            UpdateMaxWidth(rest.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                uiManager.PopWindow();
                DaggerfallUI.PostMessage(DaggerfallUIMessages.dfuiOpenRestWindow);
            });
#if VITA_TRAVELOPTIONS
            // Travel Options (built in): its follow-road key has no free pad button
            if (TravelOptions.TravelOptionsMod.Instance != null && TravelOptions.TravelOptionsMod.Instance.RoadsIntegration)
            {
                ListBox.ListItem followRoad;
                dropdownList.AddItem("Follow Road", out followRoad);
                UpdateMaxWidth(followRoad.textLabel.TextWidth);
                clickHandlers.Add(() =>
                {
                    uiManager.PopWindow();
                    TravelOptions.TravelOptionsMod.VitaFollowRequested = true;
                });
            }
#endif
            // Same for the character sheet and magic items (no pad buttons left)
            ListBox.ListItem characterSheet;
            dropdownList.AddItem(TextManager.Instance.GetLocalizedText("vitaCharacterSheet"), out characterSheet);
            UpdateMaxWidth(characterSheet.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                uiManager.PopWindow();
                DaggerfallUI.PostMessage(DaggerfallUIMessages.dfuiOpenCharacterSheetWindow);
            });
            ListBox.ListItem magicItem;
            dropdownList.AddItem(TextManager.Instance.GetLocalizedText("vitaUseMagicItem"), out magicItem);
            UpdateMaxWidth(magicItem.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                uiManager.PopWindow();
                DaggerfallUI.PostMessage(DaggerfallUIMessages.dfuiOpenUseMagicItemWindow);
            });

            // Vita: the startup launcher (built-in mod switches) stops showing once "always show" is unticked; this brings it back
            ListBox.ListItem launcher;
            dropdownList.AddItem(LauncherText(), out launcher);
            UpdateMaxWidth(launcher.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                DaggerfallUnity.Settings.ShowOptionsAtStart = !DaggerfallUnity.Settings.ShowOptionsAtStart;
                DaggerfallUnity.Settings.SaveSettings();
                launcher.textLabel.Text = LauncherText();
            });

#if VITA_MODS
            // Vita: Low Poly Trees mod models near the player (VitaNearTrees); off = DFU's billboard trees. On takes effect as areas load
            ListBox.ListItem lowPolyTrees;
            dropdownList.AddItem(LowPolyTreesText(), out lowPolyTrees);
            UpdateMaxWidth(lowPolyTrees.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                DaggerfallUnity.Settings.VitaLowPolyTrees = !DaggerfallUnity.Settings.VitaLowPolyTrees;
                DaggerfallUnity.Settings.SaveSettings();
                if (!DaggerfallUnity.Settings.VitaLowPolyTrees)
                    VitaNearTrees.ClearAll();
                lowPolyTrees.textLabel.Text = LowPolyTreesText();
            });
#endif

            // Vita: dungeon brightness (DungeonAmbientLightScale, otherwise only in the launcher's advanced settings); applies live
            ListBox.ListItem dungeonLight;
            dropdownList.AddItem(DungeonLightText(), out dungeonLight);
            UpdateMaxWidth(dungeonLight.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                float scale = DaggerfallUnity.Settings.DungeonAmbientLightScale;
                int next = 0;
                while (next < dungeonLightSteps.Length && dungeonLightSteps[next] <= scale + 0.01f)
                    next++;
                DaggerfallUnity.Settings.DungeonAmbientLightScale = dungeonLightSteps[next % dungeonLightSteps.Length];
                DaggerfallUnity.Settings.SaveSettings();
                dungeonLight.textLabel.Text = DungeonLightText();
            });

            // Vita: torch/lantern strength (VitaLights); applies live
            ListBox.ListItem fireLight;
            dropdownList.AddItem(FireLightText(), out fireLight);
            UpdateMaxWidth(fireLight.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                int fire = DaggerfallUnity.Settings.VitaFireLight;
                DaggerfallUnity.Settings.VitaFireLight = fire >= 200 ? 50 : fire >= 150 ? 200 : fire >= 100 ? 150 : fire >= 75 ? 100 : 75;   // 100 = PC look
                DaggerfallUnity.Settings.SaveSettings();
                fireLight.textLabel.Text = FireLightText();
            });

            // Vita: fire light colour (VitaLights warmth) and the player torch's reach; apply live
            ListBox.ListItem fireColour;
            dropdownList.AddItem(FireColourText(), out fireColour);
            UpdateMaxWidth(fireColour.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                DaggerfallUnity.Settings.VitaFireColour = (DaggerfallUnity.Settings.VitaFireColour + 1) % fireColourNames.Length;
                DaggerfallUnity.Settings.SaveSettings();
                fireColour.textLabel.Text = FireColourText();
            });
            ListBox.ListItem torchReach;
            dropdownList.AddItem(TorchReachText(), out torchReach);
            UpdateMaxWidth(torchReach.textLabel.TextWidth);
            clickHandlers.Add(() =>
            {
                int reach = DaggerfallUnity.Settings.VitaTorchReach;
                DaggerfallUnity.Settings.VitaTorchReach = reach >= 12 ? 6 : reach >= 9 ? 12 : 9;
                DaggerfallUnity.Settings.SaveSettings();
                torchReach.textLabel.Text = TorchReachText();
            });
#endif

            foreach (var opt in DaggerfallUI.Instance.GetPauseOptionsDropdownItems())
            {
#if UNITY_PSP2
                ListBox.ListItem item;
                dropdownList.AddItem(opt.Item1, out item);
#else
                dropdownList.AddItem(opt.Item1, out ListBox.ListItem item);
#endif
                clickHandlers.Add(opt.Item2);

                if (item.textLabel.TextWidth > maxTextWidth)
                    maxTextWidth = item.textLabel.TextWidth + 8;
            }

            listItems = dropdownList.ListItems;
            // Select nothing, so if the first option is disabled, the selectedIndex
            // will be -1 and won't interfere with the default disabled colors
            dropdownList.SelectNone();
        }

#if UNITY_PSP2
        static string LauncherText()
        {
            return TextManager.Instance.GetLocalizedText(DaggerfallUnity.Settings.ShowOptionsAtStart ? "vitaLauncherOn" : "vitaLauncherOff");
        }

#if VITA_MODS
        static string LowPolyTreesText()
        {
            return TextManager.Instance.GetLocalizedText(DaggerfallUnity.Settings.VitaLowPolyTrees ? "vitaLowPolyTreesOn" : "vitaLowPolyTreesOff");
        }
#endif

        static string DungeonLightText()
        {
            return string.Format(TextManager.Instance.GetLocalizedText("vitaDungeonLight"), DaggerfallUnity.Settings.DungeonAmbientLightScale * 100);
        }

        static string FireLightText()
        {
            return string.Format(TextManager.Instance.GetLocalizedText("vitaFireLight"), DaggerfallUnity.Settings.VitaFireLight);
        }

        static string[] fireColourNames { get { return TextManager.Instance.GetLocalizedTextList("vitaFireColours"); } }

        static string FireColourText()
        {
            string[] names = fireColourNames;
            return string.Format(TextManager.Instance.GetLocalizedText("vitaFireColour"), names[Mathf.Clamp(DaggerfallUnity.Settings.VitaFireColour, 0, names.Length - 1)]);
        }

        static string TorchReachText()
        {
            return string.Format(TextManager.Instance.GetLocalizedText("vitaTorchReach"), DaggerfallUnity.Settings.VitaTorchReach);
        }
#endif

        void UpdateMaxWidth(int itemWidth)
        {
            if (itemWidth > maxTextWidth)
                maxTextWidth = itemWidth + 8;
        }

        #endregion

        #region Private Events

        private void SavesScroller_OnScroll()
        {
            dropdownList.ScrollIndex = dropdownScroller.ScrollIndex;
        }

        private void DropdownList_OnScroll()
        {
            dropdownScroller.ScrollIndex = dropdownList.ScrollIndex;
        }

        private void DropdownList_OnUseSelectedItem()
        {
            int ind = dropdownList.SelectedIndex;

            if (ind < 0)
                return;
#if UNITY_PSP2
            // One Cross runs the row twice (its click selects the row = OnSelectItem, then VitaMenuNav's double click re-selects it),
            // so on/off rows flipped back ("Launcher at next boot" seemed stuck): one run per press
            if (Time.unscaledTime - lastUse < 0.3f)
            {
                dropdownList.SelectedIndex = -1;
                return;
            }
            lastUse = Time.unscaledTime;
#endif

            dropdownList.SelectedIndex = -1;

            if (listItems[ind].Enabled)
                clickHandlers[ind]();
        }

        private void DropdownButton_OnMouseClick(BaseScreenComponent sender, Vector2 position)
        {
            SetDropdownExpand(!dropdownPanel.Enabled);
        }

        private void ModSettingsWindowOption_OnClick()
        {
            var modTitles = ModManager.Instance.Mods
                .Where(x => x.HasSettings && x.LoadSettingsCallback != null)
                .Select(x => x.Title)
                .ToArray();

            var listPicker = new DaggerfallListPickerWindow(uiManager);
            listPicker.ListBox.AddItems(modTitles);
            listPicker.OnItemPicked += (index, modTitle) =>
            {
                listPicker.PopWindow();
                uiManager.PushWindow(new ModSettingsWindow(uiManager, ModManager.Instance.GetMod(modTitle), true));
            };

            uiManager.PushWindow(listPicker);
        }

        private void GameEffectsWindowOption_OnClick()
        {
            uiManager.PushWindow(DaggerfallUI.Instance.GameEffectsConfigWindow);
        }

        #endregion
    }
}
