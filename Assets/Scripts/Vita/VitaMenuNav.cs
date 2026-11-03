// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// D-pad menu navigation. In any window, a D-pad press moves the controller cursor to the nearest Button in that direction, so Cross
// (UI left click) presses it and hover highlights work as with a mouse. It walks the top window's component tree, so it covers every
// window (mod windows too) without per-menu tables. Idea from the Controller Assistant mod (gigantibyte); none of its code is used.
// Lists: up/down moves the selected row, Cross uses it. A window that closes on its own D-pad hotkey still does (travel map: right, automap: down, logbook: left).

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

public static class VitaMenuNav
{
    const float repeatDelay = 0.35f, repeatInterval = 0.10f;   // seconds while a direction is held
    const float cursorScale = 0.75f;   // drawn arrow size

    // D-pad up, right, down, left (Vita joystick buttons 8-11) and their directions in GUI space (y down)
    static readonly KeyCode[] keys = { KeyCode.JoystickButton8, KeyCode.JoystickButton9, KeyCode.JoystickButton10, KeyCode.JoystickButton11 };
    static readonly Vector2[] dirs = { Vector2.down, Vector2.right, Vector2.up, Vector2.left };

    static readonly List<Rect> buttons = new List<Rect>();
    static readonly List<ListBox> lists = new List<ListBox>();   // their rects are also in buttons, from listStart on
    static readonly List<HorizontalSlider> sliders = new List<HorizontalSlider>();   // their rects are in buttons too; left/right steps them
    static int listStart;
    static IUserInterfaceWindow measured;   // menu stutter probe: one VITAUI line per window
    static float measureStart, firstMs, worstMs, totalMs;
    static int measureFrames;
    static bool measureLogged;

    // True while the D-pad moves a list selection, so lists that run their row on select (pause dropdown) can tell it from a click
    public static bool Moving;

    // Set by SaveLoadManager while it reads the screen for the save picture
    public static bool HideCursor;
    static ListBox pendingUse;   // list whose selected row Cross double-clicks; next frame, after this press's click has run
    static IUserInterfaceWindow pendingTop;
    static ListBox repoint;      // list Cross just used; its selected row may have moved
    static int heldKey = -1;
    static float nextRepeat;
    static IUserInterfaceWindow lastTop;
    static int snapFrames;
    static Vector2 lastCenter, lastArrow;   // GUI space: the selected button's centre (the click point) and where its arrow is drawn

