using System.Collections.Generic;

namespace SuUstasi.Services
{
    /// <summary>GDD analytics taxonomy + typed helpers so call sites cannot misspell event names.</summary>
    public static class AnalyticsEvents
    {
        public const string LevelStart = "level_start";
        public const string LevelComplete = "level_complete";
        public const string LevelFail = "level_fail";
        public const string BlockedTap = "blocked_tap";
        public const string BoosterUsed = "booster_used";
        public const string AdImpression = "ad_impression";
        public const string IapPurchase = "iap_purchase";
        public const string FtueStep = "ftue_step";

        public static readonly string[] All = { LevelStart, LevelComplete, LevelFail, BlockedTap, BoosterUsed, AdImpression, IapPurchase, FtueStep };

        public static bool IsKnown(string name) => System.Array.IndexOf(All, name) >= 0;

        public static void TrackLevelStart(this IAnalyticsService a, int level, int pipes, string theme) =>
            a.Track(LevelStart, new Dictionary<string, object> { ["level"] = level, ["pipes"] = pipes, ["theme"] = theme });

        public static void TrackLevelComplete(this IAnalyticsService a, int level, int mistakes, int taps, int leaksLeft, double seconds, int boostersUsed, int coins) =>
            a.Track(LevelComplete, new Dictionary<string, object>
            {
                ["level"] = level, ["mistakes"] = mistakes, ["taps"] = taps, ["leaksLeft"] = leaksLeft,
                ["seconds"] = System.Math.Round(seconds), ["boostersUsed"] = boostersUsed, ["coins"] = coins,
            });

        public static void TrackLevelFail(this IAnalyticsService a, int level, int taps, double waterLeft, double seconds) =>
            a.Track(LevelFail, new Dictionary<string, object> { ["level"] = level, ["taps"] = taps, ["waterLeft"] = waterLeft, ["seconds"] = System.Math.Round(seconds) });

        public static void TrackBlockedTap(this IAnalyticsService a, int level, string pipeId, string reason, int leakCost) =>
            a.Track(BlockedTap, new Dictionary<string, object> { ["level"] = level, ["pipeId"] = pipeId, ["reason"] = reason, ["leakCost"] = leakCost });

        public static void TrackBoosterUsed(this IAnalyticsService a, string booster, int level, int left) =>
            a.Track(BoosterUsed, new Dictionary<string, object> { ["booster"] = booster, ["level"] = level, ["left"] = left });

        public static void TrackAdImpression(this IAnalyticsService a, string format, string placement, int level) =>
            a.Track(AdImpression, new Dictionary<string, object> { ["format"] = format, ["placement"] = placement, ["level"] = level });

        public static void TrackIapPurchase(this IAnalyticsService a, string productId, decimal price, string currency) =>
            a.Track(IapPurchase, new Dictionary<string, object> { ["productId"] = productId, ["price"] = (double)price, ["currency"] = currency });

        public static void TrackFtueStep(this IAnalyticsService a, string step, int level) =>
            a.Track(FtueStep, new Dictionary<string, object> { ["step"] = step, ["level"] = level });
    }
}
