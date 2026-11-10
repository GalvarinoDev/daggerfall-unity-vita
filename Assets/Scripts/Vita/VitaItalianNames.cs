// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Italian shop, tavern and bank names with the right articles and adjective endings. Port of the Italian translation's
// "tradita2" mod by XSpettro101X (Nexus 1182), which replaces FormulaHelper.GenerateBuildingName on PC. The Vita can't load
// .dfmod files, so the same logic is built in. It runs only when the Italian word tables (Text/ItalianNames.txt and
// Text/ItalianAdjectives.txt) are in the build, and only the Italian VPK has them.
// Guild halls, temples and palaces return null, since the mod names them the same way DFU does. DFU's own code names them,
// and city walls too, which the mod left blank.

#if UNITY_PSP2
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using DaggerfallConnect;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Entity;
using DaggerfallWorkshop.Game.Utility;
using DaggerfallWorkshop.Utility;

namespace DaggerfallWorkshop
{
    public static class VitaItalianNames
    {
        enum Gn { MS, FS, MP, FP }
        enum Art { Normal, SpecialMasculine, Elided, Omitted }

        class Name
        {
            public string text; public Gn gn; public Art art;
            public Name(string text, Gn gn, Art art) { this.text = text; this.gn = gn; this.art = art; }
        }

        class Adjective
        {
            public string[] forms; public bool before;      // forms indexed by Gn
            public Adjective(string[] forms, bool before) { this.forms = forms; this.before = before; }
        }

        static bool loaded;
        static Dictionary<string, Name> names;
        static Dictionary<string, Adjective> adjectives;

