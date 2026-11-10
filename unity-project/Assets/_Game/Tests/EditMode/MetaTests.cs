using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using SuUstasi.Core;
using SuUstasi.Gameplay;
using SuUstasi.Services;
using static SuUstasi.Tests.TestLevels;

namespace SuUstasi.Tests
{
    public class ProgressionTests
    {
        [TestCase(7, 0, 30)]
        [TestCase(7, 2, 20)]
        [TestCase(10, 0, 50)]
        [TestCase(15, 1, 40)]
        public void WinReward_20Plus10FlawlessPlus20Breather(int level, int mistakes, int total)
        {
            Assert.That(Progression.WinRewardFor(level, mistakes).Total, Is.EqualTo(total));
        }

        [TestCase(1, "Çırak")]
        [TestCase(19, "Çırak")]
        [TestCase(20, "Kalfa")]
        [TestCase(49, "Kalfa")]
        [TestCase(50, "Usta")]
        [TestCase(99, "Usta")]
        [TestCase(100, "Başusta")]
        public void Ranks(int level, string rank) => Assert.That(Progression.RankFor(level), Is.EqualTo(rank));

        [TestCase(1, "Banyo")]
        [TestCase(20, "Banyo")]
        [TestCase(21, "Mutfak")]
        [TestCase(41, "Apartman Bodrumu")]
        [TestCase(61, "Fabrika")]
        [TestCase(81, "Şehir Kanalı")]
        [TestCase(101, "Arıtma Tesisi")]
        [TestCase(121, "Banyo")]
        public void Themes_CycleEvery20Levels(int level, string theme) => Assert.That(Progression.ThemeFor(level), Is.EqualTo(theme));

        [Test]
        public void BoosterUnlocks_5_10_15()
        {
            Assert.That(Progression.BoosterUnlocked(BoosterType.XRay, 4), Is.False);
            Assert.That(Progression.BoosterUnlocked(BoosterType.XRay, 5), Is.True);
            Assert.That(Progression.BoosterUnlocked(BoosterType.Hammer, 9), Is.False);
            Assert.That(Progression.BoosterUnlocked(BoosterType.Hammer, 10), Is.True);
            Assert.That(Progression.BoosterUnlocked(BoosterType.Patch, 14), Is.False);
            Assert.That(Progression.BoosterUnlocked(BoosterType.Patch, 15), Is.True);
        }
    }

    public class AdPolicyTests
    {
        [Test]
        public void NoInterstitialsInLevels1To3()
        {
            var a = new AdPolicy();
            for (int l = 1; l <= 3; l++) Assert.That(a.OnLevelWin(l, 1000 * l), Is.False);
        }

        [Test]
        public void Every2ndWin_NeverOnFail_60sCooldown()
        {
            var a = new AdPolicy();
            Assert.That(a.OnLevelFail(), Is.False);
            Assert.That(a.OnLevelWin(4, 100), Is.False);
            Assert.That(a.OnLevelWin(5, 110), Is.True);
            a.MarkShown(110);
            Assert.That(a.OnLevelWin(6, 120), Is.False);
            Assert.That(a.OnLevelWin(7, 130), Is.False, "cooldown");
            Assert.That(a.OnLevelWin(8, 171), Is.True);
        }

        [Test]
        public void Gate_ShowsThroughAdsService_AndTracksImpression()
        {
            var clock = new ManualClock { Now = 1000 };
            var ads = new MockAdsService();
            var analytics = new LocalAnalyticsService();
            var gate = new InterstitialGate(new AdPolicy(), ads, analytics, clock, () => false);
            int done = 0;
            Assert.That(gate.AfterWin(4, () => done++), Is.False);
            Assert.That(gate.AfterWin(5, () => done++), Is.True);
            gate.AfterFail();
            Assert.That(done, Is.EqualTo(2));
            Assert.That(ads.InterstitialsShown, Is.EqualTo(1));
            Assert.That(analytics.Count(AnalyticsEvents.AdImpression), Is.EqualTo(1));
        }

        [Test]
        public void Gate_RespectsNoAdsPurchase()
        {
            var ads = new MockAdsService();
            var gate = new InterstitialGate(new AdPolicy(), ads, new LocalAnalyticsService(), new ManualClock { Now = 500 }, () => true);
            gate.AfterWin(4, null);
            gate.AfterWin(5, null);
            Assert.That(ads.InterstitialsShown, Is.Zero);
        }
    }

    public class ServicesTests
    {
        private static EconomyService Economy(out LocalAnalyticsService analytics, ISaveService save = null)
        {
            analytics = new LocalAnalyticsService();
            return new EconomyService(save ?? new InMemorySaveService(), analytics, new MockIapService());
        }

