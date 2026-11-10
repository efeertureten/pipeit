using System.Collections.Generic;
using System.IO;
using System.Linq;
using SuUstasi.Core;
using SuUstasi.LevelGen;
using UnityEditor;
using UnityEngine;

namespace SuUstasi.EditorTools
{
    /// <summary>Su Ustası ▸ Level Generator — generate, preview and validate reverse-built level packs.</summary>
    public sealed class LevelGeneratorWindow : EditorWindow
    {
        private string _range = LevelPackBuilder.DefaultPack;
        private int _attempts = 40;
        private bool _writeWeb = true;
        private int _previewLevel = 8;
        private LevelData _preview;
        private SolverResult _previewResult;
        private Vector2 _scroll;
        private string _log = "";

        public static string ResourcesLevels => Path.Combine(Application.dataPath, "_Game", "Resources", "Levels");
        public static string WebLevels => Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "web-playable", "levels"));

        [MenuItem("Su Ustası/Level Generator", priority = 0)]
        public static void Open() => GetWindow<LevelGeneratorWindow>("Level Generator").minSize = new Vector2(420, 560);

        [MenuItem("Su Ustası/Validate Level Pack", priority = 1)]
        public static void ValidateMenu()
        {
            var failures = LevelPackBuilder.ValidateDirectory(ResourcesLevels, out int n);
            if (failures.Count == 0) EditorUtility.DisplayDialog("Su Ustası", n + " levels valid and 100% solvable (zero lives lost).", "OK");
            else
            {
                foreach (var f in failures) Debug.LogError("[LevelPack] " + f);
                EditorUtility.DisplayDialog("Su Ustası", failures.Count + " problem(s) — see Console.", "OK");
            }
        }

        private void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            EditorGUILayout.LabelField("Reverse-Build Generator", EditorStyles.boldLabel);
            _range = EditorGUILayout.TextField(new GUIContent("Levels", "e.g. 1-36,40,61"), _range);
            _attempts = EditorGUILayout.IntSlider("Attempts / level", _attempts, 1, 200);
            _writeWeb = EditorGUILayout.ToggleLeft("Also write ../web-playable/levels (manifest only — run tools/generate_levels.mjs for the file:// bundle)", _writeWeb);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Generate Pack", GUILayout.Height(28))) Generate();
                if (GUILayout.Button("Validate", GUILayout.Height(28))) ValidateMenu();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                _previewLevel = EditorGUILayout.IntField("Level", Mathf.Max(1, _previewLevel));
                if (GUILayout.Button("Generate", GUILayout.Width(80))) Preview(LevelGenerator.Generate(_previewLevel, _attempts).Level);
                if (GUILayout.Button("Load file", GUILayout.Width(80))) LoadPreviewFile();
            }
            if (_preview != null) DrawPreview();
            if (!string.IsNullOrEmpty(_log)) EditorGUILayout.HelpBox(_log, MessageType.Info);
            EditorGUILayout.EndScrollView();
        }

        private void Generate()
        {
            var levels = LevelPackBuilder.ParseRange(_range);
            var dirs = new List<string> { ResourcesLevels };
            if (_writeWeb && Directory.Exists(Path.GetDirectoryName(WebLevels))) dirs.Add(WebLevels);
            int i = 0;
            try
            {
                var pack = LevelPackBuilder.Build(levels, _attempts, e =>
                    EditorUtility.DisplayProgressBar("Su Ustası", "Level " + e.Level + "  difficulty " + e.Result.Metrics.DifficultyScore, ++i / (float)levels.Count));
                LevelPackBuilder.Write(pack, dirs.ToArray());
                _log = "Wrote " + pack.Count + " levels to:\n" + string.Join("\n", dirs);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
            AssetDatabase.Refresh();
        }

        private void LoadPreviewFile()
        {
            var path = EditorUtility.OpenFilePanel("Level JSON", ResourcesLevels, "json");
            if (string.IsNullOrEmpty(path)) return;
            if (LevelJson.TryParse(File.ReadAllText(path), out var level, out var errors)) Preview(level);
            else _log = string.Join("\n", errors);
        }

        private void Preview(LevelData level)
        {
            _preview = level;
            _previewResult = LevelSolver.Solve(level);
            var m = _previewResult.Metrics;
            _log = _previewResult.Valid
                ? $"solvable {_previewResult.Solvable} · perfect {_previewResult.Perfect} · difficulty {m.DifficultyScore} (target {DifficultyCurve.Target(level.Level)})\n" +
                  $"depth {m.MaxChainDepth} · initial exits {m.InitialExitableCount} · deceptive {m.DeceptiveArrowRatio} · taps {m.SolutionTaps}\n" +
                  "solution: " + string.Join(" ", _previewResult.Solution.Select(s => s.ToString()))
                : string.Join("\n", _previewResult.Errors);
        }

        private void DrawPreview()
        {
            const float cell = 34;
            var rect = GUILayoutUtility.GetRect(_preview.Width * cell, _preview.Height * cell, GUILayout.ExpandWidth(false));
            EditorGUI.DrawRect(new Rect(rect.x - 4, rect.y - 4, _preview.Width * cell + 8, _preview.Height * cell + 8), new Color(0.72f, 0.53f, 0.37f));
            var session = new GameSession(_preview);
            var arrows = new Dictionary<Direction, string> { { Direction.Up, "▲" }, { Direction.Right, "▶" }, { Direction.Down, "▼" }, { Direction.Left, "◀" } };
            var style = new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = new Color(0.29f, 0.18f, 0.13f) } };
            for (int y = 0; y < _preview.Height; y++)
            {
                for (int x = 0; x < _preview.Width; x++)
                {
                    var r = new Rect(rect.x + x * cell + 1, rect.y + y * cell + 1, cell - 2, cell - 2);
                    if (session.Board.IsTank(x, y))
                    {
                        EditorGUI.DrawRect(r, new Color(0.21f, 0.64f, 1f));
                        continue;
                    }
                    var p = session.Board.PipeAt(x, y);
                    if (p == null)
                    {
                        EditorGUI.DrawRect(r, new Color(0.97f, 0.9f, 0.79f));
                        continue;
                    }
                    ColorUtility.TryParseHtmlString(Palette.ColorFor(p, session.HasLeaking), out var c);
                    EditorGUI.DrawRect(r, c);
                    GUI.Label(r, arrows[p.Dir], style);
                }
            }
        }
    }
}
