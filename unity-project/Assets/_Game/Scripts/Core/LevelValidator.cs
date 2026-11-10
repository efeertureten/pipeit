using System.Collections.Generic;
using UnityEngine;

namespace SuUstasi.Core
{
    /// <summary>Structural level rules (mirrors core.js validateLevel). Returns an empty list when valid.</summary>
    public static class LevelValidator
    {
        public static List<string> Validate(LevelData level)
        {
            var errors = new List<string>();
            var ids = new HashSet<string>();
            var owner = new Dictionary<Vector2Int, string>();
            var tank = level.Tank;

            for (int i = 0; i < level.Pipes.Count; i++)
            {
                var p = level.Pipes[i];
                string tag = "pipe[" + i + "]" + (p.Id != null ? " \"" + p.Id + "\"" : "");
                if (string.IsNullOrEmpty(p.Id)) errors.Add(tag + ": missing string id");
                else if (!ids.Add(p.Id)) errors.Add(tag + ": duplicate id");

                foreach (var c in p.Cells)
                {
                    if (c.x < 0 || c.y < 0 || c.x >= level.Width || c.y >= level.Height) errors.Add(tag + ": cell [" + c.x + "," + c.y + "] out of bounds");
                    else if (tank.Contains(c.x, c.y)) errors.Add(tag + ": cell [" + c.x + "," + c.y + "] overlaps the tank");
                    else if (owner.TryGetValue(c, out var other)) errors.Add(tag + ": cell [" + c.x + "," + c.y + "] overlaps \"" + other + "\"");
                    else owner[c] = p.Id;
                }

                var kind = ShapeAnalyzer.Analyze(p.Cells, out _);
                int n = p.Cells.Count;
                if (kind == ShapeKind.Invalid) errors.Add(tag + ": cells must form a straight line or an L");
                else if (p.Type == PipeType.Straight && !(kind == ShapeKind.Line && n >= 2 && n <= 5)) errors.Add(tag + ": straight must be a 2–5 cell line");
                else if (p.Type == PipeType.Elbow && !(kind == ShapeKind.L && n >= 3 && n <= 5)) errors.Add(tag + ": elbow must be a 3–5 cell L");
                else if (p.Type == PipeType.RotaryValve && !((kind == ShapeKind.Single || kind == ShapeKind.Line) && n <= 5)) errors.Add(tag + ": rotary_valve must be 1–5 cells in a line");
                else if (p.Type != PipeType.Straight && p.Type != PipeType.Elbow && p.Type != PipeType.RotaryValve
                         && !((kind == ShapeKind.Line && n >= 2) || kind == ShapeKind.L)) errors.Add(tag + ": " + p.Type.ToJson() + " must be a 2–5 cell line or a 3–5 cell L");
                else if (n > 5) errors.Add(tag + ": at most 5 cells");

                if (p.Type == PipeType.Frozen && p.IceCount < 1) errors.Add(tag + ": frozen needs iceCount >= 1");
                if (p.Type == PipeType.Leaking && p.LeakTimerMoves < 1) errors.Add(tag + ": leakTimerMoves must be >= 1");
                if (p.Type == PipeType.KeyValve && p.TargetId == null) errors.Add(tag + ": key_valve needs targetId (the locked pipe it opens)");
            }

            // key ↔ lock links
            var byId = new Dictionary<string, PipeData>();
            foreach (var p in level.Pipes)
                if (p.Id != null && !byId.ContainsKey(p.Id)) byId[p.Id] = p;
            var targeted = new HashSet<string>();
            foreach (var p in level.Pipes)
            {
                if (p.Type != PipeType.KeyValve || p.TargetId == null) continue;
                if (!byId.TryGetValue(p.TargetId, out var target)) errors.Add("key_valve \"" + p.Id + "\": targetId \"" + p.TargetId + "\" not found");
                else if (target.Type != PipeType.Locked) errors.Add("key_valve \"" + p.Id + "\": target \"" + p.TargetId + "\" is not a locked pipe");
                else targeted.Add(target.Id);
            }
            foreach (var p in level.Pipes)
                if (p.Type == PipeType.Locked && !targeted.Contains(p.Id)) errors.Add("locked \"" + p.Id + "\": no key_valve targets it");

            bool anyWater = false;
            foreach (var p in level.Pipes) anyWater |= p.Type.IsWater();
            if (!anyWater) errors.Add("level needs at least one water-bearing pipe");

            // Tank rule: no pipe's exit path may ever intersect tank cells.
            foreach (var p in level.Pipes)
            {
                if (p.Type == PipeType.RotaryValve)
                {
                    bool allCross = true;
                    foreach (var d in DirectionExtensions.All) allCross &= Crosses(level, p, d);
                    if (allCross) errors.Add("rotary_valve \"" + p.Id + "\": every direction crosses the tank");
                }
                else if (Crosses(level, p, p.Dir))
                {
                    errors.Add("pipe \"" + p.Id + "\": exit path toward " + p.Dir.ToJson() + " crosses the tank");
                }
            }
            return errors;
        }

        public static bool Crosses(LevelData level, PipeData p, Direction dir)
        {
            var step = dir.ToOffset();
            foreach (var c in p.Cells)
            {
                for (int x = c.x + step.x, y = c.y + step.y; x >= 0 && y >= 0 && x < level.Width && y < level.Height; x += step.x, y += step.y)
                    if (level.Tank.Contains(x, y)) return true;
            }
            return false;
        }
    }
}
