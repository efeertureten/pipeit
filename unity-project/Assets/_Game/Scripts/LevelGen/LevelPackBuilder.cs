using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SuUstasi.Core;
using SuUstasi.Core.Json;

namespace SuUstasi.LevelGen
{
    /// <summary>GDD reference levels that are shipped verbatim instead of being generated.</summary>
    public static class GddReferenceLevels
    {
        /// <summary>Level 8 exactly as written in the GDD (must stay byte-identical).</summary>
        public const string Level8 =
            "{\n" +
            "  \"level\": 8,\n" +
            "  \"grid\": [6, 6],\n" +
            "  \"tank\": { \"x\": 2, \"y\": 2, \"w\": 2, \"h\": 2 },\n" +
            "  \"leaks\": 3,\n" +
            "  \"pipes\": [\n" +
            "    { \"id\": \"p1\", \"type\": \"straight\", \"cells\": [[0,1],[1,1]], \"dir\": \"left\" },\n" +
            "    { \"id\": \"p2\", \"type\": \"straight\", \"cells\": [[4,4],[4,5]], \"dir\": \"right\" },\n" +
            "    { \"id\": \"p3\", \"type\": \"straight\", \"cells\": [[5,0],[5,1]], \"dir\": \"up\" },\n" +
            "    { \"id\": \"p4\", \"type\": \"elbow\", \"cells\": [[4,1],[4,2],[5,2]], \"dir\": \"right\" },\n" +
            "    { \"id\": \"p5\", \"type\": \"straight\", \"cells\": [[2,5],[3,5]], \"dir\": \"right\" },\n" +
            "    { \"id\": \"p6\", \"type\": \"straight\", \"cells\": [[2,4],[3,4]], \"dir\": \"down\" }\n" +
            "  ]\n" +
            "}\n";

        public static string Get(int level) => level == 8 ? Level8 : null;
    }

    public sealed class PackEntry
    {
        public int Level;
        public string File;
        public string Json;
        public LevelData Data;
        public SolverResult Result;
        public string Source; // "generated" | "gdd-reference"
        public uint? Seed;
        public int? Attempt;
    }

    /// <summary>Builds, writes and validates level packs (used by the Editor window, the batch-mode CLI and tests).</summary>
    public static class LevelPackBuilder
    {
        /// <summary>Same default pack as tools/generate_levels.mjs.</summary>
        public const string DefaultPack = "1-36,38,42,47,50,54,58,61,62,63,68,74,81,88,95,100,107,116";

        public static string FileName(int level) => "level_" + level.ToString("000") + ".json";

        public static List<int> ParseRange(string spec)
        {
            var set = new SortedSet<int>();
            foreach (var raw in spec.Split(','))
            {
                var part = raw.Trim();
                if (part.Length == 0) continue;
                var bits = part.Split('-');
                int a = int.Parse(bits[0]);
                int b = bits.Length > 1 ? int.Parse(bits[1]) : a;
                for (int i = Math.Min(a, b); i <= Math.Max(a, b); i++) set.Add(i);
            }
            return set.ToList();
        }

        public static List<PackEntry> Build(IEnumerable<int> levels, int attempts = 40, Action<PackEntry> progress = null)
        {
            var list = new List<PackEntry>();
            foreach (int n in levels)
            {
                PackEntry e;
                var reference = GddReferenceLevels.Get(n);
                if (reference != null)
                {
                    var data = LevelJson.Parse(reference);
                    e = new PackEntry { Level = n, Json = reference, Data = data, Result = LevelSolver.Solve(data), Source = "gdd-reference" };
                }
                else
                {
                    var g = LevelGenerator.Generate(n, attempts);
                    e = new PackEntry
                    {
                        Level = n, Json = LevelJson.ToJson(g.Level), Data = g.Level, Result = g.Result, Source = "generated", Seed = g.Seed, Attempt = g.Attempt,
                    };
                }
                e.File = FileName(n);
                list.Add(e);
                progress?.Invoke(e);
            }
            return list;
        }

