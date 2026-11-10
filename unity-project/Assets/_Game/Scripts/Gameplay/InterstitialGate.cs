using System;
using SuUstasi.Core;
using SuUstasi.Services;

namespace SuUstasi.Gameplay
{
    /// <summary>Applies AdPolicy around IAdsService and reports ad_impression.</summary>
    public sealed class InterstitialGate
    {
        private readonly AdPolicy _policy;
        private readonly IAdsService _ads;
        private readonly IAnalyticsService _analytics;
        private readonly IClock _clock;
        private readonly Func<bool> _noAds;

        public InterstitialGate(AdPolicy policy, IAdsService ads, IAnalyticsService analytics, IClock clock, Func<bool> noAds)
        {
            _policy = policy;
            _ads = ads;
            _analytics = analytics;
            _clock = clock;
            _noAds = noAds ?? (() => false);
        }

        /// <summary>Call after a win; invokes <paramref name="onDone"/> (after the ad if one is shown). Returns true if an ad ran.</summary>
        public bool AfterWin(int level, Action onDone)
        {
            bool due = _policy.OnLevelWin(level, _clock.Now);
            if (!due || _noAds() || !_ads.IsInterstitialReady)
            {
                onDone?.Invoke();
                return false;
            }
            _analytics.TrackAdImpression("interstitial", "level_complete", level);
            _ads.ShowInterstitial("level_complete", () =>
            {
                _policy.MarkShown(_clock.Now);
                onDone?.Invoke();
            });
            return true;
        }

        /// <summary>GDD: never show an interstitial on fail.</summary>
        public void AfterFail() => _policy.OnLevelFail();

        public void ShowRewarded(string placement, int level, Action<bool> onCompleted)
        {
            if (!_ads.IsRewardedReady)
            {
                onCompleted?.Invoke(false);
                return;
            }
            _analytics.TrackAdImpression("rewarded", placement, level);
            _ads.ShowRewarded(placement, onCompleted);
        }
    }
}