    /// <summary>Called by DaggerfallUI before the top window updates, so hover state follows in the same frame.</summary>
    public static void Tick(IUserInterfaceWindow top)
    {
        Measure(top);
        DaggerfallTravelMapWindow travelMap = top as DaggerfallTravelMapWindow;
        if (top is DaggerfallHUD || !InputManager.Instance.CursorVisible || (travelMap != null && travelMap.VitaRegionSelected) ||
            top is DaggerfallAutomapWindow || top is DaggerfallExteriorAutomapWindow)
        {
            heldKey = -1;
            lastTop = null;
            return;
        }

        // A touch hands the cursor to the mouse (InputManager drops controller mode), and the next Cross / D-pad press was ignored
        // (one swallowed press after every tap): the first pad press takes the cursor back where the touch left it and acts at once
        if (!InputManager.Instance.UsingController && InputManager.Instance.EnableController &&
            (Input.GetKeyDown(KeyCode.JoystickButton0) || Input.GetKeyDown(keys[0]) || Input.GetKeyDown(keys[1]) ||
             Input.GetKeyDown(keys[2]) || Input.GetKeyDown(keys[3])))
            InputManager.Instance.SetControllerCursor(Input.mousePosition);

        // A window just came up: after its layout settles (2 frames), put the cursor on its first main button so the selection always shows
        if (top != lastTop)
        {
            lastTop = top;
            snapFrames = 2;
        }
        if (snapFrames > 0 && --snapFrames == 0)
            SnapToFirstButton(top);

        // Cross on a list row double-clicks it: ListBox uses the item and windows that listen for the double click itself (save list) get it
        if (pendingUse != null)
        {
            if (top == pendingTop)
            {
                pendingUse.TriggerMouseDoubleClick();
                repoint = pendingUse;
            }
            pendingUse = null;
        }
        else if (repoint != null)
        {
            // The used row can refill the list (talk window topics): one frame later, put the arrow on the row now selected
            if (top == pendingTop && repoint.Enabled && repoint.SelectedIndex >= 0 && repoint.SelectedIndex < repoint.Count)
                PointAtRow(repoint);
            repoint = null;
        }
        if (Input.GetKeyDown(KeyCode.JoystickButton0) && InputManager.Instance.UsingController)
        {
            CollectTargets(top);
            pendingUse = ListAtCursor();
            pendingTop = top;
            // One Cross on the "Overwrite this save?" YES did nothing (second one worked): log where each Cross lands to catch it
            if (VitaMemory.Probes)
            {
                Vector3 m = InputManager.Instance.MousePosition;
                Vector2 gui = new Vector2(m.x, Screen.height - m.y);
                int over = 0;
                for (int i = 0; i < buttons.Count; i++)
                    if (buttons[i].Contains(gui))
                        over++;
                Debug.LogFormat("VITANAV cross {0} at {1:F0},{2:F0} over {3} of {4} targets, mouse0 {5}", top.GetType().Name, gui.x, gui.y,
                    over, buttons.Count, InputManager.Instance.GetMouseButtonDown(0));
            }
        }

        int dir = -1;
        for (int i = 0; i < keys.Length; i++)
        {
            if (Input.GetKeyDown(keys[i]))
            {
                dir = heldKey = i;
                nextRepeat = Time.unscaledTime + repeatDelay;
            }
        }
        if (dir < 0 && heldKey >= 0)
        {
            if (!Input.GetKey(keys[heldKey]))
                heldKey = -1;
            else if (Time.unscaledTime >= nextRepeat)
            {
                dir = heldKey;
                nextRepeat += repeatInterval;
            }
        }
        if (dir < 0)
            return;

        CollectTargets(top);

        // Up/down inside a list moves its selection; past either end it falls through to the buttons
        ListBox list = ListAtCursor();
        if (list != null && dirs[dir].x == 0)
        {
            int before = list.SelectedIndex;
            Moving = true;
            if (dir == 0)
                list.SelectPrevious();
            else
                list.SelectNext();
            Moving = false;
            if (list.SelectedIndex != before)
            {
                PointAtRow(list);
                return;
            }
        }

        // Left/right on a slider bar steps its value (weapon swing mode etc. could not be changed with the pad)
        HorizontalSlider slider = SliderAtCursor();
        if (slider != null && dirs[dir].y == 0)
        {
            slider.ScrollIndex += dir == 1 ? 1 : -1;
            return;
        }

        // Start from the button under the cursor (its centre), else from the cursor itself
        Vector3 mouse = InputManager.Instance.MousePosition;
        Vector2 from = new Vector2(mouse.x, Screen.height - mouse.y);
        for (int i = 0; i < buttons.Count; i++)
        {
            if (buttons[i].Contains(from))
            {
                from = buttons[i].center;
                break;
            }
        }

        // Nearest centre ahead of us; sideways distance counts double so a row/column wins over a diagonal.
        // Targets within 45 degrees of the press win over the rest (Right from the launcher's Mods went up to a checkbox, not to Play)
        Vector2 d = dirs[dir];
        float best = float.MaxValue;
        int target = -1;
        for (int i = 0; i < buttons.Count; i++)
        {
            Vector2 delta = buttons[i].center - from;
            float along = Vector2.Dot(delta, d);
            if (along < 1f)
                continue;
            float side = Mathf.Abs(d.x * delta.y - d.y * delta.x);
            float score = along + 2f * side + (side > along ? 100000f : 0f);
            if (score < best)
            {
                best = score;
                target = i;
            }
        }
        if (target >= listStart)
            PointAtRow(lists[target - listStart]);
        else if (target >= 0)
            PointAt(buttons[target]);
    }

