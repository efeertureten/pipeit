using System.Collections.Generic;
using SuUstasi.Core;
using SuUstasi.Core.Json;
using UnityEngine;

namespace SuUstasi.Runtime
{
    /// <summary>One row of Resources/Levels/manifest.json.</summary>
    public sealed class LevelEntry
    {
        public int Level;
        public string File;
        public double DifficultyScore;
        public bool Breather;
    }

    /// <summary>Loads the JSON level pack from Resources/Levels (written by tools/generate_levels.mjs or the Level Generator window).</summary>
    public sealed class LevelRepository
    {
        private readonly List<LevelEntry> _entries = new List<LevelEntry>();
        private readonly Dictionary<string, LevelData> _cache = new Dictionary<string, LevelData>();

        public IReadOnlyList<LevelEntry> Entries => _entries;
        public int Count => _entries.Count;

        public LevelRepository(string folder = "Levels")
        {
            Folder = folder;
            var manifest = Resources.Load<TextAsset>(folder + "/manifest");
            if (manifest != null && MiniJson.Parse(manifest.text) is Dictionary<string, object> root && MiniJson.GetList(root, "levels") is List<object> rows)
            {
                foreach (var row in rows)
                {
                    if (!(row is Dictionary<string, object> o) || !MiniJson.TryGetInt(o, "level", out var n)) continue;
                    var e = new LevelEntry { Level = n, File = MiniJson.GetString(o, "file") };
                    if (o.TryGetValue("difficultyScore", out var d) && d != null) e.DifficultyScore = System.Convert.ToDouble(d);
                    if (o.TryGetValue("breather", out var b) && b is bool bb) e.Breather = bb;
                    _entries.Add(e);
                }
            }
            else
            {
                // No manifest: fall back to whatever level_*.json assets exist.
                foreach (var asset in Resources.LoadAll<TextAsset>(folder))
                {
                    if (!asset.name.StartsWith("level_")) continue;
                    if (int.TryParse(asset.name.Substring(6), out var n)) _entries.Add(new LevelEntry { Level = n, File = asset.name + ".json" });
                }
            }
            _entries.Sort((a, b) => a.Level.CompareTo(b.Level));
        }

        public string Folder { get; }

        public LevelData Load(int index)
        {
            var e = _entries[Mathf.Clamp(index, 0, _entries.Count - 1)];
            if (_cache.TryGetValue(e.File, out var cached)) return cached;
            var asset = Resources.Load<TextAsset>(Folder + "/" + System.IO.Path.GetFileNameWithoutExtension(e.File));
            if (asset == null) throw new System.IO.FileNotFoundException("Resources/" + Folder + "/" + e.File);
            var data = LevelJson.Parse(asset.text);
            _cache[e.File] = data;
            return data;
        }

        public int IndexOfLevel(int levelNumber) => _entries.FindIndex(e => e.Level == levelNumber);
    }
}
