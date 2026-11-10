using System;
using System.Collections.Generic;
using System.Text;
using SuUstasi.Core.Json;
using UnityEngine;

namespace SuUstasi.Core
{
    public sealed class LevelFormatException : Exception
    {
        public IReadOnlyList<string> Errors { get; }

        public LevelFormatException(IReadOnlyList<string> errors)
            : base("Invalid level: " + string.Join("; ", errors))
        {
            Errors = errors;
        }
    }

    /// <summary>
    /// JSON level loader/writer for the GDD schema:
    /// { "level", "grid":[w,h], "tank":{x,y,w,h}, "leaks", "pipes":[{id,type,cells,dir,iceCount?,targetId?,leakTimerMoves?}] }
    /// </summary>
    public static class LevelJson
    {
        /// <summary>Parses and fully validates (schema + structural rules). Throws LevelFormatException.</summary>
        public static LevelData Parse(string json)
        {
            if (!TryParse(json, out var level, out var errors)) throw new LevelFormatException(errors);
            return level;
        }

        public static bool TryParse(string json, out LevelData level, out List<string> errors)
        {
            errors = new List<string>();
            level = null;
            object root;
            try
            {
                root = MiniJson.Parse(json);
            }
            catch (FormatException e)
            {
                errors.Add(e.Message);
                return false;
            }
            if (!(root is Dictionary<string, object> o))
            {
                errors.Add("level must be an object");
                return false;
            }

            var data = new LevelData();
            if (!MiniJson.TryGetInt(o, "level", out data.Level) || data.Level < 1) errors.Add("\"level\" must be a positive integer");

            var grid = MiniJson.GetList(o, "grid");
            if (grid == null || grid.Count != 2 || !AsInt(grid[0], out data.Width) || !AsInt(grid[1], out data.Height)
                || data.Width < 3 || data.Height < 3 || data.Width > 16 || data.Height > 16)
            {
                errors.Add("\"grid\" must be [width, height] with 3..16 cells");
                return false;
            }

            var tank = MiniJson.GetObject(o, "tank");
            if (tank == null || !MiniJson.TryGetInt(tank, "x", out var tx) || !MiniJson.TryGetInt(tank, "y", out var ty)
                || !MiniJson.TryGetInt(tank, "w", out var tw) || !MiniJson.TryGetInt(tank, "h", out var th)
                || tw < 1 || th < 1 || tx < 0 || ty < 0 || tx + tw > data.Width || ty + th > data.Height)
            {
                errors.Add("\"tank\" must be {x,y,w,h} inside the grid");
                return false;
            }
            data.Tank = new TankData(tx, ty, tw, th);

            if (o.ContainsKey("leaks"))
            {
                if (!MiniJson.TryGetInt(o, "leaks", out data.Leaks) || data.Leaks < 1 || data.Leaks > GameRules.MaxLeaks)
                    errors.Add("\"leaks\" must be 1.." + GameRules.MaxLeaks);
            }

            var pipes = MiniJson.GetList(o, "pipes");
            if (pipes == null || pipes.Count == 0)
            {
                errors.Add("\"pipes\" must be a non-empty array");
                return false;
            }

            for (int i = 0; i < pipes.Count; i++)
            {
                if (!(pipes[i] is Dictionary<string, object> po))
                {
                    errors.Add("pipe[" + i + "]: must be an object");
                    continue;
                }
                var pd = new PipeData { Id = MiniJson.GetString(po, "id") };
                string tag = "pipe[" + i + "]" + (pd.Id != null ? " \"" + pd.Id + "\"" : "");
                if (string.IsNullOrEmpty(pd.Id)) errors.Add(tag + ": missing string id");
                string type = MiniJson.GetString(po, "type");
                if (!PipeTypes.TryParse(type, out pd.Type)) errors.Add(tag + ": unknown type \"" + type + "\"");
                if (!DirectionExtensions.TryParse(MiniJson.GetString(po, "dir"), out pd.Dir)) errors.Add(tag + ": dir must be one of up/right/down/left");
                var cells = MiniJson.GetList(po, "cells");
                if (cells == null || cells.Count == 0)
                {
                    errors.Add(tag + ": cells must be a non-empty array");
                    continue;
                }
                foreach (var c in cells)
                {
                    if (c is List<object> pair && pair.Count == 2 && AsInt(pair[0], out var cx) && AsInt(pair[1], out var cy))
                        pd.Cells.Add(new Vector2Int(cx, cy));
                    else
                        errors.Add(tag + ": bad cell");
                }
                if (po.ContainsKey("iceCount") && !MiniJson.TryGetInt(po, "iceCount", out pd.IceCount)) errors.Add(tag + ": iceCount must be an integer");
                pd.TargetId = MiniJson.GetString(po, "targetId");
                if (po.ContainsKey("leakTimerMoves"))
                {
                    if (!MiniJson.TryGetInt(po, "leakTimerMoves", out pd.LeakTimerMoves)) errors.Add(tag + ": leakTimerMoves must be an integer");
                }
                else if (pd.Type == PipeType.Leaking)
                {
                    pd.LeakTimerMoves = GameRules.DefaultLeakTimer;
                }
                pd.Color = MiniJson.GetString(po, "color");
                data.Pipes.Add(pd);
            }

            if (errors.Count == 0) errors.AddRange(LevelValidator.Validate(data));
            if (errors.Count > 0) return false;
            level = data;
            return true;
        }

        private static bool AsInt(object v, out int value)
        {
            value = 0;
            if (v is long l && l >= int.MinValue && l <= int.MaxValue) { value = (int)l; return true; }
            return false;
        }

        /// <summary>
        /// Writes the GDD layout (one pipe per line). Byte-identical to web-playable/js/core.js
        /// stringifyLevel, so C#- and JS-generated packs can be diffed.
        /// </summary>
        public static string ToJson(LevelData level)
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"level\": ").Append(level.Level).Append(",\n");
            sb.Append("  \"grid\": [").Append(level.Width).Append(", ").Append(level.Height).Append("],\n");
            var t = level.Tank;
            sb.Append("  \"tank\": { \"x\": ").Append(t.X).Append(", \"y\": ").Append(t.Y).Append(", \"w\": ").Append(t.W).Append(", \"h\": ").Append(t.H).Append(" },\n");
            sb.Append("  \"leaks\": ").Append(level.Leaks).Append(",\n");
            sb.Append("  \"pipes\": [\n");
            for (int i = 0; i < level.Pipes.Count; i++)
            {
                var p = level.Pipes[i];
                sb.Append("    { \"id\": ");
                MiniJson.WriteString(sb, p.Id);
                sb.Append(", \"type\": \"").Append(p.Type.ToJson()).Append("\", \"cells\": [");
                for (int c = 0; c < p.Cells.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append('[').Append(p.Cells[c].x).Append(',').Append(p.Cells[c].y).Append(']');
                }
                sb.Append("], \"dir\": \"").Append(p.Dir.ToJson()).Append('"');
                if (p.Type == PipeType.Frozen) sb.Append(", \"iceCount\": ").Append(p.IceCount);
                if (p.Type == PipeType.KeyValve && p.TargetId != null)
                {
                    sb.Append(", \"targetId\": ");
                    MiniJson.WriteString(sb, p.TargetId);
                }
                if (p.Type == PipeType.Leaking) sb.Append(", \"leakTimerMoves\": ").Append(p.LeakTimerMoves);
                sb.Append(" }");
                sb.Append(i < level.Pipes.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("  ]\n}\n");
            return sb.ToString();
        }
    }
}