    // First frame after a window comes up (its opening cost; HUD = closing the menu), worst frame and average over its first 5 s
    static void Measure(IUserInterfaceWindow top)
    {
        float ms = Time.unscaledDeltaTime * 1000f;
        if (top != measured)
        {
            LogMeasure();
            measured = top;
            measureStart = Time.unscaledTime;
            measureFrames = 0;
            worstMs = totalMs = 0;
            measureLogged = false;
            return;
        }
        if (measureFrames == 0)
            firstMs = ms;
        measureFrames++;
        totalMs += ms;
        worstMs = Mathf.Max(worstMs, ms);
        if (Time.unscaledTime - measureStart >= 5f)
            LogMeasure();
    }

    static void LogMeasure()
    {
        if (measured == null || measureLogged || measureFrames == 0)
            return;
        measureLogged = true;
        Debug.LogFormat("VITAUI {0}: first frame {1:F0} ms, worst {2:F0} ms, {3:F1} FPS over {4} frames", measured.GetType().Name, firstMs, worstMs,
            measureFrames * 1000f / totalMs, measureFrames);
    }

    static ListBox ListAtCursor()
    {
        Vector3 mouse = InputManager.Instance.MousePosition;
        Vector2 at = new Vector2(mouse.x, Screen.height - mouse.y);
        for (int i = 0; i < lists.Count; i++)
            if (lists[i].Rectangle.Contains(at))
                return lists[i];
        return null;
    }

    static HorizontalSlider SliderAtCursor()
    {
        Vector3 mouse = InputManager.Instance.MousePosition;
        Vector2 at = new Vector2(mouse.x, Screen.height - mouse.y);
        for (int i = 0; i < sliders.Count; i++)
            if (sliders[i].Rectangle.Contains(at))
                return sliders[i];
        return null;
    }

    // Points at the list's selected row (the first row if none), scrolled into view. Rows stack as ListBox.Draw lays them out:
    // EntryWise lists start at row ScrollIndex, PixelWise lists (talk window) are shifted up by ScrollIndex pixels.
    static void PointAtRow(ListBox list)
    {
        bool pixelWise = list.VerticalScrollMode == ListBox.VerticalScrollModes.PixelWise;
        if (list.SelectedIndex < 0)
            list.SelectIndex(pixelWise ? 0 : list.ScrollIndex);
        int selected = list.SelectedIndex;
        if (selected < 0)
            return;
        TextLabel label = list.SelectedValue.textLabel;
        if (!pixelWise && (selected < list.ScrollIndex || selected >= list.ScrollIndex + list.RowsDisplayed))
            list.ScrollToSelected();

        float top = 0;
        for (int i = pixelWise ? 0 : list.ScrollIndex; i < selected; i++)
            top += list.GetItem(i).textLabel.TextHeight + list.RowSpacing;
        if (pixelWise)
        {
            // Scroll so the whole row shows; set one past and step back so the list raises OnScroll (talk window's scroll bar follows)
            int scroll = list.ScrollIndex;
            if (top < scroll)
                scroll = (int)top;
            else if (top + label.TextHeight > scroll + list.Size.y)
                scroll = (int)(top + label.TextHeight - list.Size.y);
            if (scroll != list.ScrollIndex)
            {
                list.ScrollIndex = scroll + 1;
                list.ScrollUp();
            }
            top -= list.ScrollIndex;
        }

        Rect r = list.Rectangle;
        float sx = r.width / list.Size.x, sy = r.height / list.Size.y;
        PointAt(new Rect(r.x, r.y + top * sy, Mathf.Clamp(label.TextWidth * sx, 8f, r.width), label.TextHeight * sy));
    }

