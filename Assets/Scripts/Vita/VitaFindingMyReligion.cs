// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Finding My Religion (Cliffworms, 1.23, Android build) on the Vita, where .dfmod scripts can't load. Its temples and block
// come from VitaMods/religion.pak (VitaWorldPack); its statue sprites (archive 1230) are a palette TEXTURE file in VitaNative (flatpack.py). This file does what the
// mod's DLL did, read from its compiled code: clicking a deity statue shows its plaque on the HUD, and temple members may rest in the temple
// beds, which the welcome message mentions. The setting applies from the next start of the game, as the world packs do.

#if UNITY_PSP2
using System.Collections.Generic;
using UnityEngine;
using DaggerfallConnect.Arena2;
using DaggerfallWorkshop;
using DaggerfallWorkshop.Game;
using DaggerfallWorkshop.Game.Guilds;

public static class VitaFindingMyReligion
{
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Init()
    {
        if (!DaggerfallUnity.Settings.VitaFindingMyReligion)
            return;
        GuildManager.RegisterCustomGuild(FactionFile.GuildGroups.HolyOrder, typeof(TempleFMR));
        Statue(new[] { 10, 11 }, "Mara, the Mother Goddess", "'Live soberly and peacefully.", "Honor your parents, and preserve the peace",
            "and security of home and family.'");
        Statue(new[] { 3, 4 }, "Arkay, Birth and Death God", "'Honor the earth, its creatures, and the spirits, living and dead.",
            "Guard and tend the bounties of the mortal world, and do not", "profane the spirits of the dead.'");
        Statue(new[] { 18, 19, 20, 21, 22 }, "Zenithar, God of Commerce", "'Work hard, and you will be rewarded.",
            "Spend wisely, and you will be comfortable.", "Never steal, or you will be punished.'");
        Statue(new[] { 5 }, "Stendarr, God of Mercy", "'Be kind and generous to the people of Tamriel.",
            "Protect the weak, heal the sick, and give to the needy.'");
        Statue(new[] { 13, 14, 15, 16, 17 }, "Kynareth, Goddess of Air", "'Use Nature's gifts wisely.", "Respect her power, and fear her fury.'");
        Statue(new[] { 6, 7, 8 }, "Julianos, God of Logic", "'Know the truth. Observe the law.", "When in doubt, seek wisdom from the wise.'");
        Statue(new[] { 9 }, "Dibella, Goddess of Beauty", "'Open your heart to the noble secrets of art and love.", "Treasure the gifts of friendship.",
            "Seek joy and inspiration in the mysteries of love.'");
        Statue(new[] { 2 }, "Akatosh, God of Time", "'Serve and obey your Emperor. Study the Covenants.", "Worship the Eight, do your duty, and heed the",
            "commands of the saints and priests.'");
        Statue(new[] { 12 }, "Talos, hero-god of Mankind", "'Be strong for war..", "Be bold against enemies and evil,", "and defend the people of Tamriel.'");
    }

    // The mod registers under its Mod object; there is none on the Vita and no other mod competes for these flats (PlayerActivate only
    // reads the provider when a second registration comes in)
    static void Statue(int[] records, string deity, params string[] plaque)
    {
        PlayerActivate.CustomActivation show = hit =>
        {
            DaggerfallUI.AddHUDText("You see a statue of " + deity, 5);
            DaggerfallUI.AddHUDText("", 5);
            DaggerfallUI.AddHUDText("A plaque reads :", 5);
            foreach (string line in plaque)
                DaggerfallUI.AddHUDText(line, 5);
        };
        foreach (int record in records)
            PlayerActivate.RegisterCustomActivation(null, 1230, record, show, 3.2f);
    }

    public class TempleFMR : Temple
    {
        static readonly TextFile.Token newLine = TextFile.CreateFormatToken(TextFile.Formatting.JustifyCenter);

        public TempleFMR(Divines deity) : base(deity)
        {
        }

        public override bool CanRest()
        {
            return IsMember();
        }

        public override TextFile.Token[] TokensWelcome()
        {
            var tokens = new List<TextFile.Token>(base.TokensWelcome());
            tokens.Add(newLine);
            foreach (string line in new[] { "Please feel free to make use of the beds", "for your rest and comfort. Your well-being is",
                "important to us. Before I let you go, read the", "your deity's mantra on the statue. Recite it", "every morning and before resting." })
            {
                tokens.Add(TextFile.CreateTextToken(line));
                tokens.Add(newLine);
            }
            return tokens.ToArray();
        }
    }
}
#endif
