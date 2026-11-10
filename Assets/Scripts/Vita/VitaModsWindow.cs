// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Mods window for the Vita: the setup screen's "Mods" button opens it, as on PC.
// The Vita can't load .dfmod files, so it lists the PC mods built into the port, each with an on/off box. D-pad moves the cursor
// (VitaMenuNav): the row under it shows its details on the right, Cross toggles it, Circle goes back. Settings are saved on close.

#if UNITY_PSP2
using System;
using UnityEngine;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.UserInterface;
using DaggerfallWorkshop.Game.UserInterfaceWindows;

public class VitaModsWindow : DaggerfallPopupWindow
{
    class Entry
    {
        public string title, row, author, version, description;     // row: the list's name when the title is too long for it
        public string[] needs;      // rows (Name) this mod needs: on with it; turning one of them off turns this off (PC's dependencies)
        public string[] alsoOn;     // rows turned on with this one, not needed by it: turning them off leaves this on
        public string Name { get { return row ?? title; } }
        public Func<bool> get;
        public Action<bool> set;
    }

    static Entry[] Entries()
    {
        SettingsManager s = DaggerfallUnity.Settings;
        return new[] {
            new Entry { title = "Dynamic Skies", author = "BadLuckBurt, carademono", version = "2.3.4 (Pixel)",
                description = "Animated sky with clouds, stars and both moons with their phases. Uses about 10 MB of video memory.",
                get = () => s.VitaDynamicSkies, set = v => s.VitaDynamicSkies = v },
            new Entry { title = "Basic Roads", author = "Hazelnut", version = "1.3.1",
                description = "Roads and dirt tracks between towns, painted onto the terrain.",
                get = () => s.VitaBasicRoads, set = v => s.VitaBasicRoads = v },
            new Entry { title = "Low Poly Trees", author = "SquidKamer", version = "5",
                description = "3D trees near you, billboards farther away. About 31 MB of data; can cost FPS in thick woods.",
                get = () => s.VitaLowPolyTrees, set = v => s.VitaLowPolyTrees = v },
            new Entry { title = "Windmills of Daggerfall", author = "Kamer", version = "2.2",
                description = "Windmills with turning sails on farms (1283 of them); the nearest to Daggerfall is The Hawking Plantation.",
                get = () => s.VitaWindmills, set = v => s.VitaWindmills = v },
            new Entry { title = "Beautiful Cities of Daggerfall", row = "Beautiful Cities", author = "carademono", version = "0.5.0",
                description = "Redesigned cities: walls, markets, streets, furnished interiors. Needs Expanded Textures, RMB Resource Pack, DIEP, Desert Architecture, Windmills, Finding My Religion and Harvestable Crops (turned on with it), and turns on Old Masters for its wall paintings.",
                needs = new[] { "Expanded Textures", "RMB Resource Pack", "DIEP", "Desert Architecture", "Windmills of Daggerfall", "Finding My Religion", "Harvestable Crops" },
                alsoOn = new[] { "Old Masters" },
                get = () => s.VitaBeautifulCities, set = v => s.VitaBeautifulCities = v },
            new Entry { title = "Beautiful Villages of Daggerfall", row = "Beautiful Villages", author = "carademono", version = "1.4.2",
                description = "Redesigned villages, farms and hamlets, furnished interiors. Needs Expanded Textures, RMB Resource Pack, DIEP, Desert Architecture, Windmills, Finding My Religion and Harvestable Crops (turned on with it), and turns on Old Masters for its wall paintings.",
                needs = new[] { "Expanded Textures", "RMB Resource Pack", "DIEP", "Desert Architecture", "Windmills of Daggerfall", "Finding My Religion", "Harvestable Crops" },
                alsoOn = new[] { "Old Masters" },
                get = () => s.VitaBeautifulVillages, set = v => s.VitaBeautifulVillages = v },
            new Entry { title = "Finding My Religion", author = "Cliffworms", version = "1.23 (Android)",
                description = "Rebuilt temples with deity statues: click a statue to read its plaque. Temple members may rest in the temple beds. Deity statues by King of Worms. Needs Expanded Textures and RMB Resource Pack.",
                needs = new[] { "Expanded Textures", "RMB Resource Pack" },
                get = () => s.VitaFindingMyReligion, set = v => s.VitaFindingMyReligion = v },
            new Entry { title = "Fixed Desert Architecture", row = "Desert Architecture", author = "Cliffworms", version = "1.3.7",
                description = "Flat-roof buildings in desert towns, Redguard residents, redesigned shop and library interiors. Cities and Villages take priority where they change the same block. Needs Expanded Textures and RMB Resource Pack.",
                needs = new[] { "Expanded Textures", "RMB Resource Pack" },
                get = () => s.VitaFixedDesertArchitecture, set = v => s.VitaFixedDesertArchitecture = v },
            new Entry { title = "Fixed Dungeon Exteriors", row = "Dungeon Exteriors", author = "Cliffworms", version = "1.4.4",
                description = "1,750 dungeon entrances that fit the dungeon: castles for castles, graveyards for crypts, ruins, cairns, trees for harpy nests.",
                get = () => s.VitaFixedDungeonExteriors, set = v => s.VitaFixedDungeonExteriors = v },
            // Resource mods: only the town mods above place their pieces, but every built-in mod gets a switch
            new Entry { title = "Daggerfall Expanded Textures", row = "Expanded Textures", author = "Ninelan-Ruinar", version = "1.3.0",
                description = "Models, textures and flats the town mods build with: chimneys, banners, gargoyles, signs. Turning it off turns off Cities, Villages, Desert Architecture, Finding My Religion, World of Daggerfall and Biomes.",
                get = () => s.VitaExpandedTextures, set = v => s.VitaExpandedTextures = v },
            new Entry { title = "RMB Resource Pack", author = "Many creators (carademono)", version = "0.3.0",
                description = "Models and flats the town mods build with: crop fields, hills, market stalls, clutter. Turning it off turns off Cities, Villages, Desert Architecture and Finding My Religion.",
                get = () => s.VitaRmbResourcePack, set = v => s.VitaRmbResourcePack = v },
            new Entry { title = "DIEP - Rosy's Resources", row = "DIEP", author = "RosyTheRascal and team", version = "1.1",
                description = "By RosyTheRascal, PRAEst76, BenGamey974 and AlexanderSig. Furniture and decorations the Cities and Villages interiors use. Turning it off turns off Cities and Villages.",
                get = () => s.VitaDiep, set = v => s.VitaDiep = v },
            new Entry { title = "Old Masters of Tamriel - Wikimedia Commons Paintings", row = "Old Masters", author = "Daggerfall Unity Vita", version = "1.0",
                description = "Public-domain paintings from Wikimedia Commons on the walls of Cities and Villages interiors, used in place of New Paintings. Cities and Villages turn it on; turning it off leaves them on with bare walls.",
                get = () => s.VitaNewPaintings, set = v => s.VitaNewPaintings = v },
            new Entry { title = "DET Harvestable Crops", row = "Harvestable Crops", author = "TheLacus, Ninelan & carademono", version = "1.0.1 (look only)",
                description = "Crops in town and farm fields turn into snowy stalks in winter outside the deserts. Harvesting itself isn't ported.",
                get = () => s.VitaHarvestableCrops, set = v => s.VitaHarvestableCrops = v },
            new Entry { title = "Redguards of Hammerfell", row = "Redguards", author = "WilhelmBlack", version = "0.6",
                description = "Redguard looks for the townspeople of Sentinel and Daggerfall: 17 NPC sprites and 6 talk portraits.",
                get = () => s.VitaRedguards, set = v => s.VitaRedguards = v },
#if VITA_DRAGONRIDER
            new Entry { title = "Dragon Rider", author = "relentlessp0sitivity & SquidKamer", version = "1.1",
                description = "Hatch a dragon from an egg (Akatosh Chantry quest, or a rare find on alchemists' shelves) and fly it. On the dragon: left stick speed/turn, right stick steers, hold Square hover/flight, Triangle up, Cross down, hold R boost, L fire, hold Circle to dismount.",
                get = () => s.VitaDragonRider, set = v => s.VitaDragonRider = v },
#endif
#if VITA_TRAVELOPTIONS
            new Entry { title = "Travel Options", author = "Hazelnut", version = "1.11",
                description = "Travel in real time with sped-up time instead of fast travel (\"Cautiously\" on the travel map), follow roads (Start menu: Follow Road), ship travel from ports. The speed drops to 1x while new land is still loading.",
                get = () => s.VitaTravelOptions, set = v => s.VitaTravelOptions = v },
#endif
            new Entry { title = "World of Daggerfall", row = "World of Daggerfall", author = "World of Daggerfall Team", version = "0.4.0 (no rocks yet)",
                description = "Thousands of new places in the wilderness: farms, ruins, bandit camps and forts, guard towers, docks and bridges. Its rocks and mountains are not in yet. By Kamer, carademono, Cliffworms and JayH, with Location Loader by UncannyValley and KABoissonneault.",
                needs = new[] { "Expanded Textures" },
                get = () => s.VitaWorldOfDaggerfall, set = v => s.VitaWorldOfDaggerfall = v },
            new Entry { title = "World of Daggerfall - Terrain", row = "WoD Terrain", author = "monobelisk, Freak2121, carademono & Ninelan", version = "1.5.0",
                description = "New ground shapes for the whole Iliac Bay: hills and mountains up to 5000 metres, flattened ground around towns, lowered ports and smoothed roads. New ground can take a few seconds longer to appear while you travel.",
                get = () => s.VitaWodTerrain, set = v => s.VitaWodTerrain = v },
            new Entry { title = "World of Daggerfall - Biomes", row = "Biomes", author = "carademono & Kab the Bird Ranger", version = "0.4.0",
                description = "New ground for Hammerfell's mountains, the deep desert, the subtropics and the haunted woods.",
                needs = new[] { "Expanded Textures" },
                get = () => s.VitaWorldOfDaggerfallBiomes, set = v => s.VitaWorldOfDaggerfallBiomes = v },
            new Entry { title = "Snowless - Swamp, Jungle, and Woodlands", row = "Snowless", author = "Daggerfall Unity Vita", version = "1.0",
                description = "Swamps, jungles and woodlands keep their summer ground, trees and buildings in winter, and it rains there instead of snowing.",
                get = () => s.VitaSnowlessSwamp, set = v => s.VitaSnowlessSwamp = v },
            new Entry { title = "Distant Terrain of the World of Daggerfall", row = "Distant Terrain", author = "MaoDeVaca, from Nystul's Distant Terrain", version = "3.69 (basic)",
                description = "The whole of the Iliac Bay drawn out to the horizon past the near terrain, as one light mesh made for the Vita (the mod's basic look).",
                get = () => s.VitaDistantTerrain, set = v => s.VitaDistantTerrain = v },
            new Entry { title = "Vanilla Friendly Loading Screens", row = "Loading Screens", author = "WilhelmBlack", version = "",
                description = "Original Daggerfall art on the loading screens. Off: the plain title text.",
                get = () => s.VitaLoadScreenArt, set = v => s.VitaLoadScreenArt = v },
        };
    }

