// Headless CLI over the Unity project's engine-free C# (built by tools/run_csharp_tests.mjs).
//   validate   <levelsDir>            C# solver validation of every level_*.json
//   crosscheck <levelsDir>            C# solver metrics == manifest.json (JS) and C# generator output == shipped files
//   generate   <range> <outDir>...    build a pack with the C# generator
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SuUstasi.Core;
using SuUstasi.Core.Json;
using SuUstasi.LevelGen;

public static class HeadlessCli
{
    public static int Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        if (args.Length < 2) { Console.WriteLine("usage: validate|crosscheck <levelsDir> | generate <range> <outDir>..."); return 2; }
        switch (args[0])
        {
            case "validate": return Validate(args[1]);
            case "crosscheck": return CrossCheck(args[1]);
            case "generate": return Generate(args[1], args.Skip(2).ToArray());
            default: Console.WriteLine("unknown command " + args[0]); return 2;
        }
    }

    private static int Validate(string dir)
    {
        var failures = LevelPackBuilder.ValidateDirectory(dir, out int n);
        foreach (var f in failures) Console.WriteLine("  FAIL " + f);
        Console.WriteLine(failures.Count == 0 ? "C# solver: " + n + " levels valid, solvable, zero lives lost ✓" : failures.Count + " problem(s)");
        return failures.Count == 0 ? 0 : 1;
    }

    private static int CrossCheck(string dir)
    {
        var manifest = (Dictionary<string, object>)MiniJson.Parse(File.ReadAllText(Path.Combine(dir, "manifest.json")));
        int metricOk = 0, metricBad = 0, genSame = 0, genDiff = 0;
        foreach (Dictionary<string, object> entry in (List<object>)manifest["levels"])
        {
            string file = (string)entry["file"];
            string text = File.ReadAllText(Path.Combine(dir, file)).Replace("\r\n", "\n");
            var level = LevelJson.Parse(text);
            var res = LevelSolver.Solve(level);
            var m = res.Metrics;
            var diffs = new List<string>();
            void Cmp(string key, double mine)
            {
                double js = Convert.ToDouble(entry[key]);
                if (Math.Abs(js - mine) > 1e-12) diffs.Add(key + " js=" + js + " cs=" + mine);
            }
            Cmp("difficultyScore", m.DifficultyScore);
            Cmp("maxChainDepth", m.MaxChainDepth);
            Cmp("initialExitableCount", m.InitialExitableCount);
            Cmp("deceptiveArrowRatio", m.DeceptiveArrowRatio);
            Cmp("pipeCount", m.PipeCount);
            Cmp("solutionTaps", m.SolutionTaps);
            var jsSolution = string.Join(",", ((List<object>)entry["solution"]).Cast<string>());
            var csSolution = string.Join(",", res.Solution.Select(s => s.PipeId));
            if (jsSolution != csSolution) diffs.Add("solution differs");
            if (diffs.Count == 0) metricOk++;
            else { metricBad++; Console.WriteLine("  METRICS " + file + ": " + string.Join("; ", diffs)); }

            if ((string)entry["source"] == "generated")
            {
                var g = LevelGenerator.Generate(level.Level);
                if (LevelJson.ToJson(g.Level) == text) genSame++;
                else { genDiff++; Console.WriteLine("  GENERATOR " + file + ": C# output differs from the JS-generated file"); }
            }
        }
        Console.WriteLine("solver metrics identical to JS: " + metricOk + "/" + (metricOk + metricBad));
        Console.WriteLine("C# generator byte-identical to JS: " + genSame + "/" + (genSame + genDiff));
        return metricBad == 0 && genDiff == 0 ? 0 : 1;
    }

    private static int Generate(string range, string[] outDirs)
    {
        var pack = LevelPackBuilder.Build(LevelPackBuilder.ParseRange(range), 40,
            e => Console.WriteLine("  " + e.File + "  diff " + e.Result.Metrics.DifficultyScore + "  depth " + e.Result.Metrics.MaxChainDepth));
        if (outDirs.Length > 0) LevelPackBuilder.Write(pack, outDirs);
        return 0;
    }
}
