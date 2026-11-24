// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Quick menu (hold L): a ring of weapons and shields, spells or usable items. The right stick picks a slice, R changes the page
// (Weapons, Spells, Items), D-pad Up/Down show the other rings when there are more than 8, releasing L closes it. Game time while
// open: paused, real time or slow motion (pause menu option).
//   Weapons: Cross = right hand, Square = left hand (one-handed weapons; shields and two-handers go where Daggerfall puts them),
//            again on the same hand takes it off, "Swap hands" = the SwitchHand key. Equip delays as in the inventory.
//   Spells:  Cross = cast it (ready + cast, as L does), Square = make it the L spell (tap L casts it), "Spellbook" slice.
//   Items:   the Use Magic Item list (potions, cast-when-used items), Cross = use, as that window does; a spell an item readies goes off
//            at once, as spell hotkeys do. Square on a magic item = make its power the L action. Only the cast-when-used power is used:
//            enchanted clothing and armour are never put on or taken off here.
//   Any page: D-pad Left/Right tap = put the slice on hotkey 1/2, hold = 3/4 (the same again clears it). In play the D-pad Left/Right
//            tap/hold uses them (DaggerfallHUD): spells cast at once, weapons are drawn, potions drunk, magic items used.
// Looks like the classic UI: inventory stone, the large HUD's spell/weapon/magic item buttons as page tabs, the classic font.
// Action ring (hold D-pad Down, actionRing): Talk, Info, Grab, Rest, Map, Automap, Steal; the right stick picks, releasing Down chooses.
// (Use Magic Item is on the L ring's Items page instead.)

#if UNITY_PSP2
using System;
using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallConnect.FallExe;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Formulas;
using DaggerfallWorkshop.Game.Items;
using DaggerfallWorkshop.Game.MagicAndEffects;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;
using DaggerfallWorkshop.Utility;

public class VitaQuickMenuWindow : DaggerfallPopupWindow
{
    public const int TimePaused = 0, TimeReal = 1, TimeSlow = 2;
    const float slowScale = 0.25f;
    const int pageSize = 8;
    // The Chinese glyph pack is a point-filtered 1-bit atlas at a whole number of screen pixels per font pixel at scale 1;
    // smaller scales drop rows and columns (missing strokes), so Chinese labels stay at 1
    static float labelScale { get { return Font.IsSDFCapable ? 1f : 0.75f; } }
    const float minLabelScale = 0.55f;
    const float stickPick = 0.35f;      // stick diagonals don't reach the corners
    // Ring in native 320x200 space; its texture has ringScale pixels per native pixel with smoothed edges (the stone keeps its classic texels)
    const int R = 74, r = 26, ringSize = 2 * R + 2, ringScale = 2, ringPx = ringSize * ringScale;
    static readonly Vector2 centre = new Vector2(160, 104);
    static readonly Color dimColor = new Color(0, 0, 0, 0.55f);
    static readonly Color32 seamColor = new Color32(40, 40, 40, 255), lightEdge = new Color32(190, 190, 190, 255), darkEdge = new Color32(45, 45, 45, 255),
        discLight = new Color32(115, 112, 105, 255);

    enum Kind { Weapon, SwapHands, Spell, Spellbook, Item, Nothing, Mode, Rest, Map, Automap }
    class Entry
    {
        public Kind kind;
        public PlayerActivateModes mode;
        public DaggerfallUnityItem item;
        public EffectBundleSettings spell;
        public string name, info;
        public string above;    // drawn above the icon ("Travel" over "Map"); empty in the translations
    }

    // Shared textures, made once (small; the class owns them for the session). The ring (~1.2 MB with its arrays) is freed on close.
    static Texture2D stoneBar, stoneButton, ringTexture;
    static Texture2D[] tabIcons;
    static Texture2D[] actionIcons;     // Talk, Info, Grab, Steal (the game's own icons, not ours to free), Rest, Automap (MAIN00I0 buttons), Map (TRAV0I00 compass)
    static Color32[] stone;
    static int stoneW, stoneH;
    // Ring pixels for the current slice count: slice index per pixel (255 = clear, 254 = inner disc, 253 = seam/edge) and base colours
    static byte[] ringSlice;
    static Color32[] ringBase, ringPixels;
    static Color32[] ringStone, discStone;      // the stone at ring and disc brightness, native size (90 KB each)
    static int ringSlices = -1, paintedSelected = -2;

    int page, ringPage, selected = -1, drawnSelected = -2;
    readonly List<Entry> entries = new List<Entry>();
    DaggerfallUnityItem lastRightHandItem, lastLeftHandItem;
    float savedTimeScale = -1;

    Panel ringPanel, pageButton;
    Panel[] tabs = new Panel[3];
    Panel[] tabDim = new Panel[3];
    TextLabel title, hint, pageLabel, pageKey;
    Panel pageGlyph;
    readonly List<BaseScreenComponent> hintParts = new List<BaseScreenComponent>();
    float hintUntil;        // a refusal ("Your class prohibits...") shows in the hint bar until then
    const float bindHold = 0.5f;
    readonly float[] bindDownTime = { -1, -1 };     // D-pad Left/Right press times while binding a hotkey
    readonly List<BaseScreenComponent> sliceParts = new List<BaseScreenComponent>();

    readonly bool actionRing;

    public VitaQuickMenuWindow(IUserInterfaceManager uiManager, IUserInterfaceWindow previous = null, bool actionRing = false)
        : base(uiManager, previous)
    {
        this.actionRing = actionRing;
        PauseWhileOpen = DaggerfallUnity.Settings.VitaQuickTime == TimePaused;
    }

    static DaggerfallFont Font { get { return DaggerfallUI.DefaultFont; } }
    static string Text(string key) { return TextManager.Instance.GetLocalizedText(key); }
    static PlayerEntity Player { get { return GameManager.Instance.PlayerEntity; } }

    #region Setup and textures

    protected override void Setup()
    {
        MakeTextures();

        // Hands at open, for the equip delay on close (as the inventory does)
        lastRightHandItem = Player.ItemEquipTable.GetItem(EquipSlots.RightHand);
        lastLeftHandItem = Player.ItemEquipTable.GetItem(EquipSlots.LeftHand);

        if (actionRing)
        {
            MakeActionIcons();
            title = Label("", Vector2.zero, 1f, NativePanel);
            ringPanel = new Panel { Position = centre - new Vector2(ringSize / 2f, ringSize / 2f), Size = new Vector2(ringSize, ringSize),
                BackgroundTextureLayout = BackgroundLayout.StretchToFill };
            NativePanel.Components.Add(ringPanel);
            Box(new Vector2(4, 185), new Vector2(312, 13), stoneBar, true);
            hint = Label("", Vector2.zero, labelScale, NativePanel);
            List<KeyValuePair<Glyph, string>> ringHint = new List<KeyValuePair<Glyph, string>>();
            ringHint.Add(new KeyValuePair<Glyph, string>(Glyph.StickR, Text("vitaQuickPick")));
            ringHint.Add(new KeyValuePair<Glyph, string>(Glyph.DPadDown, Text("vitaQuickRelease")));
            hintParts.AddRange(GlyphHint(ringHint, NativePanel, true));
            ShowPage(0);
            return;
        }

        for (int k = 0; k < 3; k++)
        {
            tabs[k] = Box(new Vector2(160 - 36 + k * 36 - 15.5f, 3), new Vector2(31, 15), tabIcons[k], false);
            tabDim[k] = new Panel { AutoSize = AutoSizeModes.ResizeToFill, BackgroundColor = new Color(0, 0, 0, 0.45f) };
            tabs[k].Components.Add(tabDim[k]);
        }
        title = Label("", Vector2.zero, 1f, NativePanel);

        ringPanel = new Panel { Position = centre - new Vector2(ringSize / 2f, ringSize / 2f), Size = new Vector2(ringSize, ringSize),
            BackgroundTextureLayout = BackgroundLayout.StretchToFill };
        NativePanel.Components.Add(ringPanel);

        pageButton = Box(new Vector2(4, 3), new Vector2(54, 16), stoneButton, true);
        pageLabel = Label("", Vector2.zero, labelScale, pageButton);
        pageGlyph = GlyphPanel(Glyph.DPadVertical, Vector2.zero, pageButton);
        pageKey = Label(Text("vitaQuickNextPage"), Vector2.zero, labelScale, pageButton);
        pageKey.TextColor = Color.white;

        Box(new Vector2(4, 185), new Vector2(312, 13), stoneBar, true);
        hint = Label("", Vector2.zero, labelScale, NativePanel);
        ShowPage(0);
    }

    // Classic art cut from the game's own images: INVE00I0 (inventory stone), MAIN00I0 (large HUD buttons)
    static void MakeTextures()
    {
        if (stoneBar == null)
            MakeClassicTextures();
        MakeGlyphs();
        if (ringTexture != null)
            return;
        ringTexture = new Texture2D(ringPx, ringPx, TextureFormat.ARGB32, false);
        ringTexture.filterMode = FilterMode.Bilinear;
        ringTexture.wrapMode = TextureWrapMode.Clamp;
        ringSlice = new byte[ringPx * ringPx];
        ringBase = new Color32[ringPx * ringPx];
        ringPixels = new Color32[ringPx * ringPx];
        ringSlices = -1;
    }