    Entry[] entries;
    Button[] rows;
    TextLabel title, author, version, status, description;
    int shown = -1;

    public VitaModsWindow(IUserInterfaceManager uiManager, IUserInterfaceWindow previous = null)
        : base(uiManager, previous)
    {
    }

    protected override void Setup()
    {
        // First row: every mod on or off at once
        Entry[] mods = Entries();
        Entry all = new Entry { title = "Enable all mods", version = "",
            description = "Turns every mod in this list on at once. Untick it to turn them all off again.",
            get = () => Array.TrueForAll(mods, m => m.get()) };
        all.set = v => { foreach (Entry m in mods) Set(m, v); };
        entries = new Entry[mods.Length + 1];
        entries[0] = all;
        mods.CopyTo(entries, 1);
        ParentPanel.BackgroundColor = Color.black;
        Panel frame = Box(new Vector2(6, 6), new Vector2(308, 188), DaggerfallUI.DaggerfallDefaultTextColor);
        frame.BackgroundColor = new Color(0.11f, 0.09f, 0.08f);

        Label("Mods: PC mods built into the Vita port", new Vector2(0, 6), 0, true);

        Box(new Vector2(14, 16), new Vector2(140, 166), Color.gray);
        rows = new Button[entries.Length];
        float rowHeight = Mathf.Min(8f, 162f / entries.Length);   // rows fill the list box (23 rows: 7.04 each)
        for (int i = 0; i < entries.Length; i++)
        {
            int index = i;
            Button row = DaggerfallUI.AddButton(new Rect(16, 18 + i * rowHeight, 136, rowHeight), NativePanel);
            row.Label.HorizontalAlignment = HorizontalAlignment.Left;
            row.Label.TextColor = DaggerfallUI.DaggerfallDefaultTextColor;
            row.OnMouseEnter += sender => Show(index);
            row.OnMouseClick += (sender, position) => { Toggle(index); Show(index); };
            rows[i] = row;
        }
        Label("Cross: on/off   Circle: back", new Vector2(14, 186), 140, true).TextColor = Color.gray;

        Box(new Vector2(160, 16), new Vector2(146, 166), Color.gray);
        title = Label("", new Vector2(165, 25), 0, false);
        author = Label("", new Vector2(165, 38), 0, false);
        version = Label("", new Vector2(165, 47), 0, false);
        status = Label("", new Vector2(165, 56), 0, false);
        author.TextColor = version.TextColor = status.TextColor = Color.white;
        description = Label("", new Vector2(165, 70), 0, false);
        description.TextColor = Color.white;
        foreach (TextLabel label in new[] { title, author, description })   // long titles and author lists ran past the box
        {
            label.WrapText = true;
            label.WrapWords = true;
            label.MaxWidth = 136;
        }

        Show(0);
    }