        static string TextFile(string file)
        {
            string path = Path.Combine(Path.Combine(Application.streamingAssetsPath, "Text"), file);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        // True only in the Italian build; the tables are read once
        public static bool Active
        {
            get
            {
                if (!loaded)
                {
                    loaded = true;
                    string n = TextFile("ItalianNames.txt"), a = TextFile("ItalianAdjectives.txt");
                    if (n != null && a != null)
                    {
                        names = new Dictionary<string, Name>();
                        adjectives = new Dictionary<string, Adjective>();
                        Table t = new Table(n);
                        for (int i = 0; i < t.RowCount; i++)
                        {
                            string[] row = t.GetRow(i);
                            if (row.Length < 2 || string.IsNullOrEmpty(row[0]) || row[0].StartsWith("-") || row[1].Length < 2 || names.ContainsKey(row[0]))
                                continue;
                            Gn gn;
                            switch (row[1].Substring(0, 2))
                            {
                                case "MS": gn = Gn.MS; break;
                                case "FS": gn = Gn.FS; break;
                                case "MP": gn = Gn.MP; break;
                                case "FP": gn = Gn.FP; break;
                                default: continue;
                            }
                            string flags = row[1].Substring(2);
                            Art art = flags.Contains("O") ? Art.Omitted : flags.Contains("E") ? Art.Elided : flags.Contains("L") ? Art.SpecialMasculine : Art.Normal;
                            names.Add(row[0], new Name(row[0], gn, art));
                        }
                        t = new Table(a);
                        for (int i = 0; i < t.RowCount; i++)
                        {
                            string[] row = t.GetRow(i);
                            if (row.Length < 5 || string.IsNullOrEmpty(row[0]) || row[0].StartsWith("-") || adjectives.ContainsKey(row[0]))
                                continue;
                            adjectives.Add(row[0], new Adjective(new[] { row[0], row[1], row[2], row[3] }, row[4] == "true"));
                        }
                    }
                }
                return names != null;
            }
        }

        static Name MaybeName(string s) { Name n; return names.TryGetValue(s, out n) ? n : null; }
        static Adjective MaybeAdjective(string s) { Adjective a; return adjectives.TryGetValue(s, out a) ? a : null; }
        static Name LookupName(string s) { Name n = MaybeName(s); return n ?? new Name("<nome mancante: " + s + ">", Gn.MS, Art.Normal); }
        static Adjective LookupAdjective(string s)
        {
            Adjective a = MaybeAdjective(s);
            string m = "<aggettivo mancante: " + s + ">";
            return a ?? new Adjective(new[] { m, m, m, m }, false);
        }

        static string Article(Name n)
        {
            switch (n.gn)
            {
                case Gn.MS: return n.art == Art.Normal ? "il " : n.art == Art.SpecialMasculine ? "lo " : n.art == Art.Elided ? "l'" : "";
                case Gn.FS: return n.art == Art.Elided ? "l'" : n.art == Art.Omitted ? "" : "la ";
                case Gn.MP: return n.art == Art.Normal ? "i " : n.art == Art.Omitted ? "" : "gli ";
                default: return n.art == Art.Omitted ? "" : "le ";
            }
        }

        static string Di(Name n)
        {
            switch (n.gn)
            {
                case Gn.MS: return n.art == Art.Normal ? "del " : n.art == Art.SpecialMasculine ? "dello " : n.art == Art.Elided ? "dell'" : "di ";
                case Gn.FS: return n.art == Art.Elided ? "dell'" : n.art == Art.Omitted ? "di " : "della ";
                case Gn.MP: return n.art == Art.Normal ? "dei " : n.art == Art.Omitted ? "di " : "degli ";
                default: return n.art == Art.Omitted ? "di " : "delle ";
            }
        }

        static string WithArticle(Name n) { return Article(n) + n.text; }
        static string WithArticle(string s) { return WithArticle(LookupName(s)); }
        static string WithAdjective(Adjective a, Name n) { string f = a.forms[(int)n.gn]; return a.before ? f + " " + n.text : n.text + " " + f; }
        static string WithArticleAndAdjective(Adjective a, Name n)
        {
            string f = a.forms[(int)n.gn];
            return a.before ? Article(new Name(f, n.gn, Art.Normal)) + f + " " + n.text : Article(n) + n.text + " " + f;
        }
        static string MaybeArticle(string pattern, string name) { return pattern.Length != 0 ? WithArticle(name) : LookupName(name).text; }

        // Same signature as FormulaHelper.GenerateBuildingName; null = not an Italian-named type
        public static string Generate(int seed, DFLocation.BuildingTypes type, int factionID, string locationName, string regionName)
        {
            DFRandom.srand(seed);
            switch (type)
            {
                case DFLocation.BuildingTypes.HouseForSale: return TextManager.Instance.GetLocalizedText("houseForSale");
                case DFLocation.BuildingTypes.Tavern: return Tavern(locationName);
                case DFLocation.BuildingTypes.GeneralStore: return Store("GeneralStoresB", locationName);
                case DFLocation.BuildingTypes.WeaponSmith: return Store("WeaponStoresB", locationName);
                case DFLocation.BuildingTypes.Armorer: return Store("ArmorStoresB", locationName);
                case DFLocation.BuildingTypes.Bookseller: return Store("BookStoresB", locationName);
                case DFLocation.BuildingTypes.ClothingStore: return Store("ClothingStoresB", locationName);
                case DFLocation.BuildingTypes.Alchemist: return Store("AlchemyStoresB", locationName);
                case DFLocation.BuildingTypes.GemStore: return Store("GemStoresB", locationName);
                case DFLocation.BuildingTypes.PawnShop: return Store("PawnStoresB", locationName);
                case DFLocation.BuildingTypes.FurnitureStore: return Store("FurnitureStoresB", locationName);
                case DFLocation.BuildingTypes.Library: return Store("LibraryStoresB", locationName);
                case DFLocation.BuildingTypes.Bank:
                    return PostProcess(Expand(TextManager.Instance.GetLocalizedText("theBankOf"), locationName) + " " + Di(LookupName(regionName)) + LookupName(regionName).text);
                default: return null;
            }
        }

        static string Random(string key)
        {
            string[] list = TextManager.Instance.GetLocalizedTextList(key);
            return list[DFRandom.random_range(0, list.Length)];
        }

        static string Tavern(string locationName)
        {
            string b = Random("TavernsB");
            string a = Random("TavernsA");
            Match m = Regex.Match(a, "^(il |lo |la )?(.*) e (il |lo |la )?<>$");
            if (m.Success)
                return PostProcess(MaybeArticle(m.Groups[1].Value, m.Groups[2].Value) + " e " + MaybeArticle(m.Groups[3].Value, b));
            m = Regex.Match(a, "^(il |lo |la )?<> di (il |lo |la )?(.*)$");
            if (m.Success)
                return PostProcess(MaybeArticle(m.Groups[1].Value, b) + " di " + MaybeArticle(m.Groups[2].Value, m.Groups[3].Value));
            m = Regex.Match(a, "^(il |lo |la )?<> (.*)$");
            if (m.Success && MaybeAdjective(m.Groups[2].Value) != null)
            {
                Adjective adj = LookupAdjective(m.Groups[2].Value);
                Name n = LookupName(b);
                return PostProcess(Expand(m.Groups[1].Value.Length != 0 ? WithArticleAndAdjective(adj, n) : WithAdjective(adj, n), locationName));
            }
            if (a.Contains("<>"))
                return PostProcess(Expand(a.Replace("<>", LookupName(b).text), locationName));
            return PostProcess(Expand(a, locationName));
        }

        static string Store(string bKey, string locationName)
        {
            string b = Random(bKey);
            string a = Random("StoresA");
            string s;
            Adjective adj;
            Name n;
            Match m;
            if ((m = Regex.Match(a, "^(.*) <> di %cn$")).Success && (adj = MaybeAdjective(m.Groups[1].Value)) != null)
                s = WithAdjective(adj, LookupName(b)) + " di %cn";
            else if (a == "<> di %cn")
                s = LookupName(b).text + " di %cn";
            else if ((m = Regex.Match(a, "^(il |lo |la )<> (.*)$")).Success && (adj = MaybeAdjective(m.Groups[2].Value)) != null)
                s = WithArticleAndAdjective(adj, LookupName(b));
            else if ((m = Regex.Match(a, "^<> (.*)$")).Success && (adj = MaybeAdjective(m.Groups[1].Value)) != null)
                s = WithAdjective(adj, LookupName(b));
            else if ((m = Regex.Match(a, "^<> (.*) di %ef$")).Success && (adj = MaybeAdjective(m.Groups[1].Value)) != null)
                s = WithAdjective(adj, LookupName(b)) + " di %ef";
            else if ((m = Regex.Match(a, "^(il |lo |la )<> di (il |lo |la )(.*)$")).Success && (n = MaybeName(m.Groups[3].Value)) != null)
                s = WithArticle(b) + " di " + WithArticle(n);
            else if (a == "il <> di %rt" || a == "lo <> di %rt" || a == "la <> di %rt")
                s = WithArticle(b) + " di %rt";
            else if (a == "<> di %ef")
                s = LookupName(b).text + " di %ef";
            else if ((m = Regex.Match(a, "^<> di (.*) %ef$")).Success && (n = MaybeName(m.Groups[1].Value)) != null)
                s = LookupName(b).text + " di " + n.text + " %ef";
            else if ((m = Regex.Match(a, "^<> di (il |lo |la )(.*) %ef$")).Success && (n = MaybeName(m.Groups[2].Value)) != null)
                s = LookupName(b).text + " di " + WithArticle(n) + " %ef";
            else
                s = a.Contains("<>") ? a.Replace("<>", LookupName(b).text) : a;
            return PostProcess(Expand(s, locationName));
        }

        // di/a + article contractions, then a capital first letter
        static readonly string[,] contractions = {
            { @"\bdi il\b", "del" }, { @"\bdi lo\b", "dello" }, { @"\bdi la\b", "della" }, { @"\bdi i\b", "dei" }, { @"\bdi gli\b", "degli" },
            { @"\bdi le\b", "delle" }, { @"\bdi l'", "dell'" }, { @"\ba il\b", "al" }, { @"\ba lo\b", "allo" }, { @"\ba la\b", "alla" },
            { @"\ba i\b", "ai" }, { @"\ba gli\b", "agli" }, { @"\ba le\b", "alle" }, { @"\ba l'", "all'" },
        };

        static string PostProcess(string v)
        {
            for (int i = 0; i < contractions.GetLength(0); i++)
                v = Regex.Replace(v, contractions[i, 0], contractions[i, 1]);
            return string.IsNullOrEmpty(v) ? v : char.ToUpperInvariant(v[0]) + v.Substring(1);
        }

        static string Expand(string name, string locationName)
        {
            name = name.Replace("%cn", locationName);
            if (name.Contains("%ef"))
            {
                DFRandom.rand();     // classic burns one roll before %ef, as FormulaHelper does
                var bank = (NameHelper.BankTypes)MapsFile.RegionRaces[GameManager.Instance.PlayerGPS.CurrentRegionIndex];
                name = name.Replace("%ef", DaggerfallUnity.Instance.NameHelper.FirstName(bank, Genders.Male));
            }
            if (name.Contains("%rt"))
            {
                string title = MacroHelper.RegentTitle(null);
                title = string.IsNullOrEmpty(title) ? title : char.ToUpperInvariant(title[0]) + title.Substring(1);
                name = name.Replace("%rt", WithArticle(title));
            }
            return name;
        }
    }
}
#endif
