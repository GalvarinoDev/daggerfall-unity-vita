// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// HUD style box (hold Select): Classic HUD, Minimal, No HUD, each with a small picture (baked on the PC by vita/tools/hudstyles.py into
// StreamingAssets/HudStyles/hudstyles.565; without it the cells show names only). D-pad Left/Right or a stick sideways picks, releasing
// Select applies it (DaggerfallHUD.VitaHudMode). Looks like the quick menu: inventory stone, classic font.

#if UNITY_PSP2
using System.IO;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

public class VitaHudStyleWindow : DaggerfallPopupWindow
{
    static readonly int[] modes = { 1, 2, 0 };     // cells left to right, as DaggerfallHUD's modes
    static readonly string[] names = { "vitaHudClassic", "vitaHudMinimal", "vitaHudNone" };
    internal static readonly Color32 selectedTint = new Color32(214, 200, 150, 255);     // also baked into vitaui.pak
    static readonly Color selectedText = new Color32(255, 90, 60, 255);
    const float stickPick = 0.5f;

    readonly DaggerfallHUD hud;
    int selected;
    float stickX;
    readonly Panel[] cells = new Panel[3];
    readonly TextLabel[] labels = new TextLabel[3];
    TextLabel title;
    // Owned by this window, freed on close
    Texture2D boxTexture, cellTexture, cellSelectedTexture, barTexture;
    readonly Texture2D[] thumbnails = new Texture2D[3];

    public VitaHudStyleWindow(IUserInterfaceManager uiManager, DaggerfallHUD hud)
        : base(uiManager, hud)
    {
        this.hud = hud;
        PauseWhileOpen = true;
    }

    static string Text(string key) { return TextManager.Instance.GetLocalizedText(key); }

    protected override void Setup()
    {
        boxTexture = VitaQuickMenuWindow.ClassicStone(240, 86, 0.85f, new Color32(), 0);
        cellTexture = VitaQuickMenuWindow.ClassicStone(68, 60, 0.65f, new Color32(), 0);
        cellSelectedTexture = VitaQuickMenuWindow.ClassicStone(68, 60, 0.65f, selectedTint, 0.5f);
        barTexture = VitaQuickMenuWindow.ClassicStone(312, 13, 0.8f, new Color32(), 0);
        LoadThumbnails();

        AddPanel(new Vector2(40, 52), new Vector2(240, 86), boxTexture, NativePanel);
        title = Centre(AddLabel(Text("vitaHudStyle"), 1f, NativePanel), 160, 61);
        selected = System.Array.IndexOf(modes, hud.VitaHudMode);
        if (selected < 0)
            selected = 0;
        for (int i = 0; i < 3; i++)
        {
            float x = 52 + i * 74;
            cells[i] = AddPanel(new Vector2(x, 70), new Vector2(68, 60), cellTexture, NativePanel);
            if (thumbnails[i] != null)
            {
                Panel thumb = AddPanel(new Vector2(x + 4, 74), new Vector2(60, 34), thumbnails[i], NativePanel);
                thumb.Outline.Enabled = true;
                thumb.Outline.Color = new Color32(30, 26, 20, 255);
            }
            labels[i] = AddLabel(Text(names[i]), 0.75f, NativePanel);
            Centre(labels[i], x + 34, 121);
        }
        AddPanel(new Vector2(4, 185), new Vector2(312, 13), barTexture, NativePanel);     // the quick menu's bar position, for its glyph hint
        VitaQuickMenuWindow.MakeGlyphs();
        var hintItems = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<VitaQuickMenuWindow.Glyph, string>>();
        hintItems.Add(new System.Collections.Generic.KeyValuePair<VitaQuickMenuWindow.Glyph, string>(VitaQuickMenuWindow.Glyph.DPad, ""));
        hintItems.Add(new System.Collections.Generic.KeyValuePair<VitaQuickMenuWindow.Glyph, string>(VitaQuickMenuWindow.Glyph.StickL, ""));
        hintItems.Add(new System.Collections.Generic.KeyValuePair<VitaQuickMenuWindow.Glyph, string>(VitaQuickMenuWindow.Glyph.StickR, Text("vitaQuickPick")));
        hintItems.Add(new System.Collections.Generic.KeyValuePair<VitaQuickMenuWindow.Glyph, string>(VitaQuickMenuWindow.NoGlyph, Text("vitaHudRelease")));
        VitaQuickMenuWindow.GlyphHint(hintItems, NativePanel, true);
        ShowSelection();
    }