    // Crossed swords: weapons; spark: cast spell; hand and spark: use magic item
    static readonly Rect[] tabRects = { new Rect(227, 0, 47, 23), new Rect(84, 0, 47, 23), new Rect(84, 23, 47, 23) };

    // From vitaui.pak when it has them (VitaUiPack), else cut and drawn here
    static void MakeClassicTextures()
    {
        stoneBar = StoneTexture(312, 13, 0.8f);
        stoneButton = StoneTexture(54, 16, 0.9f);
        tabIcons = new Texture2D[3];
        ImageData main = new ImageData();
        Color32[] mainColors = null;
        for (int k = 0; k < 3; k++)
        {
            tabIcons[k] = VitaUiPack.Texture("tab" + k, 47, 23, DaggerfallUI.Instance.GlobalFilterMode);
            if (tabIcons[k] != null)
                continue;
            if (mainColors == null)
            {
                main = ImageReader.GetImageData("MAIN00I0.IMG", 0, 0, false, false);
                mainColors = ImageReader.GetColors(main);
            }
            tabIcons[k] = ImageReader.GetSubTexture(mainColors, tabRects[k], main.width, main.height);
        }
    }

    // The inventory stone the drawn textures tile; only needed for what vitaui.pak doesn't hold
    static void LoadStone()
    {
        if (stone != null)
            return;
        ImageData inve = ImageReader.GetImageData("INVE00I0.IMG", 0, 0, false, false);
        Color32[] inveColors = ImageReader.GetColors(inve);
        stoneW = 106; stoneH = 160;
        stone = new Color32[stoneW * stoneH];
        for (int y = 0; y < stoneH; y++)     // top-left (52, 20) of the paper doll's stone; colour arrays run bottom-up
            Array.Copy(inveColors, (inve.height - 1 - (20 + y)) * inve.width + 52, stone, y * stoneW, stoneW);
    }

    // Mode icons as the HUD shows them; Rest and Automap are the large HUD's buttons without their stone (neutral greys flood-filled
    // away from the edges), Map the travel map's compass rose without its sea (blue flood-filled away)
    const int compassX = 4, compassY = 129, compassW = 41, compassH = 36;
    static void MakeActionIcons()
    {
        if (actionIcons != null)
            return;
        actionIcons = new Texture2D[7];
        string[] names = { "classic-talk", "classic-info", "classic-grab", "classic-steal" };
        for (int k = 0; k < 4; k++)
        {
            Vector2 size;
            actionIcons[k] = DaggerfallUI.GetTextureFromResources("Icons/" + names[k], out size);
        }
        actionIcons[4] = VitaUiPack.Texture("button4", 33, 19, FilterMode.Point);
        actionIcons[5] = VitaUiPack.Texture("button6", 33, 19, FilterMode.Point);
        actionIcons[6] = VitaUiPack.Texture("compass", compassW, compassH, FilterMode.Point);
        if (actionIcons[4] == null || actionIcons[5] == null)
        {
            ImageData main = ImageReader.GetImageData("MAIN00I0.IMG", 0, 0, false, false);
            Color32[] mainColors = ImageReader.GetColors(main);
            if (actionIcons[4] == null)
                actionIcons[4] = IconTexture(ButtonIconPixels(mainColors, main.width, main.height, 225 + 7, 23 + 2), 33, 19);
            if (actionIcons[5] == null)
                actionIcons[5] = IconTexture(ButtonIconPixels(mainColors, main.width, main.height, 178 + 7, 23 + 2), 33, 19);
        }
        if (actionIcons[6] == null)
            actionIcons[6] = IconTexture(CompassPixels(), compassW, compassH);
    }

    static Texture2D IconTexture(Color32[] pixels, int w, int h)
    {
        Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
        t.filterMode = FilterMode.Point;
        t.SetPixels32(pixels);
        t.Apply(false, true);
        return t;
    }

    static Color32[] ButtonIconPixels(Color32[] src, int srcW, int srcH, int x0, int y0)
    {
        return CutIcon(src, srcW, srcH, x0, y0, 33, 19, false);
    }

    static Color32[] CompassPixels()
    {
        ImageData trav = ImageReader.GetImageData("TRAV0I00.IMG", 0, 0, false, false);
        return CutIcon(ImageReader.GetColors(trav), trav.width, trav.height, compassX, compassY, compassW, compassH, true);
    }

