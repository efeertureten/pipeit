using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace SuUstasi.Services
{
    /// <summary>Monotonic wall clock (seconds since construction).</summary>
    public sealed class SystemClock : IClock
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public double Now => _sw.Elapsed.TotalSeconds;
    }

    /// <summary>Manually driven clock for tests.</summary>
    public sealed class ManualClock : IClock
    {
        public double Now { get; set; }
        public void Advance(double seconds) => Now += seconds;
    }

    public sealed class ConsoleLogSink : ILogSink
    {
        public void Log(string message) => Console.WriteLine(message);
    }

    /// <summary>Keeps every event in memory and mirrors it to a log sink. Warns on events outside the GDD taxonomy.</summary>
    public sealed class LocalAnalyticsService : IAnalyticsService
    {
        private readonly ILogSink _log;
        public List<KeyValuePair<string, IDictionary<string, object>>> Events { get; } = new List<KeyValuePair<string, IDictionary<string, object>>>();

        public LocalAnalyticsService(ILogSink log = null) { _log = log; }

        public void Track(string eventName, IDictionary<string, object> parameters = null)
        {
            parameters = parameters ?? new Dictionary<string, object>();
            Events.Add(new KeyValuePair<string, IDictionary<string, object>>(eventName, parameters));
            if (_log == null) return;
            var sb = new StringBuilder("[analytics] ").Append(eventName);
            if (!AnalyticsEvents.IsKnown(eventName)) sb.Append(" (UNKNOWN EVENT)");
            foreach (var kv in parameters) sb.Append(' ').Append(kv.Key).Append('=').Append(kv.Value);
            _log.Log(sb.ToString());
        }

        public int Count(string eventName)
        {
            int n = 0;
            foreach (var e in Events)
                if (e.Key == eventName) n++;
            return n;
        }
    }

    /// <summary>Ads that are always ready and complete instantly (rewarded always rewards unless configured otherwise).</summary>
    public sealed class MockAdsService : IAdsService
    {
        private readonly ILogSink _log;
        public bool RewardSucceeds = true;
        public int InterstitialsShown { get; private set; }
        public int RewardedShown { get; private set; }

        public MockAdsService(ILogSink log = null) { _log = log; }

        public bool IsInterstitialReady => true;
        public bool IsRewardedReady => true;

        public void ShowInterstitial(string placement, Action onClosed)
        {
            InterstitialsShown++;
            _log?.Log("[ads] interstitial (mock) @" + placement);
            onClosed?.Invoke();
        }

        public void ShowRewarded(string placement, Action<bool> onCompleted)
        {
            RewardedShown++;
            _log?.Log("[ads] rewarded (mock) @" + placement + " -> " + RewardSucceeds);
            onCompleted?.Invoke(RewardSucceeds);
        }
    }

    /// <summary>Catalogue of mock products; every purchase succeeds unless the id is unknown.</summary>
    public sealed class MockIapService : IIapService
    {
        private readonly List<IapProduct> _products = new List<IapProduct>
        {
            new IapProduct { Id = "coins_500", Title = "500 Jeton", Price = 29.99m, Coins = 500 },
            new IapProduct { Id = "coins_1500", Title = "1500 Jeton", Price = 69.99m, Coins = 1500 },
            new IapProduct { Id = "no_ads", Title = "Reklamsız", Price = 49.99m, RemovesAds = true },
        };

        public IReadOnlyList<IapProduct> Products => _products;

        public void Purchase(string productId, Action<IapResult> onResult)
        {
            bool known = _products.Exists(p => p.Id == productId);
            onResult?.Invoke(new IapResult { Success = known, ProductId = productId, Error = known ? null : "unknown product" });
        }
    }

    public sealed class InMemorySaveService : ISaveService
    {
        private string _json;
        public PlayerProgress Load() => PlayerProgress.FromJson(_json);
        public void Save(PlayerProgress progress) => _json = progress.ToJson();
        public void Delete() => _json = null;
    }

    /// <summary>Local JSON file save (Unity passes Application.persistentDataPath). Writes atomically via a temp file.</summary>
    public sealed class JsonFileSaveService : ISaveService
    {
        private readonly string _path;
        private readonly ILogSink _log;

        public JsonFileSaveService(string path, ILogSink log = null)
        {
            _path = path;
            _log = log;
        }

        public string FilePath => _path;

        public PlayerProgress Load()
        {
            try
            {
                return File.Exists(_path) ? PlayerProgress.FromJson(File.ReadAllText(_path)) : new PlayerProgress();
            }
            catch (Exception e)
            {
                _log?.Log("[save] corrupt save, starting fresh: " + e.Message);
                return new PlayerProgress();
            }
        }

        public void Save(PlayerProgress progress)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, progress.ToJson(), new UTF8Encoding(false));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }

        public void Delete()
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
    }

    /// <summary>Tiny service registry so gameplay code depends on interfaces only.</summary>
    public static class ServiceLocator
    {
        private static readonly Dictionary<Type, object> Services = new Dictionary<Type, object>();

        public static void Register<T>(T service) where T : class => Services[typeof(T)] = service;
        public static T Get<T>() where T : class => Services.TryGetValue(typeof(T), out var s) ? (T)s : null;
        public static bool Has<T>() where T : class => Services.ContainsKey(typeof(T));
        public static void Clear() => Services.Clear();
    }
}
