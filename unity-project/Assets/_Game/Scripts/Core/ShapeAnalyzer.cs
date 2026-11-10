using System.Collections.Generic;
using UnityEngine;

namespace SuUstasi.Core
{
    public enum ShapeKind
    {
        Invalid,
        Single,
        Line,
        L,
    }

    /// <summary>Classifies a cell set as single / line / L and orders it head-to-tail (mirrors core.js analyzeShape).</summary>
    public static class ShapeAnalyzer
    {
        public static ShapeKind Analyze(IReadOnlyList<Vector2Int> cells, out List<Vector2Int> path)
        {
            path = new List<Vector2Int>();
            int n = cells.Count;
            if (n == 0) return ShapeKind.Invalid;
            var set = new HashSet<Vector2Int>(cells);
            if (set.Count != n) return ShapeKind.Invalid;
            if (n == 1)
            {
                path.Add(cells[0]);
                return ShapeKind.Single;
            }

            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            foreach (var c in cells)
            {
                if (c.x < minX) minX = c.x;
                if (c.x > maxX) maxX = c.x;
                if (c.y < minY) minY = c.y;
                if (c.y > maxY) maxY = c.y;
            }

            if (minY == maxY && maxX - minX == n - 1)
            {
                for (int x = minX; x <= maxX; x++) path.Add(new Vector2Int(x, minY));
                return ShapeKind.Line;
            }
            if (minX == maxX && maxY - minY == n - 1)
            {
                for (int y = minY; y <= maxY; y++) path.Add(new Vector2Int(minX, y));
                return ShapeKind.Line;
            }

            int w = maxX - minX + 1, h = maxY - minY + 1;
            if (w < 2 || h < 2 || w + h - 1 != n) return ShapeKind.Invalid;

            var corners = new[] { new Vector2Int(minX, minY), new Vector2Int(maxX, minY), new Vector2Int(minX, maxY), new Vector2Int(maxX, maxY) };
            foreach (var corner in corners)
            {
                if (!set.Contains(corner)) continue;
                int cx = corner.x, cy = corner.y;
                int ox = cx == minX ? maxX : minX;
                int oy = cy == minY ? maxY : minY;
                bool ok = true;
                var hArm = new List<Vector2Int>();
                var vArm = new List<Vector2Int>();
                for (int x = System.Math.Min(cx, ox); x <= System.Math.Max(cx, ox); x++)
                {
                    if (!set.Contains(new Vector2Int(x, cy))) ok = false;
                    hArm.Add(new Vector2Int(x, cy));
                }
                for (int y = System.Math.Min(cy, oy); y <= System.Math.Max(cy, oy); y++)
                {
                    if (!set.Contains(new Vector2Int(cx, y))) ok = false;
                    vArm.Add(new Vector2Int(cx, y));
                }
                if (!ok) continue;
                // order: horizontal arm end -> corner -> vertical arm end
                if (hArm[0].x == cx) hArm.Reverse();
                if (vArm[0].y != cy) vArm.Reverse();
                path.AddRange(hArm);
                for (int i = 1; i < vArm.Count; i++) path.Add(vArm[i]);
                return ShapeKind.L;
            }
            return ShapeKind.Invalid;
        }
    }
}
