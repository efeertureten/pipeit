using System;

namespace SuUstasi.Core
{
    /// <summary>GDD colour palette (hex). Runtime converts with PaletteColors.ToColor.</summary>
    public static class Palette
    {
        public const string Water = "#36A2FF";
        public const string Yellow = "#F2B632";
        public const string Orange = "#E8743B";
        public const string Green = "#5BBE7A";
        public const string Dry = "#8D99AE";
        public const string Rusty = "#A5673F";
        public const string Frozen = "#BDE9FF";
        public const string Error = "#E5484D";
        public const string Background = "#FFF4E0";
        public const string Ink = "#4A2E22";
        public const string Brass = "#C9A13B";

        /// <summary>Water pipes cycle yellow/orange/green; green is reserved for leaking pipes when a level has any.</summary>
        public static string WaterColorFor(int pipeIndex, bool levelHasLeaking)
        {
            if (levelHasLeaking) return pipeIndex % 2 == 0 ? Yellow : Orange;
            switch (pipeIndex % 3)
            {
                case 0: return Yellow;
                case 1: return Orange;
                default: return Green;
            }
        }

        public static string ColorFor(Pipe pipe, bool levelHasLeaking)
        {
            switch (pipe.Type)
            {
                case PipeType.Dry: return Dry;
                case PipeType.Rusty: return pipe.RustIntact ? Rusty : WaterColorFor(pipe.Index, levelHasLeaking);
                case PipeType.Frozen: return pipe.IceCount > 0 ? Frozen : WaterColorFor(pipe.Index, levelHasLeaking);
                case PipeType.Leaking: return Green;
                case PipeType.RotaryValve: return Brass;
                default: return WaterColorFor(pipe.Index, levelHasLeaking);
            }
        }
    }

    public static class GameTiming
    {
        /// <summary>Visual slide-out. Logic frees the cells immediately (non-blocking input).</summary>
        public const float ExitDuration = 0.6f;
        public const float ValveAutoExitDelay = 0.5f;
        public const float XRayDuration = 3.0f;
        public const float ShakeDuration = 0.35f;
        public const float FlashDuration = 0.55f;
    }

    public static class GameRules
    {
        public const int DefaultLeaks = 3;
        public const int MaxLeaks = 4;
        /// <summary>Levels 1..FtueLevels never cost leak lives and never show interstitials.</summary>
        public const int FtueLevels = 3;
        public const int DefaultLeakTimer = 6;
    }

    public enum BoosterType
    {
        XRay,
        Hammer,
        Patch,
    }

    public static class BoosterRules
    {
        public static int UnlockLevel(BoosterType b) => b == BoosterType.XRay ? 5 : b == BoosterType.Hammer ? 10 : 15;
        public static int CoinPrice(BoosterType b) => b == BoosterType.XRay ? 60 : b == BoosterType.Hammer ? 120 : 90;
        public static int StartCount(BoosterType b) => b == BoosterType.XRay ? 2 : 1;
        public static string Id(BoosterType b) => b == BoosterType.XRay ? "xray" : b == BoosterType.Hammer ? "hammer" : "patch";
        public static readonly BoosterType[] All = { BoosterType.XRay, BoosterType.Hammer, BoosterType.Patch };
    }

    public static class EconomyRules
    {
        public const int WinCoins = 20;
        public const int FlawlessBonus = 10;
        public const int BreatherBonus = 20;
        public const int BreatherEvery = 5;
        public const int StartCoins = 100;
    }

    public static class AdRules
    {
        public const int NoInterstitialUpToLevel = 3;
        public const double CooldownSeconds = 60;
        public const int EveryNWins = 2;
    }

    /// <summary>Mirrors JavaScript's Math.round (half rounds toward +∞) so C# and JS metrics agree exactly.</summary>
    public static class JsMath
    {
        public static double Round(double v) => Math.Floor(v + 0.5);
        public static double Round1(double v) => Math.Floor(v * 10 + 0.5) / 10;
        public static double Round2(double v) => Math.Floor(v * 100 + 0.5) / 100;
        public static double Round3(double v) => Math.Floor(v * 1000 + 0.5) / 1000;
    }
}
