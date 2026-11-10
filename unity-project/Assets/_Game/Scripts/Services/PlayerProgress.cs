using System.Collections.Generic;
using SuUstasi.Core;
using SuUstasi.Core.Json;

namespace SuUstasi.Services
{
    /// <summary>Everything persisted between sessions. Serialized with MiniJson (versioned).</summary>
    public sealed class PlayerProgress
    {
        public const int CurrentVersion = 1;

        public int Version = CurrentVersion;
        public int Coins = EconomyRules.StartCoins;
        /// <summary>Highest unlocked index into the level pack.</summary>
        public int MaxLevelIndex;
        public List<int> CompletedLevels = new List<int>();
        public int XRay = BoosterRules.StartCount(BoosterType.XRay);
        public int Hammer = BoosterRules.StartCount(BoosterType.Hammer);
        public int Patch = BoosterRules.StartCount(BoosterType.Patch);
        public bool SoundOn = true;
        public bool NoAds;
        public List<string> SeenIntros = new List<string>();
        public List<string> FtueSteps = new List<string>();
        public List<string> BoosterGifts = new List<string>();
        public int Wins;
        public int Fails;

        public int GetBooster(BoosterType b) => b == BoosterType.XRay ? XRay : b == BoosterType.Hammer ? Hammer : Patch;

        public void SetBooster(BoosterType b, int value)
        {
            if (value < 0) value = 0;
            if (b == BoosterType.XRay) XRay = value;
            else if (b == BoosterType.Hammer) Hammer = value;
            else Patch = value;
        }

        public string ToJson()
        {
            var o = new Dictionary<string, object>
            {
                ["version"] = Version, ["coins"] = Coins, ["maxLevelIndex"] = MaxLevelIndex,
                ["completedLevels"] = Box(CompletedLevels), ["xray"] = XRay, ["hammer"] = Hammer, ["patch"] = Patch,
                ["soundOn"] = SoundOn, ["noAds"] = NoAds, ["seenIntros"] = Box(SeenIntros), ["ftueSteps"] = Box(FtueSteps),
                ["boosterGifts"] = Box(BoosterGifts), ["wins"] = Wins, ["fails"] = Fails,
            };
            return MiniJson.Serialize(o, true);
        }

        /// <summary>Tolerant loader: unknown/missing fields fall back to defaults (forward compatible).</summary>
        public static PlayerProgress FromJson(string json)
        {
            var p = new PlayerProgress();
            if (string.IsNullOrEmpty(json)) return p;
            if (!(MiniJson.Parse(json) is Dictionary<string, object> o)) return p;
            if (MiniJson.TryGetInt(o, "version", out var v)) p.Version = v;
            if (MiniJson.TryGetInt(o, "coins", out var c)) p.Coins = c;
            if (MiniJson.TryGetInt(o, "maxLevelIndex", out var m)) p.MaxLevelIndex = m;
            if (MiniJson.TryGetInt(o, "xray", out var x)) p.XRay = x;
            if (MiniJson.TryGetInt(o, "hammer", out var h)) p.Hammer = h;
            if (MiniJson.TryGetInt(o, "patch", out var pa)) p.Patch = pa;
            if (MiniJson.TryGetInt(o, "wins", out var w)) p.Wins = w;
            if (MiniJson.TryGetInt(o, "fails", out var f)) p.Fails = f;
            if (o.TryGetValue("soundOn", out var s) && s is bool sb) p.SoundOn = sb;
            if (o.TryGetValue("noAds", out var na) && na is bool nab) p.NoAds = nab;
            p.CompletedLevels = Ints(MiniJson.GetList(o, "completedLevels"));
            p.SeenIntros = Strings(MiniJson.GetList(o, "seenIntros"));
            p.FtueSteps = Strings(MiniJson.GetList(o, "ftueSteps"));
            p.BoosterGifts = Strings(MiniJson.GetList(o, "boosterGifts"));
            p.Version = CurrentVersion;
            return p;
        }

        private static List<object> Box<T>(List<T> list)
        {
            var l = new List<object>();
            foreach (var x in list) l.Add(x);
            return l;
        }

        private static List<int> Ints(List<object> list)
        {
            var r = new List<int>();
            if (list == null) return r;
            foreach (var x in list)
                if (x is long l) r.Add((int)l);
            return r;
        }

        private static List<string> Strings(List<object> list)
        {
            var r = new List<string>();
            if (list == null) return r;
            foreach (var x in list)
                if (x is string s) r.Add(s);
            return r;
        }
    }
}
