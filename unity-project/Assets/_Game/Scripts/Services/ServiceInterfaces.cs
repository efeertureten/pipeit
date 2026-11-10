using System;
using System.Collections.Generic;

namespace SuUstasi.Services
{
    /// <summary>Ad network facade. Production: wrap LevelPlay/AdMob/AppLovin; dev: <see cref="MockAdsService"/>.</summary>
    public interface IAdsService
    {
        bool IsInterstitialReady { get; }
        bool IsRewardedReady { get; }
        void ShowInterstitial(string placement, Action onClosed);
        /// <summary>onCompleted(true) when the reward was earned.</summary>
        void ShowRewarded(string placement, Action<bool> onCompleted);
    }

    /// <summary>Analytics facade. Event names live in <see cref="AnalyticsEvents"/>.</summary>
    public interface IAnalyticsService
    {
        void Track(string eventName, IDictionary<string, object> parameters = null);
    }

    /// <summary>Persistence facade (local JSON in dev, cloud save later).</summary>
    public interface ISaveService
    {
        PlayerProgress Load();
        void Save(PlayerProgress progress);
        void Delete();
    }

    public sealed class IapProduct
    {
        public string Id;
        public string Title;
        public decimal Price;
        public string Currency = "TRY";
        public int Coins;
        public bool RemovesAds;
    }

    public sealed class IapResult
    {
        public bool Success;
        public string ProductId;
        public string Error;
    }

    /// <summary>In-app purchase facade. Production: Unity IAP; dev: <see cref="MockIapService"/>.</summary>
    public interface IIapService
    {
        IReadOnlyList<IapProduct> Products { get; }
        void Purchase(string productId, Action<IapResult> onResult);
    }

    /// <summary>Seconds clock, injectable for tests (AdPolicy cooldowns, timers).</summary>
    public interface IClock
    {
        double Now { get; }
    }

    public interface ILogSink
    {
        void Log(string message);
    }
}