    /// <summary>Draws the controller cursor after the top window (InputManager's own OnGUI draw ended up under the UI on the Vita).</summary>
    public static void DrawCursor(IUserInterfaceWindow top)
    {
        InputManager im = InputManager.Instance;
        Texture2D image = im.controllerCursorImage;
        if (HideCursor || top is DaggerfallHUD || top is VitaLoadingWindow || !im.CursorVisible || !im.UsingController || image == null)
            return;
        Vector3 mouse = im.MousePosition;
        Vector2 at = new Vector2(mouse.x, Screen.height - mouse.y);
        if ((at - lastCenter).sqrMagnitude < 1f)
            at = lastArrow;
        GUI.DrawTexture(new Rect(at.x, at.y, image.width * cursorScale, image.height * cursorScale), image);
    }

    // The cursor (click point) goes to the button's centre; DrawCursor shows the arrow tip just right of the button, level with its
    // middle, so it points at the label without covering it
    static void PointAt(Rect r)
    {
        lastCenter = r.center;
        lastArrow = new Vector2(Mathf.Min(r.xMax + 2f, Screen.width - 8f), r.center.y);
        InputManager.Instance.SetControllerCursor(new Vector2(lastCenter.x, Screen.height - lastCenter.y));
    }

    // Top-most of the big buttons (at least half the largest's area), so small corner buttons don't win (pause window's dropdown)
    static void SnapToFirstButton(IUserInterfaceWindow top)
    {
        CollectTargets(top);
        if (lists.Count > 0)
        {
            PointAtRow(lists[0]);
            return;
        }
        DaggerfallUnitySetupGameWizard wizard = top as DaggerfallUnitySetupGameWizard;
        if (wizard != null && wizard.VitaNavStart != null && buttons.Contains(wizard.VitaNavStart.Rectangle))
        {
            PointAt(wizard.VitaNavStart.Rectangle);
            return;
        }
        float largest = 0;
        for (int i = 0; i < buttons.Count; i++)
            largest = Mathf.Max(largest, buttons[i].width * buttons[i].height);
        int pick = -1;
        for (int i = 0; i < buttons.Count; i++)
        {
            Rect r = buttons[i];
            if (r.width * r.height >= largest / 2 && (pick < 0 || r.y < buttons[pick].y || (r.y == buttons[pick].y && r.x < buttons[pick].x)))
                pick = i;
        }
        if (pick >= 0)
            PointAt(buttons[pick]);
    }

    // Buttons, plus click spots of windows that have no buttons for them (race map provinces)
    static void CollectTargets(IUserInterfaceWindow top)
    {
        buttons.Clear();
        lists.Clear();
        sliders.Clear();
        Collect(top.ParentPanel);
        CreateCharRaceSelect raceSelect = top as CreateCharRaceSelect;
        if (raceSelect != null)
            raceSelect.AddVitaNavTargets(buttons);
        listStart = buttons.Count;
        for (int i = 0; i < lists.Count; i++)
            buttons.Add(lists[i].Rectangle);
    }

    static void Collect(BaseScreenComponent c)
    {
        if (!c.Enabled)
            return;

        if (c is Button || c is Checkbox)
        {
            Rect r = c.Rectangle;
            if (r.width > 0 && r.height > 0 && r.width * r.height < Screen.width * Screen.height / 4f)
                buttons.Add(r);
        }
        HorizontalSlider slider = c as HorizontalSlider;
        if (slider != null && slider.TotalUnits > slider.DisplayUnits)
        {
            sliders.Add(slider);
            buttons.Add(c.Rectangle);
        }
        ListBox list = c as ListBox;
        if (list != null && list.Count > 0)
            lists.Add(list);

        Panel panel = c as Panel;
        if (panel != null)
        {
            foreach (BaseScreenComponent child in panel.Components)
                Collect(child);
        }
    }
}
#endif
