using System.Collections.Generic;
using SuUstasi.Core;
using SuUstasi.Services;

namespace SuUstasi.Gameplay
{
    public enum BoosterStatus
    {
        Ok,
        /// <summary>Level below the booster's unlock level (XRay 5, Hammer 10, Patch 15).</summary>
        Locked,
        /// <summary>Inventory is empty — offer the shop / a rewarded ad.</summary>
        Empty,
        /// <summary>Booster cannot apply right now (e.g. Patch at max leaks, level not playing).</summary>
        NotApplicable,
    }

    /// <summary>
    /// Booster rules: XRay highlights exitable pipes for 3 s, Hammer removes any pipe regardless of
    /// blockers, Patch adds +1 leak (cap 4). Inventory is consumed only on successful use.
    /// </summary>
    public sealed class BoosterController
    {
        private readonly EconomyService _economy;
        private readonly IAnalyticsService _analytics;
        private readonly IClock _clock;

        public bool HammerArmed { get; private set; }
        public double XRayUntil { get; private set; } = double.NegativeInfinity;
        public int UsedThisLevel { get; private set; }
        /// <summary>Lets custom/debug levels use boosters regardless of unlock level.</summary>
        public bool IgnoreUnlocks { get; set; }

        public BoosterController(EconomyService economy, IAnalyticsService analytics, IClock clock)
        {
            _economy = economy;
            _analytics = analytics;
            _clock = clock;
        }

        public bool IsXRayActive => _clock.Now < XRayUntil;

        public void ResetForLevel()
        {
            HammerArmed = false;
            XRayUntil = double.NegativeInfinity;
            UsedThisLevel = 0;
        }

        public BoosterStatus Check(BoosterType b, GameSession session)
        {
            if (session == null || session.Status != SessionStatus.Playing) return BoosterStatus.NotApplicable;
            if (!IgnoreUnlocks && !Progression.BoosterUnlocked(b, session.LevelNumber)) return BoosterStatus.Locked;
            if (_economy.BoosterCount(b) <= 0) return BoosterStatus.Empty;
            if (b == BoosterType.Patch && session.Leaks >= session.MaxLeaks) return BoosterStatus.NotApplicable;
            return BoosterStatus.Ok;
        }

        /// <summary>Highlights all currently exitable pipes for GameTiming.XRayDuration seconds.</summary>
        public BoosterStatus UseXRay(GameSession session, out List<string> highlighted)
        {
            highlighted = new List<string>();
            var status = Check(BoosterType.XRay, session);
            if (status != BoosterStatus.Ok) return status;
            _economy.TryConsumeBooster(BoosterType.XRay);
            XRayUntil = _clock.Now + GameTiming.XRayDuration;
            highlighted = session.ExitablePipeIds();
            Used(BoosterType.XRay, session);
            return BoosterStatus.Ok;
        }

        public BoosterStatus ArmHammer(GameSession session)
        {
            var status = Check(BoosterType.Hammer, session);
            if (status == BoosterStatus.Ok) HammerArmed = true;
            return status;
        }

        public void CancelHammer() => HammerArmed = false;

        /// <summary>Removes the target pipe (consumes one Hammer). Returns null when nothing happened.</summary>
        public List<GameEvent> ApplyHammer(GameSession session, string pipeId)
        {
            if (!HammerArmed) return null;
            HammerArmed = false;
            if (Check(BoosterType.Hammer, session) != BoosterStatus.Ok) return null;
            var events = session.Hammer(pipeId);
            if (events == null) return null;
            _economy.TryConsumeBooster(BoosterType.Hammer);
            Used(BoosterType.Hammer, session);
            return events;
        }

        public BoosterStatus UsePatch(GameSession session)
        {
            var status = Check(BoosterType.Patch, session);
            if (status != BoosterStatus.Ok) return status;
            if (!session.Patch()) return BoosterStatus.NotApplicable;
            _economy.TryConsumeBooster(BoosterType.Patch);
            Used(BoosterType.Patch, session);
            return BoosterStatus.Ok;
        }

        private void Used(BoosterType b, GameSession session)
        {
            UsedThisLevel++;
            _analytics.TrackBoosterUsed(BoosterRules.Id(b), session.LevelNumber, _economy.BoosterCount(b));
        }
    }
}
