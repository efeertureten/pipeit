using System;
using SuUstasi.Core;
using SuUstasi.Services;

namespace SuUstasi.Gameplay
{
    /// <summary>Coins, booster inventory, rewards, IAP fulfilment and level progress — persisted through ISaveService.</summary>
    public sealed class EconomyService
    {
        private readonly ISaveService _save;
        private readonly IAnalyticsService _analytics;
        private readonly IIapService _iap;

        public PlayerProgress Progress { get; private set; }
        public event Action Changed;

        public EconomyService(ISaveService save, IAnalyticsService analytics, IIapService iap)
        {
            _save = save;
            _analytics = analytics;
            _iap = iap;
            Progress = save.Load();
        }

        public int Coins => Progress.Coins;
        public int BoosterCount(BoosterType b) => Progress.GetBooster(b);

        public void Persist()
        {
            _save.Save(Progress);
            Changed?.Invoke();
        }

        public WinReward GrantWinReward(int level, int mistakes)
        {
            var reward = Progression.WinRewardFor(level, mistakes);
            Progress.Coins += reward.Total;
            Progress.Wins++;
            if (!Progress.CompletedLevels.Contains(level)) Progress.CompletedLevels.Add(level);
            Persist();
            return reward;
        }

        public void RecordFail()
        {
            Progress.Fails++;
            Persist();
        }

        /// <summary>Unlocks the next pack entry after a win.</summary>
        public void UnlockUpTo(int packIndex)
        {
            if (packIndex > Progress.MaxLevelIndex) Progress.MaxLevelIndex = packIndex;
            Persist();
        }

        public bool TryBuyBooster(BoosterType b)
        {
            int price = BoosterRules.CoinPrice(b);
            if (Progress.Coins < price) return false;
            Progress.Coins -= price;
            Progress.SetBooster(b, Progress.GetBooster(b) + 1);
            Persist();
            return true;
        }

        public void AddBooster(BoosterType b, int amount = 1)
        {
            Progress.SetBooster(b, Progress.GetBooster(b) + amount);
            Persist();
        }

        public bool TryConsumeBooster(BoosterType b)
        {
            int have = Progress.GetBooster(b);
            if (have <= 0) return false;
            Progress.SetBooster(b, have - 1);
            Persist();
            return true;
        }

        /// <summary>One free booster the first time its unlock level is reached. Returns true when gifted.</summary>
        public bool GiftOnUnlock(BoosterType b, int level)
        {
            string id = BoosterRules.Id(b);
            if (level != BoosterRules.UnlockLevel(b) || Progress.BoosterGifts.Contains(id)) return false;
            Progress.BoosterGifts.Add(id);
            AddBooster(b);
            return true;
        }

        public void Purchase(string productId, Action<bool> done)
        {
            _iap.Purchase(productId, result =>
            {
                if (!result.Success)
                {
                    done?.Invoke(false);
                    return;
                }
                IapProduct product = null;
                foreach (var p in _iap.Products)
                    if (p.Id == productId) product = p;
                if (product != null)
                {
                    Progress.Coins += product.Coins;
                    if (product.RemovesAds) Progress.NoAds = true;
                    _analytics.TrackIapPurchase(product.Id, product.Price, product.Currency);
                }
                Persist();
                done?.Invoke(true);
            });
        }

        public void ResetProgress()
        {
            _save.Delete();
            Progress = new PlayerProgress();
            Persist();
        }
    }
}
