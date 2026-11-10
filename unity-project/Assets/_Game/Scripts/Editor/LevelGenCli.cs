using System;
using System.Collections.Generic;
using System.IO;
using SuUstasi.LevelGen;
using UnityEditor;
using UnityEngine;

namespace SuUstasi.EditorTools
{
    /// <summary>
    /// Batch-mode entry points (CI):
    ///   Unity -batchmode -quit -projectPath unity-project -executeMethod SuUstasi.EditorTools.LevelGenCli.GeneratePack [-levels 1-36,61] [-attempts 40] [-webOut ../web-playable/levels]
    ///   Unity -batchmode -quit -projectPath unity-project -executeMethod SuUstasi.EditorTools.LevelGenCli.ValidatePack
    /// Exit code 0 = success, 1 = failure.
    /// </summary>
    public static class LevelGenCli
    {
        public static void GeneratePack()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                string range = Arg(args, "-levels") ?? LevelPackBuilder.DefaultPack;
                int attempts = int.TryParse(Arg(args, "-attempts"), out var a) ? a : 40;
                var dirs = new List<string> { LevelGeneratorWindow.ResourcesLevels };
                var web = Arg(args, "-webOut");
                if (web != null) dirs.Add(Path.GetFullPath(Path.Combine(Application.dataPath, "..", web)));
                var pack = LevelPackBuilder.Build(LevelPackBuilder.ParseRange(range), attempts,
                    e => Debug.Log($"[LevelGen] {e.File} difficulty {e.Result.Metrics.DifficultyScore} depth {e.Result.Metrics.MaxChainDepth}"));
                LevelPackBuilder.Write(pack, dirs.ToArray());
                AssetDatabase.Refresh();
                Debug.Log("[LevelGen] wrote " + pack.Count + " levels");
                EditorApplication.Exit(0);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                EditorApplication.Exit(1);
            }
        }

        public static void ValidatePack()
        {
            var failures = LevelPackBuilder.ValidateDirectory(LevelGeneratorWindow.ResourcesLevels, out int n);
            foreach (var f in failures) Debug.LogError("[LevelPack] " + f);
            Debug.Log("[LevelPack] checked " + n + " levels, " + failures.Count + " problem(s)");
            EditorApplication.Exit(failures.Count == 0 ? 0 : 1);
        }

        private static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
