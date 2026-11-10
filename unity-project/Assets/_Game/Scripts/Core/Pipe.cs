using System.Collections.Generic;
using UnityEngine;

namespace SuUstasi.Core
{
    /// <summary>Live state of one pipe during a level attempt.</summary>
    public sealed class Pipe
    {
        public string Id { get; }
        /// <summary>Position in the level's pipe list (stable tie-breaker, colour cycling).</summary>
        public int Index { get; }
        public PipeType Type { get; }
        /// <summary>Cells in JSON order (scan order).</summary>
        public IReadOnlyList<Vector2Int> Cells { get; }
        /// <summary>Cells ordered head → (corner) → tail, for rendering.</summary>
        public IReadOnlyList<Vector2Int> Path { get; }
        public ShapeKind Shape { get; }
        public Direction InitialDir { get; }
        public bool IsWater { get; }
        public string ColorOverride { get; }

        public Direction Dir { get; internal set; }
        public bool RustIntact { get; internal set; }
        public int IceCount { get; internal set; }
        public bool IsLocked { get; internal set; }
        /// <summary>key_valve → locked pipe id.</summary>
        public string TargetId { get; }
        public int LeakTimer { get; internal set; }
        public int LeakTimerMax { get; }
        public bool Removed { get; internal set; }
        /// <summary>Rotary valve scheduled to slide out after GameTiming.ValveAutoExitDelay.</summary>
        public bool PendingExit { get; internal set; }

        public int Size => Cells.Count;

        public Pipe(PipeData data, int index)
        {
            Id = data.Id;
            Index = index;
            Type = data.Type;
            Cells = new List<Vector2Int>(data.Cells);
            Shape = ShapeAnalyzer.Analyze(data.Cells, out var path);
            Path = path;
            Dir = data.Dir;
            InitialDir = data.Dir;
            IsWater = data.Type.IsWater();
            RustIntact = data.Type == PipeType.Rusty;
            IceCount = data.Type == PipeType.Frozen ? data.IceCount : 0;
            IsLocked = data.Type == PipeType.Locked;
            TargetId = data.TargetId;
            LeakTimerMax = data.Type == PipeType.Leaking ? data.LeakTimerMoves : 0;
            LeakTimer = LeakTimerMax;
            ColorOverride = data.Color;
        }

        public override string ToString() => Id + "(" + Type.ToJson() + "," + Dir.ToJson() + ")";
    }
}