    void LoadThumbnails()
    {
        try
        {
            byte[] data = File.ReadAllBytes(Path.Combine(Application.streamingAssetsPath, "HudStyles/hudstyles.565"));
            int w = data[4] | data[5] << 8, h = data[6] | data[7] << 8, n = data[8] | data[9] << 8, size = w * h * 2;
            if (data[0] != 'H' || data[1] != 'U' || data[2] != 'D' || data[3] != '1' || n < 3 || data.Length < 10 + 3 * size)
                return;
            for (int i = 0; i < 3; i++)
            {
                byte[] raw = new byte[size];
                System.Buffer.BlockCopy(data, 10 + i * size, raw, 0, size);
                Texture2D t = new Texture2D(w, h, TextureFormat.RGB565, false);
                t.filterMode = FilterMode.Bilinear;
                t.LoadRawTextureData(raw);
                t.Apply(false, true);
                thumbnails[i] = t;
            }
        }
        catch (System.Exception e)
        {
            Debug.Log("VitaHudStyleWindow: no thumbnails (" + e.Message + ")");
        }
    }

    static Panel AddPanel(Vector2 position, Vector2 size, Texture2D texture, Panel parent)
    {
        Panel p = new Panel { Position = position, Size = size, BackgroundTexture = texture, BackgroundTextureLayout = BackgroundLayout.StretchToFill };
        parent.Components.Add(p);
        return p;
    }

    static TextLabel AddLabel(string text, float scale, Panel parent)
    {
        TextLabel label = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, Vector2.zero, text, parent);
        label.TextScale = scale;
        label.TextColor = DaggerfallUI.DaggerfallDefaultTextColor;
        return label;
    }

    static TextLabel Centre(TextLabel label, float x, float y)
    {
        label.Position = new Vector2(Mathf.Round(x - label.TextWidth / 2f), y);
        return label;
    }

    void ShowSelection()
    {
        for (int i = 0; i < 3; i++)
        {
            cells[i].BackgroundTexture = i == selected ? cellSelectedTexture : cellTexture;
            labels[i].TextColor = i == selected ? selectedText : DaggerfallUI.DaggerfallDefaultTextColor;
        }
    }

    public override void Update()
    {
        base.Update();
        // Chinese labels grow to their minimum scale on their first draw (TextLabel), after Setup centred them: centre again
        Centre(title, 160, 61);
        for (int i = 0; i < 3; i++)
            Centre(labels[i], 52 + i * 74 + 34, 121);
        if (uiManager.TopWindow != this)
            return;
        if (!Input.GetKey(KeyCode.JoystickButton6) && !VitaQuickMenuWindow.TestHold)
        {
            hud.VitaHudMode = modes[selected];
            CloseWindow();
            return;
        }
        // D-pad Left/Right, or either stick pushed sideways (one step per push)
        int step = Input.GetKeyDown(KeyCode.JoystickButton11) ? -1 : Input.GetKeyDown(KeyCode.JoystickButton9) ? 1 : 0;
        float x = Input.GetAxisRaw("Axis1");
        if (Mathf.Abs(Input.GetAxisRaw("Axis4")) > Mathf.Abs(x))
            x = Input.GetAxisRaw("Axis4");
        if (step == 0 && Mathf.Abs(x) > stickPick && Mathf.Abs(stickX) <= stickPick)
            step = x > 0 ? 1 : -1;
        stickX = x;
        if (step != 0)
        {
            selected = Mathf.Clamp(selected + step, 0, 2);
            ShowSelection();
        }
    }

    public override void OnPush()
    {
        base.OnPush();
        InputManager.Instance.VitaQuickMenuOpen = true;
        GameManager.Instance.PlayerMouseLook.ForceHideCursor(true);
    }

    public override void OnPop()
    {
        base.OnPop();
        InputManager.Instance.VitaQuickMenuOpen = false;
        GameManager.Instance.PlayerMouseLook.ForceHideCursor(false);
        Object.Destroy(boxTexture);
        Object.Destroy(cellTexture);
        Object.Destroy(cellSelectedTexture);
        Object.Destroy(barTexture);
        foreach (Texture2D t in thumbnails)
            if (t != null)
                Object.Destroy(t);
    }
}
#endif