    // Cuts w x h at (x0, y0) and clears the background connected to its edges: neutral greys (button stone) or blues (sea)
    static Color32[] CutIcon(Color32[] src, int srcW, int srcH, int x0, int y0, int w, int h, bool sea)
    {
        Color32[] c = new Color32[w * h];      // rows top-down here, flipped on upload
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                c[y * w + x] = src[(srcH - 1 - (y0 + y)) * srcW + x0 + x];
        bool[] seen = new bool[w * h];
        Stack<int> open = new Stack<int>();
        for (int x = 0; x < w; x++) { open.Push(x); open.Push((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { open.Push(y * w); open.Push(y * w + w - 1); }
        while (open.Count > 0)
        {
            int i = open.Pop();
            if (seen[i])
                continue;
            seen[i] = true;
            Color32 p = c[i];
            if (sea ? p.b <= p.r + 12 : Math.Max(p.r, Math.Max(p.g, p.b)) - Math.Min(p.r, Math.Min(p.g, p.b)) >= 6)
                continue;
            c[i] = new Color32(0, 0, 0, 0);
            int x = i % w, y = i / w;
            if (x > 0) open.Push(i - 1);
            if (x < w - 1) open.Push(i + 1);
            if (y > 0) open.Push(i - w);
            if (y < h - 1) open.Push(i + w);
        }
        Color32[] up = new Color32[w * h];
        for (int y = 0; y < h; y++)
            Array.Copy(c, y * w, up, (h - 1 - y) * w, w);
        return up;
    }

#if UNITY_EDITOR
    /// <summary>Every texture and ring the Vita menus draw, made by the code above, for vitaui.pak (Assets/Editor/Vita/VitaUiBaker.cs).
    /// Keys and layouts are the ones VitaUiPack reads.</summary>
    public static void BakeRecords(Action<string, byte[]> add)
    {
        VitaUiPack.Off = true;
        Color32 none = new Color32();
        // Quick menu bar and page button, then the HUD style box (VitaHudStyleWindow: box, cell, selected cell, bar = the quick menu's)
        add(StoneKey(312, 13, 0.8f, none, 0), VitaUiPack.Raw(StonePixels(312, 13, 0.8f, none, 0)));
        add(StoneKey(54, 16, 0.9f, none, 0), VitaUiPack.Raw(StonePixels(54, 16, 0.9f, none, 0)));
        add(StoneKey(240, 86, 0.85f, none, 0), VitaUiPack.Raw(StonePixels(240, 86, 0.85f, none, 0)));
        add(StoneKey(68, 60, 0.65f, none, 0), VitaUiPack.Raw(StonePixels(68, 60, 0.65f, none, 0)));
        Color32 tint = VitaHudStyleWindow.selectedTint;
        add(StoneKey(68, 60, 0.65f, tint, 0.5f), VitaUiPack.Raw(StonePixels(68, 60, 0.65f, tint, 0.5f)));

        ImageData main = ImageReader.GetImageData("MAIN00I0.IMG", 0, 0, false, false);
        Color32[] mainColors = ImageReader.GetColors(main);
        for (int k = 0; k < 3; k++)
        {
            Rect rect = tabRects[k];
            rect.y = main.height - rect.y - rect.height;     // as ImageReader.GetSubTexture cuts it
            add("tab" + k, VitaUiPack.Raw(ImageReader.GetSubColors(mainColors, rect, main.width, main.height)));
        }
        add("button4", VitaUiPack.Raw(ButtonIconPixels(mainColors, main.width, main.height, 225 + 7, 23 + 2)));
        add("button6", VitaUiPack.Raw(ButtonIconPixels(mainColors, main.width, main.height, 178 + 7, 23 + 2)));
        add("compass", VitaUiPack.Raw(CompassPixels()));

        ringSlice = new byte[ringPx * ringPx];
        ringBase = new Color32[ringPx * ringPx];
        for (int n = 1; n <= pageSize; n++)
        {
            BuildRing(n);
            int count = ringSlice.Length;
            byte[] record = new byte[count * 5];
            Array.Copy(ringSlice, record, count);
            for (int i = 0; i < count; i++)
            {
                record[count + i * 4] = ringBase[i].r;
                record[count + i * 4 + 1] = ringBase[i].g;
                record[count + i * 4 + 2] = ringBase[i].b;
                record[count + i * 4 + 3] = ringBase[i].a;
            }
            add("ring" + n, record);
        }
        ringSlice = null;
        ringBase = null;
        ringSlices = -1;
        VitaUiPack.Off = false;
    }
#endif

    public static bool TestHold;    // test harness: open without the button held (shots)

    // Test harness: forget the shared textures, so the next open makes them again (VitaUiPack A/B)
    public static void ResetShared()
    {
        FreeRing();
        UnityEngine.Object.Destroy(stoneBar);
        UnityEngine.Object.Destroy(stoneButton);
        stoneBar = stoneButton = null;
        if (tabIcons != null)
            foreach (Texture2D icon in tabIcons)
                UnityEngine.Object.Destroy(icon);
        tabIcons = null;
        if (actionIcons != null)
        {
            UnityEngine.Object.Destroy(actionIcons[4]);     // 0-3 are the game's own icons
            UnityEngine.Object.Destroy(actionIcons[5]);
            UnityEngine.Object.Destroy(actionIcons[6]);
        }
        actionIcons = null;
        stone = null;
        ringStone = discStone = null;
    }

    static void FreeRing()
    {
        if (ringTexture != null)
            UnityEngine.Object.Destroy(ringTexture);
        ringTexture = null;
        ringSlice = null;
        ringBase = null;
        ringPixels = null;
        ringSlices = -1;
    }

    // Stone (rows top-down) at (x, y), tiled
    static Color32 Stone(int x, int y, float brightness)
    {
        Color32 c = stone[(y % stoneH) * stoneW + (x % stoneW)];
        return new Color32((byte)Mathf.Min(255, c.r * brightness), (byte)Mathf.Min(255, c.g * brightness), (byte)Mathf.Min(255, c.b * brightness), 255);
    }

    // The inventory stone for other Vita windows (VitaHudStyleWindow), optionally mixed towards a tint; the caller owns the texture
    internal static Texture2D ClassicStone(int w, int h, float brightness, Color32 tint, float tintAmount)
    {
        return StoneTexture(w, h, brightness, tint, tintAmount);
    }

    static Texture2D StoneTexture(int w, int h, float brightness)
    {
        return StoneTexture(w, h, brightness, new Color32(), 0);
    }

    static Texture2D StoneTexture(int w, int h, float brightness, Color32 tint, float tintAmount)
    {
        Texture2D t = VitaUiPack.Texture(StoneKey(w, h, brightness, tint, tintAmount), w, h, FilterMode.Point);
        if (t != null)
            return t;
        t = new Texture2D(w, h, TextureFormat.ARGB32, false);
        t.filterMode = FilterMode.Point;
        t.SetPixels32(StonePixels(w, h, brightness, tint, tintAmount));
        t.Apply(false, true);
        return t;
    }

    static string StoneKey(int w, int h, float brightness, Color32 tint, float tintAmount)
    {
        return "stone" + w + "x" + h + "b" + Mathf.RoundToInt(brightness * 100) + "t" + tint.r + "," + tint.g + "," + tint.b + "a" + Mathf.RoundToInt(tintAmount * 100);
    }

    static Color32[] StonePixels(int w, int h, float brightness, Color32 tint, float tintAmount)
    {
        LoadStone();
        Color32[] c = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                c[(h - 1 - y) * w + x] = Mix(Stone(x, y, brightness), tint, tintAmount);
        return c;
    }

    // Slice index of every ring pixel for n slices (255 = clear, 254 = inner disc, 253 = seam/edge), plus the stone with seams and the
    // classic bevel (light top-left, dark bottom-right). Edges are blended over the pixel's width, so circles and seams don't stair-step.
    // Hot loop (~90k pixels on open): plain int blends, the stone looked up from two prepared tables, angle maths only inside the ring.
    static void BuildRing(int n)
    {
        ringSlices = n;
        paintedSelected = -2;      // new base pixels: the next paint writes them all
        if (VitaUiPack.Ring(n, ringSlice, ringBase))
            return;
        LoadStone();
        if (ringStone == null)
        {
            ringStone = new Color32[ringSize * ringSize];
            discStone = new Color32[ringSize * ringSize];
            for (int y = 0; y < ringSize; y++)
                for (int x = 0; x < ringSize; x++)
                {
                    ringStone[y * ringSize + x] = Stone(x, y, 0.95f);
                    discStone[y * ringSize + x] = Stone(x, y, 0.8f);      // 0.7 lost the grain next to the slices
                }
        }
        float c = ringSize / 2f, step = Mathf.PI * 2 / n, px = 1f / ringScale;
        float clearSq = (R + px) * (R + px);
        Color32 clear = new Color32(0, 0, 0, 0);
        for (int y = 0; y < ringPx; y++)
        {
            float dy = c - (y + 0.5f) * px;      // native units
            int row = (ringPx - 1 - y) * ringPx;    // y counts down from the top; the texture's rows go up
            int stoneRow = (y / ringScale) * ringSize;
            for (int x = 0; x < ringPx; x++)
            {
                int i = row + x;
                float dx = (x + 0.5f) * px - c;
                float dSq = dx * dx + dy * dy;
                if (dSq >= clearSq)
                {
                    ringSlice[i] = 255;
                    ringBase[i] = clear;
                    continue;
                }
                float d = Mathf.Sqrt(dSq);
                int si = stoneRow + x / ringScale;
                float disc = Cover(r - d);
                Color32 col;
                byte slice;
                if (disc >= 1)
                {
                    col = Mix(discStone[si], DiscEdge(dy, d), Cover(d - (r - 1f)));     // sunken: a soft lit lower edge
                    slice = 254;
                }
                else
                {
                    // Slice stone and seams
                    float a = Mathf.Atan2(dx, dy);      // 0 = up, clockwise
                    if (a < 0)
                        a += Mathf.PI * 2;
                    float s = (a + step / 2) / step;
                    float seam = n > 1 ? Cover(1f - Mathf.Abs(s - Mathf.Round(s)) * step * d) : 0;     // distance to the nearest seam
                    col = Mix(ringStone[si], seamColor, seam);
                    slice = seam < 0.5f ? (byte)((int)s % n) : (byte)253;
                    // Outer bevel, then the centre disc's edge
                    float rim = Cover(d - (R - 1.5f));
                    col = Mix(col, darkEdge, rim);     // dark all round (a light top half read as a white arch)
                    if (disc > 0)
                        col = Mix(col, Mix(discStone[si], DiscEdge(dy, d), Cover(d - (r - 1f))), disc);
                    if (rim >= 0.5f)
                        slice = 253;
                    if (disc >= 0.5f)
                        slice = 254;
                    col.a = (byte)(255 * (1 - Cover(d - R)));
                }
                ringSlice[i] = slice;
                ringBase[i] = col;
            }
        }
    }

    // The centre disc's edge, shaded by angle: dark at the top, lit at the bottom. A plain top/bottom switch left a step at 3 and 9 o'clock.
    static Color32 DiscEdge(float dy, float d)
    {
        return Mix(darkEdge, discLight, 0.5f - 0.5f * dy / Mathf.Max(d, 0.001f));
    }

    // How much of a ring pixel lies past an edge, from the signed distance to it in native units (0 = none, 1 = all)
    static float Cover(float distance)
    {
        float t = distance * ringScale + 0.5f;
        return t <= 0 ? 0 : t >= 1 ? 1 : t;
    }

    static Color32 Mix(Color32 a, Color32 b, float t)
    {
        if (t <= 0)
            return a;
        if (t >= 1)
            return b;
        int k = (int)(t * 256);
        return new Color32((byte)(a.r + ((b.r - a.r) * k >> 8)), (byte)(a.g + ((b.g - a.g) * k >> 8)), (byte)(a.b + ((b.b - a.b) * k >> 8)), a.a);
    }

    // Brightens the selected slice; after the first paint only the old and the new slice's pixels change
    static void PaintRing(int sel)
    {
        bool full = paintedSelected == -2;
        for (int i = 0; i < ringPixels.Length; i++)
        {
            byte s = ringSlice[i];
            if (s == sel)
            {
                Color32 b = ringBase[i];
                ringPixels[i] = new Color32((byte)Math.Min(255, b.r * 333 >> 8), (byte)Math.Min(255, b.g * 320 >> 8), (byte)Math.Min(255, b.b * 269 >> 8), b.a);
            }
            else if (full || s == paintedSelected)
                ringPixels[i] = ringBase[i];
        }
        paintedSelected = sel;
        ringTexture.SetPixels32(ringPixels);
        ringTexture.Apply(false, false);
    }

    Panel Box(Vector2 position, Vector2 size, Texture2D texture, bool bevel)
    {
        Panel box = new Panel { Position = position, Size = size, BackgroundTexture = texture, BackgroundTextureLayout = BackgroundLayout.StretchToFill };
        NativePanel.Components.Add(box);
        if (bevel)
        {
            box.Outline.Enabled = true;
            box.Outline.Color = lightEdge;
            box.Outline.Sides = Sides.Top | Sides.Left;
            Panel shade = new Panel { Size = size };     // ResizeToFill drew this as a stray line across the box's top-left (seen 11-13 .. 11-14)
            shade.Outline.Enabled = true;
            shade.Outline.Color = darkEdge;
            shade.Outline.Sides = Sides.Bottom | Sides.Right;
            box.Components.Add(shade);
        }
        return box;
    }

    static TextLabel Label(string text, Vector2 position, float scale, Panel parent)
    {
        TextLabel label = DaggerfallUI.AddTextLabel(Font, position, text, parent);
        label.TextScale = scale;
        label.TextColor = DaggerfallUI.DaggerfallDefaultTextColor;
        return label;
    }

    // Puts a label's text centred on x (in its parent's space) at y
    static void Centre(TextLabel label, string text, float x, float y)
    {
        label.Text = text;
        label.Position = new Vector2(Mathf.Round(x - label.TextWidth / 2f), y);
    }

    static float Width(string text, float scale)
    {
        return Font.CalculateTextWidth(text, Vector2.one) * scale;     // as TextLabel.TextWidth: native width at scale 1, times TextScale
    }

    // Wraps text to fit maxWidth in at most maxLines lines (words first, then characters for long words and CJK); the last line ends ".." if cut
    static List<string> Fit(string text, float maxWidth, int maxLines, float scale)
    {
        List<string> lines = new List<string>();
        string line = "";
        foreach (string word in text.Split(' '))
        {
            string tryLine = line.Length == 0 ? word : line + " " + word;
            if (Width(tryLine, scale) <= maxWidth)
            {
                line = tryLine;
                continue;
            }
            if (line.Length > 0)
                lines.Add(line);
            line = "";
            foreach (char ch in word)
            {
                if (line.Length > 0 && Width(line + ch, scale) > maxWidth)
                {
                    lines.Add(line);
                    line = "";
                }
                line += ch;
            }
        }
        if (line.Length > 0)
            lines.Add(line);
        if (lines.Count > maxLines)
        {
            lines.RemoveRange(maxLines, lines.Count - maxLines);
            lines[maxLines - 1] += "..";
        }
        if (lines.Count > 0)
        {
            string last = lines[lines.Count - 1];
            while (last.Length > 3 && Width(last, scale) > maxWidth)
                last = last.Substring(0, last.Length - 3) + "..";
            lines[lines.Count - 1] = last;
        }
        return lines;
    }

    // Shrinks a label (down to minLabelScale) until it wraps at spaces with nothing cut ("Slowfalling", "Slow motion");
    // CJK keeps its size and wraps by character
    static float FitScale(string text, float maxWidth, int maxLines)
    {
        foreach (char ch in text)
            if (ch >= 0x2E80)
                return labelScale;
        float scale = labelScale;
        while (scale > minLabelScale && string.Join(" ", Fit(text, maxWidth, maxLines, scale).ToArray()) != text)
            scale -= 0.025f;
        return scale;
    }

    // PlayStation button glyphs baked on the PC (vita/tools/padglyphs.py, from Kenney's CC0 Input Prompts): VitaNative/padglyphs.bin.
    // Ids match the pack. Each is drawn 8 units tall, its width from the pack (shoulder keys and the D-pad are wider).
    internal enum Glyph { Cross, Circle, Square, Triangle, L, R, DPad, DPadVertical, DPadDown, StickL, StickR }
    static Texture2D[] glyphs;
    static float[] glyphWidths;

    internal static void MakeGlyphs()
    {
        if (glyphs != null)
            return;
        glyphs = new Texture2D[11];
        glyphWidths = new float[11];
        for (int g = 0; g < 11; g++)
            glyphWidths[g] = 8;
        byte[] data;
        try { data = System.IO.File.ReadAllBytes(System.IO.Path.Combine(System.IO.Path.Combine(Application.streamingAssetsPath, "VitaNative"), "padglyphs.bin")); }
        catch (Exception e) { Debug.LogWarning("VitaQuickMenu: no padglyphs.bin (" + e.Message + ")"); return; }
        if (data.Length < 8 || data[0] != 'V' || data[1] != 'G' || data[2] != 'L' || data[3] != 'Y')
            return;
        int count = BitConverter.ToUInt16(data, 6), at = 8;
        for (int n = 0; n < count && at + 6 <= data.Length; n++)
        {
            int id = BitConverter.ToUInt16(data, at), w = BitConverter.ToUInt16(data, at + 2), h = BitConverter.ToUInt16(data, at + 4);
            at += 6;
            if (at + w * h * 4 > data.Length)
                break;
            if (id < glyphs.Length)
            {
                Color32[] c = new Color32[w * h];
                for (int y = 0; y < h; y++)                 // pack rows run top to bottom, Unity's bottom to top
                    for (int x = 0; x < w; x++)
                    {
                        int i = at + ((h - 1 - y) * w + x) * 4;
                        c[y * w + x] = new Color32(data[i], data[i + 1], data[i + 2], data[i + 3]);
                    }
                Texture2D t = new Texture2D(w, h, TextureFormat.ARGB32, false);
                t.filterMode = FilterMode.Bilinear;
                t.wrapMode = TextureWrapMode.Clamp;
                t.SetPixels32(c);
                t.Apply(false, true);
                glyphs[id] = t;
                glyphWidths[id] = Mathf.Round(8f * w / h * 2) / 2;
            }
            at += w * h * 4;
        }
    }

    internal static float GlyphWidth(Glyph g)
    {
        return glyphWidths[(int)g];
    }

    // A glyph at native size, 8 units tall
    internal static Panel GlyphPanel(Glyph g, Vector2 position, Panel parent)
    {
        Panel p = new Panel { Position = position, Size = new Vector2(GlyphWidth(g), 8), BackgroundTexture = glyphs[(int)g],
            BackgroundTextureLayout = BackgroundLayout.StretchToFill };
        parent.Components.Add(p);
        return p;
    }

    // Hint bar: glyph + word per action, centred; a refusal message replaces it for a moment
    void LayoutHint()
    {
        foreach (BaseScreenComponent part in hintParts)
            NativePanel.Components.Remove(part);
        hintParts.Clear();
        List<KeyValuePair<Glyph, string>> items = new List<KeyValuePair<Glyph, string>>();
        if (page == 0)
        {
            items.Add(new KeyValuePair<Glyph, string>(Glyph.Cross, Text("vitaQuickRHand")));
            items.Add(new KeyValuePair<Glyph, string>(Glyph.Square, Text("vitaQuickLHand")));
        }
        else if (page == 1)
        {
            items.Add(new KeyValuePair<Glyph, string>(Glyph.Cross, Text("vitaQuickCast")));
            items.Add(new KeyValuePair<Glyph, string>(Glyph.Square, Text("vitaQuickSetL")));
        }
        else
        {
            items.Add(new KeyValuePair<Glyph, string>(Glyph.Cross, Text("vitaQuickUse")));
            items.Add(new KeyValuePair<Glyph, string>(Glyph.Square, Text("vitaQuickSetL")));
        }
        items.Add(new KeyValuePair<Glyph, string>(Glyph.StickR, Text("vitaQuickPick")));
        items.Add(new KeyValuePair<Glyph, string>(Glyph.DPad, Text("vitaQuickHotkey")));
        items.Add(new KeyValuePair<Glyph, string>(Glyph.R, Text("vitaQuickPageHint")));
        hintParts.AddRange(GlyphHint(items, NativePanel, hintUntil == 0));
        if (hintUntil == 0)
            hint.Text = "";
    }

    // Glyph + word per item, centred in the 306-unit bottom bar (gaps shrink when a language runs long).
    // An item with no word shares the next item's word ("[D-pad][stick] Pick"); NoGlyph = a word alone.
    internal const Glyph NoGlyph = (Glyph)(-1);
    internal static List<BaseScreenComponent> GlyphHint(List<KeyValuePair<Glyph, string>> items, Panel parent, bool show)
    {
        const float gap = 2;
        float words = 0;
        int breaks = 0;
        for (int n = 0; n < items.Count; n++)
        {
            if (items[n].Key != NoGlyph)
                words += GlyphWidth(items[n].Key) + gap;
            words += Width(items[n].Value, labelScale);
            if (n < items.Count - 1 && items[n].Value != "")
                breaks++;
        }
        float spacing = breaks > 0 ? Mathf.Clamp((306 - words) / breaks, 4, 12) : 0;
        float x = Mathf.Round(160 - (words + spacing * breaks) / 2);
        List<BaseScreenComponent> parts = new List<BaseScreenComponent>();
        foreach (var item in items)
        {
            if (item.Key != NoGlyph)
            {
                Panel g = GlyphPanel(item.Key, new Vector2(x, 187.5f), parent);
                x += g.Size.x + gap;
                g.Enabled = show;
                parts.Add(g);
            }
            if (item.Value == "")
                continue;
            TextLabel label = Label(item.Value, new Vector2(x, 190f), labelScale, parent);   // same centre as the glyph and the bar
            x += Width(item.Value, labelScale) + spacing;
            label.Enabled = show;
            parts.Add(label);
        }
        return parts;
    }

    #endregion

    #region Pages

    void ShowPage(int newPage)
    {
        page = newPage;
        ringPage = 0;
        selected = -1;
        Refresh();
    }

    // Rebuilds the entry list for the current page (after every action: equipped hands, stack counts and spell costs change)
    void Refresh()
    {
        entries.Clear();
        if (actionRing)
            AddActions();
        else if (page == 0)
            AddWeapons();
        else if (page == 1)
            AddSpells();
        else
            AddItems();
        if (entries.Count == 0)
            entries.Add(new Entry { kind = Kind.Nothing, name = Text("vitaQuickNothing") });

        int pages = (entries.Count + pageSize - 1) / pageSize;
        ringPage = Mathf.Clamp(ringPage, 0, pages - 1);
        if (selected < 0)
            selected = DefaultSelection() % pageSize;
        selected = Mathf.Clamp(selected, 0, SliceCount() - 1);
        if (actionRing)
        {
            LayoutSlices();
            return;
        }

        for (int k = 0; k < 3; k++)
        {
            tabDim[k].Enabled = k != page;
            tabs[k].Outline.Enabled = false;     // the dimmed other tabs already show which page is up
        }
        LayoutHint();
        pageButton.Enabled = pages > 1;
        Centre(pageLabel, string.Format(Text("vitaQuickPage"), ringPage + 1, pages), 27, 2.5f);
        string next = Fit(Text("vitaQuickNextPage"), 38, 1, labelScale)[0];
        float nextX = 27 - (pageGlyph.Size.x + 2 + Width(next, labelScale)) / 2;      // D-pad glyph + word, centred under the page number
        pageGlyph.Position = new Vector2(Mathf.Round(nextX), 8.5f);
        pageKey.Text = next;
        pageKey.Position = new Vector2(Mathf.Round(nextX + pageGlyph.Size.x + 2), 9f);

        LayoutSlices();
    }

    int SliceCount()
    {
        return Mathf.Min(pageSize, entries.Count - ringPage * pageSize);
    }

    Entry Selected
    {
        get
        {
            int i = ringPage * pageSize + selected;
            return selected >= 0 && i < entries.Count ? entries[i] : null;
        }
    }

    int DefaultSelection()
    {
        for (int i = 0; i < entries.Count; i++)
        {
            Entry e = entries[i];
            if (actionRing && e.kind == Kind.Mode && e.mode == GameManager.Instance.PlayerActivate.CurrentMode)
                return i;
            if (page == 0 && e.item != null && e.item == Player.ItemEquipTable.GetItem(EquipSlots.RightHand))
                return SetRingPage(i);
            if (page == 1 && e.kind == Kind.Spell && e.spell.Name == LSpellName())
                return SetRingPage(i);
        }
        return 0;
    }

    int SetRingPage(int index)
    {
        ringPage = index / pageSize;
        return index;
    }

    // Clockwise from the top
    void AddActions()
    {
        entries.Add(new Entry { kind = Kind.Mode, mode = PlayerActivateModes.Talk, name = Text("vitaModeTalk") });
        entries.Add(new Entry { kind = Kind.Mode, mode = PlayerActivateModes.Info, name = Text("vitaModeInfo") });
        entries.Add(new Entry { kind = Kind.Mode, mode = PlayerActivateModes.Grab, name = Text("vitaModeGrab") });
        entries.Add(new Entry { kind = Kind.Rest, name = Text("vitaRest") });
        entries.Add(new Entry { kind = Kind.Map, name = Text("vitaMap"), above = Text("vitaMapAbove") });
        entries.Add(new Entry { kind = Kind.Automap, name = Text("vitaAutomap") });
        entries.Add(new Entry { kind = Kind.Mode, mode = PlayerActivateModes.Steal, name = Text("vitaModeSteal") });
    }

    void AddWeapons()
    {
        ItemEquipTable equip = Player.ItemEquipTable;
        for (int i = 0; i < Player.Items.Count; i++)
        {
            DaggerfallUnityItem item = Player.Items.GetItem(i);
            bool weapon = item.ItemGroup == ItemGroups.Weapons && item.TemplateIndex != (int)Weapons.Arrow;
            if (!weapon && !item.IsShield)
                continue;
            string hand = item == equip.GetItem(EquipSlots.RightHand) ? Text("vitaQuickRHand") :
                          item == equip.GetItem(EquipSlots.LeftHand) ? Text("vitaQuickLHand") : null;
            entries.Add(new Entry { kind = Kind.Weapon, item = item, name = item.LongName, info = hand });
        }
        entries.Add(new Entry { kind = Kind.SwapHands, name = Text("vitaQuickSwap"),
            info = GameManager.Instance.WeaponManager.UsingRightHand ? Text("vitaQuickRHand") : Text("vitaQuickLHand") });
    }

    void AddSpells()
    {
        // Spells need the spellbook, as the spellbook window and recast do
        if (!Player.Items.Contains(ItemGroups.MiscItems, (int)MiscItems.Spellbook))
            return;
        EffectBundleSettings[] spells = Player.GetSpells();
        string quick = LSpellName();
        if (spells != null)
        {
            foreach (EffectBundleSettings spell in spells)
            {
                int cost = FormulaHelper.CalculateTotalEffectCosts(spell.Effects, spell.TargetType, null, spell.MinimumCastingCost).spellPointCost;
                if (spell.Tag == PlayerEntity.lycanthropySpellTag)
                    cost = 0;
                string info = string.Format(Text("vitaQuickCost"), cost);
                if (spell.Name == quick)
                    info += " " + Text("vitaQuickOnL");
                entries.Add(new Entry { kind = Kind.Spell, spell = spell, name = spell.Name, info = info });
            }
        }
        entries.Add(new Entry { kind = Kind.Spellbook, name = Text("vitaQuickSpellbook") });
    }

    // Same list as DaggerfallUseMagicItemWindow.UpdateUsableMagicItems
    void AddItems()
    {
        for (int i = 0; i < Player.Items.Count; i++)
        {
            DaggerfallUnityItem item = Player.Items.GetItem(i);
            bool usable = false;
            if (item.IsEnchanted && item.LegacyEnchantments != null)
            {
                foreach (DaggerfallEnchantment enchantment in item.LegacyEnchantments)
                    if (enchantment.type == EnchantmentTypes.CastWhenUsed)
                        usable = true;
            }
            else if (item.IsPotion)
                usable = true;
            if (usable)
            {
                string info = item.IsPotion ? "x" + item.stackCount : item.ConditionPercentage + "%";
                if (!item.IsPotion && "M:" + item.UID == LItemKey())
                    info += " " + Text("vitaQuickOnL");
                entries.Add(new Entry { kind = Kind.Item, item = item, name = item.LongName, info = info });
            }
        }
    }

    void LayoutSlices()
    {
        foreach (BaseScreenComponent part in sliceParts)
            NativePanel.Components.Remove(part);
        sliceParts.Clear();

        int n = SliceCount();
        if (n != ringSlices)
            BuildRing(n);
        drawnSelected = -2;
        UpdateSelection();

        float mid = (R + r) / 2f;
        float maxWidth = n > 2 ? 2 * mid * Mathf.Sin(Mathf.PI / n) - 6 : 70;
        for (int s = 0; s < n; s++)
        {
            Entry e = entries[ringPage * pageSize + s];
            float a = s * Mathf.PI * 2 / n;
            Vector2 at = centre + new Vector2(Mathf.Sin(a), -Mathf.Cos(a)) * (n == 1 ? (R + r) / 2f : mid);

            float scale = FitScale(e.name, maxWidth, 2);
            float lineHeight = Font.GlyphHeight * scale + 1;
            List<string> lines = Fit(e.name, maxWidth, 2, scale);
            int nameLines = lines.Count;
            if (!string.IsNullOrEmpty(e.info))
                lines.AddRange(Fit(e.info, maxWidth, 1, scale));
            Texture2D icon = Icon(e);
            Vector2 iconSize = Vector2.zero;
            if (icon != null)
            {
                float k = Mathf.Min(1f, Mathf.Min(Mathf.Min(maxWidth - 6, 32) / icon.width, (lines.Count > 2 ? 11f : 15f) / icon.height));
                iconSize = new Vector2(Mathf.Round(icon.width * k), Mathf.Round(icon.height * k));
            }
            string keys = HotkeyTag(e);
            bool above = !string.IsNullOrEmpty(e.above);
            float block = (above ? lineHeight : 0) + iconSize.y + (icon != null ? 1 : 0) + lines.Count * lineHeight + (keys != null ? 9 : 0);
            float y = at.y - block / 2;
            if (above)
            {
                TextLabel label = Label("", Vector2.zero, scale, NativePanel);
                sliceParts.Add(label);
                Centre(label, e.above, at.x, Mathf.Round(y));
                label.TextColor = s == selected ? DaggerfallUI.DaggerfallBrighterSelectedTextColor : DaggerfallUI.DaggerfallDefaultTextColor;
                y += lineHeight;
            }
            if (icon != null)
            {
                Panel p = new Panel { Position = new Vector2(Mathf.Round(at.x - iconSize.x / 2), Mathf.Round(y)), Size = iconSize,
                    BackgroundTexture = icon, BackgroundTextureLayout = BackgroundLayout.StretchToFill };
                AddPart(p);
                y += iconSize.y + 1;
            }
            for (int l = 0; l < lines.Count; l++)
            {
                TextLabel label = Label("", Vector2.zero, scale, NativePanel);
                sliceParts.Add(label);
                Centre(label, lines[l], at.x, Mathf.Round(y));
                label.TextColor = l >= nameLines ? Color.white : s == selected ? DaggerfallUI.DaggerfallBrighterSelectedTextColor : DaggerfallUI.DaggerfallDefaultTextColor;
                y += lineHeight;
            }
            if (keys != null)
            {
                // D-pad glyph + its hotkey numbers ("1", "2 3")
                float w = GlyphWidth(Glyph.DPad) + 2 + Width(keys, labelScale);
                Panel g = GlyphPanel(Glyph.DPad, new Vector2(Mathf.Round(at.x - w / 2), Mathf.Round(y + 1)), NativePanel);
                sliceParts.Add(g);
                TextLabel label = Label(keys, new Vector2(g.Position.x + g.Size.x + 2, Mathf.Round(y + 2)), labelScale, NativePanel);
                label.TextColor = Color.white;
                sliceParts.Add(label);
            }
        }

        // Above the ring: the selected entry's full name (one line, shrunk if needed; the lit tab already names the page).
        // Centre disc: its details only (hand, condition, cost, count).
        Entry sel = Selected;
        string name = sel != null ? (string.IsNullOrEmpty(sel.above) ? sel.name : sel.above + " " + sel.name) : Text(page == 0 ? "vitaQuickWeapons" : page == 1 ? "vitaQuickSpells" : "vitaQuickItems");
        float titleScale = 1f;
        while (titleScale > 0.6f && Width(name, titleScale) > 300)
            titleScale -= 0.05f;
        title.TextScale = titleScale;
        Centre(title, Fit(name, 300, 1, titleScale)[0], 160, 20);
        string info = sel != null ? CentreInfo(sel) : null;
        if (!string.IsNullOrEmpty(info))
        {
            float lineHeight = Font.GlyphHeight * labelScale + 1;
            List<string> lines = Fit(info, 2 * r - 8, 3, labelScale);
            float y = centre.y - lines.Count * lineHeight / 2;
            for (int l = 0; l < lines.Count; l++)
            {
                TextLabel label = Label("", Vector2.zero, labelScale, NativePanel);
                sliceParts.Add(label);
                Centre(label, lines[l], centre.x, Mathf.Round(y));
                label.TextColor = Color.white;
                y += lineHeight;
            }
        }
    }

    void AddPart(BaseScreenComponent part)
    {
        NativePanel.Components.Add(part);
        sliceParts.Add(part);
    }

    string CentreInfo(Entry e)
    {
        switch (e.kind)
        {
            case Kind.Weapon:
                return (e.info != null ? e.info + " " : "") + e.item.ConditionPercentage + "%";
            case Kind.Spell:
            case Kind.Item:
            case Kind.SwapHands:
                return e.info;
            default:
                return null;
        }
    }

    static Texture2D Icon(Entry e)
    {
        switch (e.kind)
        {
            case Kind.Weapon:
            case Kind.Item:
                return DaggerfallUnity.Instance.ItemHelper.GetInventoryImage(e.item).texture;
            case Kind.Spell:
                return DaggerfallUI.Instance.SpellIconCollection.GetSpellIcon(e.spell.Icon);
            case Kind.SwapHands:
                return tabIcons[0];
            case Kind.Spellbook:
                return tabIcons[1];
            case Kind.Mode:
                return actionIcons[e.mode == PlayerActivateModes.Talk ? 0 : e.mode == PlayerActivateModes.Info ? 1 : e.mode == PlayerActivateModes.Grab ? 2 : 3];
            case Kind.Rest:
                return actionIcons[4];
            case Kind.Automap:
                return actionIcons[5];
            case Kind.Map:
                return actionIcons[6];
            default:
                return null;
        }
    }

    void UpdateSelection()
    {
        if (drawnSelected == selected)
            return;
        if (drawnSelected >= 0)
        {
            LayoutSlices();     // label colours and the centre text follow the selection; it repaints the ring
            return;
        }
        drawnSelected = selected;
        PaintRing(selected);
        ringPanel.BackgroundTexture = ringTexture;
    }

    #endregion

    #region Input

    public override void Update()
    {
        base.Update();
        if (uiManager.TopWindow != this)    // Circle closed it, or a message box is up
            return;
        if (actionRing)
        {
            // Releasing D-pad Down chooses the slice; nothing else does anything here
            if (!Input.GetKey(KeyCode.JoystickButton10) && !TestHold)
            {
                Entry chosen = Selected;
                CloseWindow();
                DoAction(chosen);
                return;
            }
            StickPick();
            return;
        }
        if (!Input.GetKey(KeyCode.JoystickButton4) && !TestHold)
        {
            CloseWindow();
            return;
        }
        if (hintUntil > 0 && Time.unscaledTime > hintUntil)
        {
            hintUntil = 0;
            hint.Text = "";                     // the action ring keeps its glyph hint (Refresh rebuilds only the menu's)
            foreach (BaseScreenComponent part in hintParts)
                part.Enabled = true;
            Refresh();
        }

        StickPick();

        // D-pad Left/Right: tap = hotkey 1/2, hold (bindHold s) = hotkey 3/4
        for (int k = 0; k < 2; k++)
        {
            KeyCode key = k == 0 ? KeyCode.JoystickButton11 : KeyCode.JoystickButton9;
            if (Input.GetKeyDown(key))
                bindDownTime[k] = Time.unscaledTime;
            if (bindDownTime[k] < 0)
                continue;
            if (Time.unscaledTime - bindDownTime[k] >= bindHold)
            {
                bindDownTime[k] = -1;
                Bind(k + 2, Selected);
            }
            else if (Input.GetKeyUp(key))
            {
                bindDownTime[k] = -1;
                Bind(k, Selected);
            }
        }
        UpdateButtons();
    }

    // Right stick: the slice it points at (up = first, clockwise); centred keeps the last pick
    void StickPick()
    {
        float x = Input.GetAxisRaw("Axis4"), y = Input.GetAxisRaw("Axis5");
        int n = SliceCount();
        if (x * x + y * y > stickPick * stickPick && n > 0)
        {
            float a = Mathf.Atan2(x, y);
            if (a < 0)
                a += Mathf.PI * 2;
            int pick = Mathf.RoundToInt(a / (Mathf.PI * 2 / n)) % n;
            if (pick != selected)
            {
                selected = pick;
                UpdateSelection();
            }
        }
    }

    void UpdateButtons()
    {
        if (Input.GetKeyDown(KeyCode.JoystickButton5))      // R: next page
            ShowPage((page + 1) % 3);
        else if (Input.GetKeyDown(KeyCode.JoystickButton8) || Input.GetKeyDown(KeyCode.JoystickButton10))     // D-pad Up/Down: other rings
        {
            int pages = (entries.Count + pageSize - 1) / pageSize;
            if (pages > 1)
            {
                ringPage = (ringPage + (Input.GetKeyDown(KeyCode.JoystickButton8) ? pages - 1 : 1)) % pages;
                selected = 0;
                Refresh();
            }
        }
        else if (Input.GetKeyDown(KeyCode.JoystickButton0))
            Use(Selected, EquipSlots.RightHand);
        else if (Input.GetKeyDown(KeyCode.JoystickButton2) && page == 0)
            Use(Selected, EquipSlots.LeftHand);
        else if (Input.GetKeyDown(KeyCode.JoystickButton2) && page == 1 && Selected != null && Selected.kind == Kind.Spell)
        {
            SetLSpell(Selected.spell.Name);
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            Refresh();
        }
        else if (Input.GetKeyDown(KeyCode.JoystickButton2) && page == 2 && Selected != null && Selected.kind == Kind.Item && !Selected.item.IsPotion)
        {
            SetLSpell("M:" + Selected.item.UID);
            DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
            Refresh();
        }
    }

    public override void OnPush()
    {
        base.OnPush();
        InputManager.Instance.VitaQuickMenuOpen = true;
        GameManager.Instance.PlayerMouseLook.ForceHideCursor(true);    // no cursor, and the right stick doesn't turn the view
        ApplyTimeMode();
    }

    public override void OnPop()
    {
        base.OnPop();
        InputManager.Instance.VitaQuickMenuOpen = false;
        GameManager.Instance.PlayerMouseLook.ForceHideCursor(false);
        if (savedTimeScale >= 0)
            Time.timeScale = savedTimeScale;
        savedTimeScale = -1;
        SetEquipDelayTime();     // no SaveSettings here: SetLSpell and Bind save when they change something (the write took ~300 ms per close)
        ringPanel.BackgroundTexture = null;
        FreeRing();
    }

    // The window was pushed paused or not by PauseWhileOpen (the pause menu's quick menu time option)
    void ApplyTimeMode()
    {
        int mode = DaggerfallUnity.Settings.VitaQuickTime;
        if (savedTimeScale >= 0)
        {
            Time.timeScale = savedTimeScale;
            savedTimeScale = -1;
        }
        PauseWhileOpen = mode == TimePaused;
        GameManager.Instance.PauseGame(mode == TimePaused);
        if (mode == TimeSlow)
        {
            savedTimeScale = Time.timeScale;
            Time.timeScale = savedTimeScale * slowScale;
        }
    }

    // Action ring choice, after the window closed
    static void DoAction(Entry e)
    {
        if (e == null)
            return;
        if (e.kind == Kind.Mode)
            GameManager.Instance.PlayerActivate.ChangeInteractionMode(e.mode);
        else if (e.kind == Kind.Rest)
            DaggerfallUI.PostMessage(DaggerfallUIMessages.dfuiOpenRestWindow);
        else if (e.kind == Kind.Map)
            DaggerfallUI.PostMessage(DaggerfallUIMessages.dfuiOpenTravelMapWindow);
        else if (e.kind == Kind.Automap)
            DaggerfallUI.PostMessage(DaggerfallUIMessages.dfuiOpenAutomap);
    }

    void Use(Entry e, EquipSlots hand)
    {
        if (e == null)
            return;
        switch (e.kind)
        {
            case Kind.Weapon:
                Equip(e.item, hand);
                Refresh();
                break;
            case Kind.SwapHands:
                GameManager.Instance.WeaponManager.VitaToggleHand();
                Refresh();
                break;
            case Kind.Spell:
                if (hand != EquipSlots.RightHand)
                    break;
                CloseWindow();
                CastNow(e.spell);
                break;
            case Kind.Spellbook:
                if (hand != EquipSlots.RightHand)
                    break;
                CloseWindow();
                DaggerfallUI.PostMessage(DaggerfallUIMessages.dfuiOpenSpellBookWindow);
                break;
            case Kind.Item:
                if (hand != EquipSlots.RightHand)
                    break;
                // As DaggerfallUseMagicItemWindow.MagicItemPicker_OnItemPicked
                DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
                CloseWindow();
                if (e.item.IsPotion)
                {
                    GameManager.Instance.PlayerEffectManager.DrinkPotion(e.item);
                    Player.Items.RemoveOne(e.item);
                }
                else if (e.item.IsEnchanted)
                    UseMagicItem(e.item);
                break;
        }
    }

    // As DaggerfallInventoryWindow.EquipItem (broken and forbidden checks, armour values), with the hand chosen for one-handed weapons.
    // The same hand again takes the item off.
    void Equip(DaggerfallUnityItem item, EquipSlots hand)
    {
        int refusal = EquipItem(item, hand);
        if (refusal > 0)
            ShowMessage(refusal, refusal == ItemBrokenTextId ? item : null);
    }

    const int ItemBrokenTextId = 29;
    const int ForbiddenEquipmentTextId = 1068;

    // Returns the refusal text id (broken, forbidden) or 0
    static int EquipItem(DaggerfallUnityItem item, EquipSlots hand)
    {
        ItemEquipTable equip = Player.ItemEquipTable;

        if (item.IsEquipped && (item.EquipSlot == hand || ItemEquipTable.GetItemHands(item) != ItemHands.Either))
        {
            if (equip.UnequipItem(item.EquipSlot) != null)
                Player.UpdateEquippedArmorValues(item, false);
            return 0;
        }
        if (item.currentCondition < 1)
            return ItemBrokenTextId;
        bool prohibited = false;
        PlayerEntity player = Player;
        if (item.ItemGroup == ItemGroups.Armor)
        {
            if (item.IsShield && ((1 << (item.TemplateIndex - (int)Armor.Buckler) & (int)player.Career.ForbiddenShields) != 0))
                prohibited = true;
            else if (!item.IsShield && (1 << (item.NativeMaterialValue >> 8) & (int)player.Career.ForbiddenArmors) != 0)
                prohibited = true;
            else if (((item.nativeMaterialValue >> 8) == 2) && (1 << (item.NativeMaterialValue & 0xFF) & (int)player.Career.ForbiddenMaterials) != 0)
                prohibited = true;
        }
        else if (item.ItemGroup == ItemGroups.Weapons)
        {
            if ((item.GetWeaponSkillUsed() & (int)player.Career.ForbiddenProficiencies) != 0)
                prohibited = true;
            else if ((1 << item.NativeMaterialValue & (int)player.Career.ForbiddenMaterials) != 0)
                prohibited = true;
        }
        if (prohibited)
            return ForbiddenEquipmentTextId;

        List<DaggerfallUnityItem> unequipped = new List<DaggerfallUnityItem>();
        bool either = ItemEquipTable.GetItemHands(item) == ItemHands.Either;
        // A one-handed weapon into the left hand can't share with a two-hander (Daggerfall only clears that for shields)
        DaggerfallUnityItem right = equip.GetItem(EquipSlots.RightHand);
        if (either && hand == EquipSlots.LeftHand && right != null && ItemEquipTable.GetItemHands(right) == ItemHands.Both)
            equip.UnequipItem(EquipSlots.RightHand, unequipped);
        if (either)
        {
            // Moving it between hands: off the other one first
            if (item.IsEquipped)
                equip.UnequipItem(item.EquipSlot, unequipped);
            equip.VitaForceHand = hand;
        }
        List<DaggerfallUnityItem> list = equip.EquipItem(item);
        equip.VitaForceHand = EquipSlots.None;
        if (list != null)
        {
            unequipped.AddRange(list);
            foreach (DaggerfallUnityItem off in unequipped)
                player.UpdateEquippedArmorValues(off, false);
            player.UpdateEquippedArmorValues(item, true);
        }
        return 0;
    }

    // A classic message (RSC text) as one line
    static string RscLine(int textId, DaggerfallUnityItem item)
    {
        TextFile.Token[] tokens = DaggerfallUnity.Instance.TextProvider.GetRSCTokens(textId);
        if (tokens == null || tokens.Length == 0)
            return null;
        MacroHelper.ExpandMacros(ref tokens, item);
        List<string> words = new List<string>();
        foreach (TextFile.Token token in tokens)
            if (token.formatting == TextFile.Formatting.Text && !string.IsNullOrEmpty(token.text))
                words.Add(token.text.Trim());
        return string.Join(" ", words.ToArray());
    }

    void ShowMessage(int textId, DaggerfallUnityItem item)
    {
        // In the hint bar for a moment: a message box would need its own button press while L is held
        string line = RscLine(textId, item);
        if (line == null)
            return;
        Centre(hint, Fit(line, 306, 1, labelScale)[0], 160, 190f);
        hint.TextColor = DaggerfallUI.DaggerfallBrighterSelectedTextColor;
        hintUntil = Time.unscaledTime + 2.5f;
        foreach (BaseScreenComponent part in hintParts)
            part.Enabled = false;
        DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
    }

    // As DaggerfallInventoryWindow.SetEquipDelayTime: changing what a hand holds takes Daggerfall's equip time
    void SetEquipDelayTime()
    {
        SetEquipDelayTime(lastRightHandItem, lastLeftHandItem);
    }

    static void SetEquipDelayTime(DaggerfallUnityItem lastRightHandItem, DaggerfallUnityItem lastLeftHandItem)
    {
        DaggerfallUnityItem right = Player.ItemEquipTable.GetItem(EquipSlots.RightHand);
        DaggerfallUnityItem left = Player.ItemEquipTable.GetItem(EquipSlots.LeftHand);
        int delayRight = 0, delayLeft = 0;
        if (lastRightHandItem != right)
        {
            if (lastRightHandItem != null)
                delayRight = WeaponManager.EquipDelayTimes[lastRightHandItem.GroupIndex];
            if (right != null)
                delayRight += WeaponManager.EquipDelayTimes[right.GroupIndex];
        }
        if (lastLeftHandItem != left)
        {
            if (lastLeftHandItem != null)
                delayLeft = WeaponManager.EquipDelayTimes[lastLeftHandItem.GroupIndex];
            if (left != null)
                delayLeft += WeaponManager.EquipDelayTimes[left.GroupIndex];
        }
        GameManager.Instance.WeaponManager.EquipCountdownRightHand += delayRight;
        GameManager.Instance.WeaponManager.EquipCountdownLeftHand += delayLeft;
    }

    #endregion

    #region L spell (tap L) and hotkeys (D-pad Left/Right)

    // Both saved for the current character only: "<character name>|<L action>", "<character name>|<key 1>|..|<key 4>"
    // The L action is a spell name, or "M:<item UID>" for a magic item's cast-when-used power (as its hotkey key).
    // ponytail: one character's set at a time (another character starts empty); a list per character if players ask
    static string LValue()
    {
        string saved = DaggerfallUnity.Settings.VitaQuickSpell;
        string prefix = Player.Name + "|";
        return !string.IsNullOrEmpty(saved) && saved.StartsWith(prefix) ? saved.Substring(prefix.Length) : null;
    }

    static string LItemKey()
    {
        string value = LValue();
        ulong uid;
        return value != null && value.StartsWith("M:") && ulong.TryParse(value.Substring(2), out uid) ? value : null;
    }

    static string LSpellName()
    {
        string value = LValue();
        return value != null && value != LItemKey() ? value : null;
    }

    static void SetLSpell(string value)
    {
        DaggerfallUnity.Settings.VitaQuickSpell = Player.Name + "|" + value;
        DaggerfallUnity.Settings.SaveSettings();
    }

    // A magic item's cast-when-used power, as DaggerfallUseMagicItemWindow uses it; a spell it readied then goes off at once, as spell
    // hotkeys do (caster-only spells are already applied by the item). Never equips or unequips the item.
    static void UseMagicItem(DaggerfallUnityItem item)
    {
        EntityEffectManager manager = GameManager.Instance.PlayerEffectManager;
        if (!manager || GameManager.Instance.PlayerSpellCasting.IsPlayingAnim)
            return;
        manager.DoItemEnchantmentPayloads(EnchantmentPayloadFlags.Used, item, Player.Items);
        EntityEffectBundle ready = manager.ReadySpell;
        if (ready != null && ready.CastByItem == item && !ready.Settings.NoCastingAnims)
            manager.CastReadySpell();
    }

    // Hotkey keys: "S:<spell name>", "W:<item UID>" (weapon or shield), "P:<potion recipe>" (any potion of that kind), "M:<item UID>"
    static string[] Hotkeys()
    {
        string saved = DaggerfallUnity.Settings.VitaHotkeys;
        string prefix = Player.Name + "|";
        string[] keys = new string[4];
        if (!string.IsNullOrEmpty(saved) && saved.StartsWith(prefix))
        {
            string[] parts = saved.Substring(prefix.Length).Split('|');
            for (int k = 0; k < 4 && k < parts.Length; k++)
                keys[k] = parts[k].Length > 0 ? parts[k] : null;
        }
        return keys;
    }

    static string HotkeyOf(Entry e)
    {
        if (e == null)
            return null;
        switch (e.kind)
        {
            case Kind.Spell:
                return e.spell.Name.IndexOf('|') < 0 ? "S:" + e.spell.Name : null;
            case Kind.Weapon:
                return "W:" + e.item.UID;
            case Kind.Item:
                return e.item.IsPotion ? "P:" + e.item.PotionRecipeKey : "M:" + e.item.UID;
            default:
                return null;
        }
    }

    // "1", "2 3" for the slice's hotkeys, or null
    static string HotkeyTag(Entry e)
    {
        string key = HotkeyOf(e);
        if (key == null)
            return null;
        string tag = null;
        string[] keys = Hotkeys();
        for (int k = 0; k < 4; k++)
            if (keys[k] == key)
                tag = (tag == null ? "" : tag + " ") + (k + 1);
        return tag;
    }

    // The same thing on the same slot again clears it
    void Bind(int slot, Entry e)
    {
        string key = HotkeyOf(e);
        if (key == null)
            return;
        string[] keys = Hotkeys();
        keys[slot] = keys[slot] == key ? null : key;
        DaggerfallUnity.Settings.VitaHotkeys = Player.Name + "|" + string.Join("|", Array.ConvertAll(keys, k => k ?? ""));
        DaggerfallUnity.Settings.SaveSettings();
        DaggerfallUI.Instance.PlayOneShot(SoundClips.ButtonClick);
        Refresh();
    }

    // As the spellbook's pick (lycanthropes cast for free), then cast at once (the spell otherwise waits for Cross). Touch spells with
    // no one in reach stay ready, as from the spellbook.
    static void CastNow(EffectBundleSettings spell)
    {
        EntityEffectManager manager = GameManager.Instance.PlayerEffectManager;
        if (!manager || GameManager.Instance.PlayerSpellCasting.IsPlayingAnim)
            return;
        if (!manager.SetReadySpell(new EntityEffectBundle(spell, GameManager.Instance.PlayerEntityBehaviour), spell.Tag == PlayerEntity.lycanthropySpellTag))
            return;
        if (spell.TargetType != TargetTypes.CasterOnly && !spell.NoCastingAnims)
            manager.CastReadySpell();       // caster-only and no-animation spells go off in the manager's own Update
    }

    static bool HasSpellbook()
    {
        if (Player.Items.Contains(ItemGroups.MiscItems, (int)MiscItems.Spellbook))
            return true;
        DaggerfallUI.AddHUDText(TextManager.Instance.GetLocalizedText("noSpellbook"));
        return false;
    }

    static EffectBundleSettings? FindSpell(string name)
    {
        EffectBundleSettings[] spells = Player.GetSpells();
        if (spells != null)
            foreach (EffectBundleSettings spell in spells)
                if (spell.Name == name)
                    return spell;
        return null;
    }

    /// <summary>Tap L: the L spell or magic item set in this menu, else the last spell cast (upstream's RecastSpell), cast at once.</summary>
    public static void CastLSpell()
    {
        EntityEffectManager manager = GameManager.Instance.PlayerEffectManager;
        if (!manager || GameManager.Instance.PlayerSpellCasting.IsPlayingAnim)
            return;
        string itemKey = LItemKey();
        if (itemKey != null)
        {
            DaggerfallUnityItem item = Player.Items.GetItem(ulong.Parse(itemKey.Substring(2)));
            if (item == null)
                DaggerfallUI.AddHUDText(Text("vitaQuickNotCarried"));
            else
                UseMagicItem(item);
            return;
        }
        string name = LSpellName();
        EffectBundleSettings? spell = name != null ? FindSpell(name) : null;
        if (spell == null && manager.LastSpell == null)
            return;
        if (!HasSpellbook())
            return;
        if (spell != null)
            CastNow(spell.Value);
        else
            CastNow(manager.LastSpell.Settings);
    }

    /// <summary>D-pad Left/Right tap (slots 0, 1) or hold (2, 3) in play</summary>
    public static void UseHotkey(int slot)
    {
        string key = Hotkeys()[slot];
        if (key == null)
        {
            DaggerfallUI.AddHUDText(Text("vitaQuickNoHotkey"));
            return;
        }
        string value = key.Substring(2);
        if (key[0] == 'S')
        {
            EffectBundleSettings? spell = FindSpell(value);
            if (spell == null)
                DaggerfallUI.AddHUDText(Text("vitaQuickNotCarried"));
            else if (HasSpellbook())
                CastNow(spell.Value);
            return;
        }
        DaggerfallUnityItem item = null;
        if (key[0] == 'P')
        {
            for (int i = 0; i < Player.Items.Count && item == null; i++)
            {
                DaggerfallUnityItem candidate = Player.Items.GetItem(i);
                if (candidate.IsPotion && candidate.PotionRecipeKey.ToString() == value)
                    item = candidate;
            }
        }
        else
        {
            ulong uid;
            if (ulong.TryParse(value, out uid))
                item = Player.Items.GetItem(uid);
        }
        if (item == null)
        {
            DaggerfallUI.AddHUDText(Text("vitaQuickNotCarried"));
            return;
        }
        if (key[0] == 'W')
            DrawWeapon(item);
        else if (item.IsPotion)
        {
            // As DaggerfallUseMagicItemWindow.MagicItemPicker_OnItemPicked
            GameManager.Instance.PlayerEffectManager.DrinkPotion(item);
            Player.Items.RemoveOne(item);
        }
        else
            UseMagicItem(item);
    }

    // Weapon hotkey: into the right hand if it isn't in a hand yet (shields where Daggerfall puts them), then out and in use
    static void DrawWeapon(DaggerfallUnityItem item)
    {
        WeaponManager weapons = GameManager.Instance.WeaponManager;
        if (!item.IsEquipped)
        {
            DaggerfallUnityItem right = Player.ItemEquipTable.GetItem(EquipSlots.RightHand);
            DaggerfallUnityItem left = Player.ItemEquipTable.GetItem(EquipSlots.LeftHand);
            int refusal = EquipItem(item, EquipSlots.RightHand);
            if (refusal > 0)
            {
                string line = RscLine(refusal, refusal == ItemBrokenTextId ? item : null);
                if (line != null)
                    DaggerfallUI.AddHUDText(line);
                return;
            }
            SetEquipDelayTime(right, left);
        }
        if (!item.IsShield && item.IsEquipped)
            weapons.UsingRightHand = item.EquipSlot != EquipSlots.LeftHand;
        if (weapons.Sheathed)
            weapons.VitaToggleSheath();
    }

    #endregion

    public override void Draw()
    {
        // The HUD, a dim over the whole screen, then the menu (DaggerfallPopupWindow clears the parent's colour every frame)
        if (PreviousWindow != null)
            PreviousWindow.Draw();
        ParentPanel.BackgroundColor = dimColor;
        ParentPanel.Draw();
    }
}
#endif
