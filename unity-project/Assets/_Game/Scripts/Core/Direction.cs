using System;
using UnityEngine;

namespace SuUstasi.Core
{
    /// <summary>Nozzle direction. Declaration order is clockwise (up → right → down → left).</summary>
    public enum Direction
    {
        Up = 0,
        Right = 1,
        Down = 2,
        Left = 3,
    }

    public static class DirectionExtensions
    {
        /// <summary>All directions in clockwise order (same order as the JS core's DIRS).</summary>
        public static readonly Direction[] All = { Direction.Up, Direction.Right, Direction.Down, Direction.Left };

        /// <summary>Grid offset. The grid's y axis grows DOWN, so Up is (0,-1).</summary>
        public static Vector2Int ToOffset(this Direction d)
        {
            switch (d)
            {
                case Direction.Up: return new Vector2Int(0, -1);
                case Direction.Right: return new Vector2Int(1, 0);
                case Direction.Down: return new Vector2Int(0, 1);
                default: return new Vector2Int(-1, 0);
            }
        }

        public static Direction RotateCW(this Direction d) => (Direction)(((int)d + 1) % 4);
        public static Direction RotateCCW(this Direction d) => (Direction)(((int)d + 3) % 4);

        public static string ToJson(this Direction d)
        {
            switch (d)
            {
                case Direction.Up: return "up";
                case Direction.Right: return "right";
                case Direction.Down: return "down";
                default: return "left";
            }
        }

        public static bool TryParse(string s, out Direction d)
        {
            switch (s)
            {
                case "up": d = Direction.Up; return true;
                case "right": d = Direction.Right; return true;
                case "down": d = Direction.Down; return true;
                case "left": d = Direction.Left; return true;
                default: d = Direction.Up; return false;
            }
        }

        public static Direction Parse(string s)
        {
            if (!TryParse(s, out var d)) throw new FormatException("Unknown direction '" + s + "'");
            return d;
        }
    }
}
