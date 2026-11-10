using System;
using System.Collections.Generic;
using UnityEngine;

namespace SuUstasi.Core
{
    /// <summary>Level schema (see README "Level JSON"). Loaded by LevelJson, checked by LevelValidator.</summary>
    [Serializable]
    public sealed class LevelData
    {
        public int Level;
        public int Width;
        public int Height;
        public TankData Tank;
        public int Leaks = GameRules.DefaultLeaks;
        public List<PipeData> Pipes = new List<PipeData>();
    }

    [Serializable]
    public struct TankData
    {
        public int X;
        public int Y;
        public int W;
        public int H;

        public TankData(int x, int y, int w, int h)
        {
            X = x;
            Y = y;
            W = w;
            H = h;
        }

        public bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;
    }

    [Serializable]
    public sealed class PipeData
    {
        public string Id;
        public PipeType Type;
        public List<Vector2Int> Cells = new List<Vector2Int>();
        public Direction Dir;
        /// <summary>frozen: number of other exits needed before it thaws.</summary>
        public int IceCount;
        /// <summary>key_valve: id of the locked pipe this key opens.</summary>
        public string TargetId;
        /// <summary>leaking: taps allowed before a burst (default 6).</summary>
        public int LeakTimerMoves;
        /// <summary>Optional palette name override ("yellow" / "orange" / "green").</summary>
        public string Color;

        public PipeData() { }

        public PipeData(string id, PipeType type, Direction dir, params Vector2Int[] cells)
        {
            Id = id;
            Type = type;
            Dir = dir;
            Cells = new List<Vector2Int>(cells);
            if (type == PipeType.Leaking) LeakTimerMoves = GameRules.DefaultLeakTimer;
        }
    }
}
