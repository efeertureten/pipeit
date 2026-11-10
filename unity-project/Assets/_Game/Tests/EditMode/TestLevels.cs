using System;
using System.IO;
using SuUstasi.Core;
using UnityEngine;

namespace SuUstasi.Tests
{
    /// <summary>Shared fixtures for the EditMode suite (runs in Unity Test Runner and headless via NUnitLite).</summary>
    public static class TestLevels
    {
        public static readonly TankData Tank2 = new TankData(2, 2, 2, 2);

        public static PipeData P(string id, PipeType type, Direction dir, params int[] xy)
        {
            var p = new PipeData { Id = id, Type = type, Dir = dir };
            for (int i = 0; i < xy.Length; i += 2) p.Cells.Add(new Vector2Int(xy[i], xy[i + 1]));
            if (type == PipeType.Leaking) p.LeakTimerMoves = GameRules.DefaultLeakTimer;
            return p;
        }

        /// <summary>6x6 board with the GDD 2x2 tank at (2,2); level 40 so FTUE is off unless overridden.</summary>
        public static LevelData Make(int level, params PipeData[] pipes)
        {
            var l = new LevelData { Level = level, Width = 6, Height = 6, Tank = Tank2, Leaks = 3 };
            l.Pipes.AddRange(pipes);
            return l;
        }

        public static LevelData Make(params PipeData[] pipes) => Make(40, pipes);

        public static GameSession Session(params PipeData[] pipes) => new GameSession(Make(pipes));

        /// <summary>The GDD Level 8 reference (verbatim JSON).</summary>
        public static LevelData Level8() => LevelJson.Parse(LevelGen.GddReferenceLevels.Level8);

        /// <summary>Folder holding the shipped level_*.json files.</summary>
        public static string LevelsDirectory()
        {
#if UNITY_2017_2_OR_NEWER
            return Path.Combine(Application.dataPath, "_Game", "Resources", "Levels");
#else
            var env = Environment.GetEnvironmentVariable("SU_LEVELS_DIR");
            if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return env;
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                var dir = new DirectoryInfo(start);
                while (dir != null)
                {
                    var candidate = Path.Combine(dir.FullName, "unity-project", "Assets", "_Game", "Resources", "Levels");
                    if (Directory.Exists(candidate)) return candidate;
                    dir = dir.Parent;
                }
            }
            throw new DirectoryNotFoundException("Levels folder not found; set SU_LEVELS_DIR");
#endif
        }
    }
}
