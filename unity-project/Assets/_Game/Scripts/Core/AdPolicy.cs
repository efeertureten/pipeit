namespace SuUstasi.Core
{
    /// <summary>
    /// Interstitial rules (GDD): none in levels 1–3, never on fail, at least 60 s apart,
    /// every 2nd eligible win. Pure logic — the clock is passed in, so it is fully testable.
    /// </summary>
    public sealed class AdPolicy
    {
        public double CooldownSeconds { get; }
        public int EveryNWins { get; }
        public int NoInterstitialUpToLevel { get; }

        public double LastShownAt { get; private set; } = double.NegativeInfinity;
        public int WinsSinceAd { get; private set; }

        public AdPolicy(double cooldownSeconds = AdRules.CooldownSeconds, int everyNWins = AdRules.EveryNWins,
            int noInterstitialUpToLevel = AdRules.NoInterstitialUpToLevel)
        {
            CooldownSeconds = cooldownSeconds;
            EveryNWins = everyNWins;
            NoInterstitialUpToLevel = noInterstitialUpToLevel;
        }

        /// <summary>Register a win; returns true when an interstitial should be shown now.</summary>
        public bool OnLevelWin(int level, double nowSeconds)
        {
            if (level <= NoInterstitialUpToLevel) return false;
            WinsSinceAd++;
            if (WinsSinceAd < EveryNWins) return false;
            return nowSeconds - LastShownAt >= CooldownSeconds;
        }

        /// <summary>Interstitials are never shown on fail.</summary>
        public bool OnLevelFail() => false;

        public void MarkShown(double nowSeconds)
        {
            LastShownAt = nowSeconds;
            WinsSinceAd = 0;
        }
    }
}
