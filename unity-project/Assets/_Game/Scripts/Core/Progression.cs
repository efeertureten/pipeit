namespace SuUstasi.Core
{
    public readonly struct WinReward
    {
        public int Base { get; }
        public int Flawless { get; }
        public int Breather { get; }
        public int Total => Base + Flawless + Breather;

        public WinReward(int @base, int flawless, int breather)
        {
            Base = @base;
            Flawless = flawless;
            Breather = breather;
        }
    }

    /// <summary>Ranks, neighbourhood themes, breather cadence, booster unlocks and win rewards.</summary>
    public static class Progression
    {
        public static readonly string[] Themes = { "Banyo", "Mutfak", "Apartman Bodrumu", "Fabrika", "Şehir Kanalı", "Arıtma Tesisi" };

        /// <summary>Çırak (1–19), Kalfa (20–49), Usta (50–99), Başusta (100+).</summary>
        public static string RankFor(int level)
        {
            if (level >= 100) return "Başusta";
            if (level >= 50) return "Usta";
            if (level >= 20) return "Kalfa";
            return "Çırak";
        }

        /// <summary>Neighbourhood theme, cycling every 20 levels.</summary>
        public static string ThemeFor(int level) => Themes[((System.Math.Max(1, level) - 1) / 20) % Themes.Length];

        /// <summary>Every 5th level is a breather (fewer pipes, more initial exits, bonus coins).</summary>
        public static bool IsBreather(int level) => level % EconomyRules.BreatherEvery == 0;

        public static bool BoosterUnlocked(BoosterType booster, int level) => level >= BoosterRules.UnlockLevel(booster);

        /// <summary>20 coins + 10 flawless (0 mistakes) + 20 on breather levels.</summary>
        public static WinReward WinRewardFor(int level, int mistakes) =>
            new WinReward(EconomyRules.WinCoins, mistakes == 0 ? EconomyRules.FlawlessBonus : 0, IsBreather(level) ? EconomyRules.BreatherBonus : 0);
    }
}
