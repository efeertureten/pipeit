using System.Collections.Generic;
using UnityEngine;

namespace SuUstasi.Core
{
    /// <summary>Result of a swept-path scan.</summary>
    public readonly struct ScanResult
    {
        /// <summary>True when every body cell has an unobstructed ray to the board edge.</summary>
        public bool CanExit { get; }
        /// <summary>Distinct pipes in the swept path, nearest first (ties by pipe index).</summary>
        public List<string> BlockingPipeIds { get; }
        /// <summary>Every non-own cell the body would sweep through, in scan order, deduplicated.</summary>
        public List<Vector2Int> SweptCells { get; }
        /// <summary>A ray touched the tank. Valid levels never do this for a pipe's exit direction.</summary>
        public bool HitsTank { get; }

        public ScanResult(bool canExit, List<string> blockers, List<Vector2Int> swept, bool hitsTank)
        {
            CanExit = canExit;
            BlockingPipeIds = blockers;
            SweptCells = swept;
            HitsTank = hitsTank;
        }
    }

    /// <summary>
    /// Swept-path scanning. Every cell of the tapped pipe casts a ray in the nozzle direction
    /// to the board boundary, ignoring the pipe's own cells — straight and L-shaped (elbow)
    /// pipes slide as one rigid body, so every body cell needs a clear ray.
    /// </summary>
    public static class PathScanner
    {
        public static ScanResult Scan(Board board, Pipe pipe, Direction dir)
        {
            var step = dir.ToOffset();
            var nearest = new Dictionary<int, int>(); // pipe index -> min distance
            var swept = new List<Vector2Int>();
            var sweptSeen = new HashSet<int>();
            bool hitsTank = false;

            foreach (var cell in pipe.Cells)
            {
                int dist = 1;
                for (int x = cell.x + step.x, y = cell.y + step.y; board.InBounds(x, y); x += step.x, y += step.y, dist++)
                {
                    int o = board.OccupantAt(x, y);
                    if (o == pipe.Index) continue;
                    if (o == Board.TankCell) hitsTank = true;
                    else if (o >= 0 && (!nearest.TryGetValue(o, out var prev) || dist < prev)) nearest[o] = dist;
                    int k = y * board.Width + x;
                    if (sweptSeen.Add(k)) swept.Add(new Vector2Int(x, y));
                }
            }

            var order = new List<KeyValuePair<int, int>>(nearest);
            order.Sort((a, b) => a.Value != b.Value ? a.Value.CompareTo(b.Value) : a.Key.CompareTo(b.Key));
            var blockers = new List<string>(order.Count);
            foreach (var kv in order) blockers.Add(board.Pipes[kv.Key].Id);
            return new ScanResult(!hitsTank && blockers.Count == 0, blockers, swept, hitsTank);
        }
    }
}