        /// <summary>Writes level files + manifest.json into every directory (stale level_*.json files are removed).</summary>
        public static void Write(IReadOnlyList<PackEntry> pack, params string[] directories)
        {
            string manifest = ManifestJson(pack);
            var keep = new HashSet<string>(pack.Select(p => p.File));
            foreach (var dir in directories)
            {
                Directory.CreateDirectory(dir);
                foreach (var f in Directory.GetFiles(dir, "level_*.json"))
                    if (!keep.Contains(Path.GetFileName(f))) File.Delete(f);
                foreach (var e in pack) File.WriteAllText(Path.Combine(dir, e.File), e.Json, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest, new UTF8Encoding(false));
            }
        }

        public static string ManifestJson(IReadOnlyList<PackEntry> pack)
        {
            var levels = new List<object>();
            foreach (var e in pack)
            {
                var m = e.Result.Metrics;
                var mech = new Dictionary<string, object>();
                foreach (var kv in m.Mechanics) mech[kv.Key.ToJson()] = kv.Value;
                levels.Add(new Dictionary<string, object>
                {
                    ["level"] = e.Level,
                    ["file"] = e.File,
                    ["source"] = e.Source,
                    ["seed"] = e.Seed.HasValue ? (object)(long)e.Seed.Value : null,
                    ["attempt"] = e.Attempt,
                    ["grid"] = new List<object> { e.Data.Width, e.Data.Height },
                    ["theme"] = Progression.ThemeFor(e.Level),
                    ["rank"] = Progression.RankFor(e.Level),
                    ["breather"] = Progression.IsBreather(e.Level),
                    ["targetDifficulty"] = DifficultyCurve.Target(e.Level),
                    ["difficultyScore"] = m.DifficultyScore,
                    ["pipeCount"] = m.PipeCount,
                    ["waterPipeCount"] = m.WaterPipeCount,
                    ["maxChainDepth"] = m.MaxChainDepth,
                    ["initialExitableCount"] = m.InitialExitableCount,
                    ["deceptiveArrowRatio"] = m.DeceptiveArrowRatio,
                    ["solutionTaps"] = m.SolutionTaps,
                    ["mechanics"] = mech,
                    ["solution"] = e.Result.Solution.Select(s => (object)s.PipeId).ToList(),
                });
            }
            var root = new Dictionary<string, object>
            {
                ["generatedBy"] = "SuUstasi.LevelGen.LevelPackBuilder",
                ["algorithm"] = "reverse-build + deterministic solver",
                ["levels"] = levels,
            };
            return MiniJson.Serialize(root, true) + "\n";
        }

        /// <summary>Validates every level_*.json in a directory. Returns human-readable failures (empty = all good).</summary>
        public static List<string> ValidateDirectory(string dir, out int checkedCount)
        {
            var failures = new List<string>();
            checkedCount = 0;
            if (!Directory.Exists(dir))
            {
                failures.Add("directory not found: " + dir);
                return failures;
            }
            var seen = new HashSet<PipeType>();
            foreach (var path in Directory.GetFiles(dir, "level_*.json").OrderBy(p => p, StringComparer.Ordinal))
            {
                checkedCount++;
                string f = Path.GetFileName(path);
                string text = File.ReadAllText(path);
                if (!LevelJson.TryParse(text, out var level, out var errors))
                {
                    failures.Add(f + ": " + string.Join("; ", errors));
                    continue;
                }
                var res = LevelSolver.Solve(level);
                if (!res.Solvable) failures.Add(f + ": NOT solvable");
                else if (!res.Perfect) failures.Add(f + ": perfect play still loses " + res.LeaksLost + " lives");
                if (LevelSolver.Replay(level, res.Solution.Select(s => s.PipeId), out _) != SessionStatus.Won) failures.Add(f + ": solution replay does not win");
                var reference = GddReferenceLevels.Get(level.Level);
                if (reference != null && reference != text.Replace("\r\n", "\n")) failures.Add(f + ": differs from the GDD reference");
                foreach (var kv in res.Metrics.Mechanics) seen.Add(kv.Key);
            }
            foreach (var t in PipeTypes.All)
                if (checkedCount >= 20 && !seen.Contains(t)) failures.Add("pack never uses pipe type " + t.ToJson());
            return failures;
        }
    }
}