        [Test]
        public void PlayerProgress_RoundTripsThroughJson()
        {
            var p = new PlayerProgress { Coins = 321, MaxLevelIndex = 7, NoAds = true, Hammer = 5 };
            p.CompletedLevels.Add(3);
            p.SeenIntros.Add("elbow");
            var back = PlayerProgress.FromJson(p.ToJson());
            Assert.That(back.Coins, Is.EqualTo(321));
            Assert.That(back.MaxLevelIndex, Is.EqualTo(7));
            Assert.That(back.NoAds, Is.True);
            Assert.That(back.Hammer, Is.EqualTo(5));
            Assert.That(back.CompletedLevels, Is.EqualTo(new[] { 3 }));
            Assert.That(back.SeenIntros, Is.EqualTo(new[] { "elbow" }));
        }

        [Test]
        public void JsonFileSave_PersistsAndSurvivesCorruption()
        {
            var path = Path.Combine(Path.GetTempPath(), "su_ustasi_test_" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var save = new JsonFileSaveService(path);
                var p = new PlayerProgress { Coins = 999 };
                save.Save(p);
                Assert.That(new JsonFileSaveService(path).Load().Coins, Is.EqualTo(999));
                File.WriteAllText(path, "{corrupt");
                Assert.That(new JsonFileSaveService(path).Load().Coins, Is.EqualTo(EconomyRules.StartCoins));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Economy_GrantsRewards_BuysAndConsumesBoosters()
        {
            var e = Economy(out _);
            int start = e.Coins;
            Assert.That(e.GrantWinReward(10, 0).Total, Is.EqualTo(50));
            Assert.That(e.Coins, Is.EqualTo(start + 50));
            int xray = e.BoosterCount(BoosterType.XRay);
            Assert.That(e.TryBuyBooster(BoosterType.XRay), Is.True);
            Assert.That(e.BoosterCount(BoosterType.XRay), Is.EqualTo(xray + 1));
            Assert.That(e.Coins, Is.EqualTo(start + 50 - BoosterRules.CoinPrice(BoosterType.XRay)));
            Assert.That(e.TryConsumeBooster(BoosterType.XRay), Is.True);
            Assert.That(e.GiftOnUnlock(BoosterType.Hammer, 10), Is.True);
            Assert.That(e.GiftOnUnlock(BoosterType.Hammer, 10), Is.False, "gift only once");
        }

        [Test]
        public void Economy_IapPurchase_AppliesProductAndTracks()
        {
            var e = Economy(out var analytics);
            int start = e.Coins;
            bool ok = false;
            e.Purchase("coins_500", r => ok = r);
            Assert.That(ok, Is.True);
            Assert.That(e.Coins, Is.EqualTo(start + 500));
            e.Purchase("no_ads", null);
            Assert.That(e.Progress.NoAds, Is.True);
            Assert.That(analytics.Count(AnalyticsEvents.IapPurchase), Is.EqualTo(2));
            e.Purchase("nope", r => ok = r);
            Assert.That(ok, Is.False);
        }

        [Test]
        public void Analytics_TaxonomyMatchesGdd()
        {
            Assert.That(AnalyticsEvents.All, Is.EquivalentTo(new[]
            {
                "level_start", "level_complete", "level_fail", "blocked_tap", "booster_used", "ad_impression", "iap_purchase", "ftue_step",
            }));
        }

        [Test]
        public void ServiceLocator_ResolvesByInterface()
        {
            ServiceLocator.Clear();
            var ads = new MockAdsService();
            ServiceLocator.Register<IAdsService>(ads);
            Assert.That(ServiceLocator.Get<IAdsService>(), Is.SameAs(ads));
            Assert.That(ServiceLocator.Get<IIapService>(), Is.Null);
            ServiceLocator.Clear();
        }
    }

    public class BoosterControllerTests
    {
        private static BoosterController Controller(out EconomyService economy, out ManualClock clock, out LocalAnalyticsService analytics)
        {
            analytics = new LocalAnalyticsService();
            economy = new EconomyService(new InMemorySaveService(), analytics, new MockIapService());
            clock = new ManualClock();
            return new BoosterController(economy, analytics, clock);
        }

        [Test]
        public void XRay_HighlightsExitablePipesFor3Seconds()
        {
            var c = Controller(out var economy, out var clock, out var analytics);
            var level = Level8();
            level.Level = 9;
            var s = new GameSession(level);
            Assert.That(c.UseXRay(s, out var ids), Is.EqualTo(BoosterStatus.Ok));
            Assert.That(ids, Is.EqualTo(new[] { "p1", "p2", "p3" }));
            Assert.That(c.IsXRayActive, Is.True);
            clock.Advance(3.01);
            Assert.That(c.IsXRayActive, Is.False);
            Assert.That(analytics.Count(AnalyticsEvents.BoosterUsed), Is.EqualTo(1));
            Assert.That(economy.BoosterCount(BoosterType.XRay), Is.EqualTo(BoosterRules.StartCount(BoosterType.XRay) - 1));
        }

        [Test]
        public void Boosters_AreLockedBelowTheirUnlockLevel()
        {
            var c = Controller(out _, out _, out _);
            var s = new GameSession(Level8()); // level 8
            Assert.That(c.Check(BoosterType.XRay, s), Is.EqualTo(BoosterStatus.Ok));
            Assert.That(c.Check(BoosterType.Hammer, s), Is.EqualTo(BoosterStatus.Locked));
            Assert.That(c.Check(BoosterType.Patch, s), Is.EqualTo(BoosterStatus.Locked));
        }

        [Test]
        public void Hammer_RemovesTarget_AndConsumesOnlyOnUse()
        {
            var c = Controller(out var economy, out _, out _);
            var level = Level8();
            level.Level = 12;
            var s = new GameSession(level);
            Assert.That(c.ArmHammer(s), Is.EqualTo(BoosterStatus.Ok));
            Assert.That(economy.BoosterCount(BoosterType.Hammer), Is.EqualTo(1), "arming is free");
            var events = c.ApplyHammer(s, "p6");
            Assert.That(events.Any(e => e.Kind == GameEventKind.Removed && e.Cause == ExitCause.Hammer), Is.True);
            Assert.That(economy.BoosterCount(BoosterType.Hammer), Is.Zero);
            Assert.That(c.ArmHammer(s), Is.EqualTo(BoosterStatus.Empty));
        }

        [Test]
        public void Patch_AddsLeak_UntilCapOf4()
        {
            var c = Controller(out var economy, out _, out _);
            economy.AddBooster(BoosterType.Patch, 3);
            var level = Level8();
            level.Level = 20;
            var s = new GameSession(level);
            Assert.That(c.UsePatch(s), Is.EqualTo(BoosterStatus.Ok));
            Assert.That(s.Leaks, Is.EqualTo(4));
            Assert.That(c.UsePatch(s), Is.EqualTo(BoosterStatus.NotApplicable));
            Assert.That(economy.BoosterCount(BoosterType.Patch), Is.EqualTo(3));
        }
    }

    public class StateMachineTests
    {
        private enum S { A, B, C }

        [Test]
        public void EnforcesTransitions_AndRunsHooks()
        {
            int entered = 0, exited = 0;
            var fsm = new StateMachine<S>(S.A).Allow(S.A, S.B).Allow(S.B, S.C, S.A).OnEnter(S.B, () => entered++).OnExit(S.A, () => exited++);
            Assert.That(fsm.CanGo(S.C), Is.False);
            Assert.Throws<InvalidOperationException>(() => fsm.Go(S.C));
            fsm.Go(S.B);
            Assert.That(fsm.Current, Is.EqualTo(S.B));
            Assert.That(fsm.Previous, Is.EqualTo(S.A));
            Assert.That(entered, Is.EqualTo(1));
            Assert.That(exited, Is.EqualTo(1));
            Assert.That(fsm.TryGo(S.C), Is.True);
            Assert.That(fsm.TryGo(S.B), Is.False);
        }
    }

    public class SfxRecipeTests
    {
        [Test]
        public void AllRecipes_AreAudible_AndNeverClip()
        {
            var clips = new[]
            {
                SfxRecipes.Water(1), SfxRecipes.Bonk(), SfxRecipes.Drain(), SfxRecipes.Crack(), SfxRecipes.Ice(), SfxRecipes.Unlock(),
                SfxRecipes.Rotate(), SfxRecipes.Leak(), SfxRecipes.Hammer(), SfxRecipes.Win(), SfxRecipes.Lose(), SfxRecipes.Coin(), SfxRecipes.Click(),
            };
            foreach (var c in clips)
            {
                Assert.That(c.Length, Is.GreaterThan(1000));
                float peak = c.Max(v => Math.Abs(v));
                Assert.That(peak, Is.InRange(0.5f, 1.0f));
                Assert.That(c.Any(float.IsNaN), Is.False);
            }
        }

        [Test]
        public void Water_PitchRisesWithCombo()
        {
            // count zero crossings in the "bloop" window as a pitch proxy
            int Crossings(float[] b)
            {
                int n = 0;
                for (int i = (int)(0.03 * SfxRecipes.SampleRate) + 1; i < (int)(0.15 * SfxRecipes.SampleRate); i++)
                    if (Math.Sign(b[i]) != Math.Sign(b[i - 1])) n++;
                return n;
            }
            Assert.That(Crossings(SfxRecipes.Water(5)), Is.GreaterThan(Crossings(SfxRecipes.Water(1))));
        }
    }
}