    // Like PC DFU's dependencies (each mod's dfmod.json, REQUIRED entries): a mod comes on with what it needs; a needed mod going off
    // takes its users with it. Chains follow through (Cities -> Desert Architecture -> Expanded Textures).
    void Toggle(int index)
    {
        Entry e = entries[index];
        Set(e, !e.get());
    }

    void Set(Entry e, bool on)
    {
        if (e.get() == on && on)
            return;
        e.set(on);
        foreach (Entry other in entries)
        {
            if (on && e.needs != null && Array.IndexOf(e.needs, other.Name) >= 0 && !other.get())
                Set(other, true);
            if (on && e.alsoOn != null && Array.IndexOf(e.alsoOn, other.Name) >= 0 && !other.get())
                Set(other, true);
            if (!on && other.get() && other.needs != null && Array.IndexOf(other.needs, e.Name) >= 0)
                Set(other, false);
        }
    }

    void Show(int index)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            rows[i].Label.Text = (entries[i].get() ? "[X] " : "[  ] ") + (entries[i].row ?? entries[i].title);
            rows[i].BackgroundColor = i == index ? new Color(0.27f, 0.23f, 0.08f) : Color.clear;
        }
        // TextLabel draws "\n" as an unknown glyph: one label per line
        Entry e = entries[index];
        status.Text = "Status: " + (e.get() ? "On" : "Off");
        if (index == shown)
            return;
        shown = index;
        title.Text = e.title;
        author.Text = e.author != null ? "Author: " + e.author : "";
        version.Text = e.version != "" ? "Version: " + e.version : "";
        description.Text = e.description;
        // Stacked by height: the title and author may take two lines; empty lines take no space
        float y = 25;
        title.Position = new Vector2(165, y);
        y += title.Size.y + 6;
        author.Position = new Vector2(165, y);
        if (e.author != null)
            y += author.Size.y + 2;
        version.Position = new Vector2(165, y);
        if (e.version != "")
            y += 9;
        status.Position = new Vector2(165, y);
        description.Position = new Vector2(165, y + 14);
    }

    public override void OnPop()
    {
        base.OnPop();
        DaggerfallUnity.Settings.SaveSettings();
    }

    Panel Box(Vector2 position, Vector2 size, Color outline)
    {
        Panel box = new Panel();
        box.Position = position;
        box.Size = size;
        box.Outline.Enabled = true;
        box.Outline.Color = outline;
        NativePanel.Components.Add(box);
        return box;
    }

    TextLabel Label(string text, Vector2 position, float width, bool centred)
    {
        TextLabel label = DaggerfallUI.AddTextLabel(DaggerfallUI.DefaultFont, position, text, NativePanel);
        label.TextColor = DaggerfallUI.DaggerfallDefaultTextColor;
        label.ShadowPosition = Vector2.zero;
        if (centred)
        {
            if (width > 0)
            {
                label.HorizontalAlignment = HorizontalAlignment.None;
                label.Position = new Vector2(position.x + (width - label.TextWidth) / 2, position.y);
            }
            else
                label.HorizontalAlignment = HorizontalAlignment.Center;
        }
        return label;
    }
}
#endif
