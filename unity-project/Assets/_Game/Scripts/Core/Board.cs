using System.Collections.Generic;
using UnityEngine;

namespace SuUstasi.Core
{
    /// <summary>
    /// Logical grid: occupancy of every cell (pipe index, empty or the immutable tank).
    /// (0,0) is top-left, x grows right, y grows down.
    /// </summary>
    public sealed class Board
    {
        public const int Empty = -1;
        public const int TankCell = -2;

        private readonly int[] _occupancy;
        private readonly List<Pipe> _pipes;
        private readonly Dictionary<string, Pipe> _byId;

        public int Width { get; }
        public int Height { get; }
        public TankData Tank { get; }
        public IReadOnlyList<Pipe> Pipes => _pipes;

        public Board(LevelData level)
        {
            Width = level.Width;
            Height = level.Height;
            Tank = level.Tank;
            _occupancy = new int[Width * Height];
            for (int i = 0; i < _occupancy.Length; i++) _occupancy[i] = Empty;
            for (int y = Tank.Y; y < Tank.Y + Tank.H; y++)
                for (int x = Tank.X; x < Tank.X + Tank.W; x++)
                    _occupancy[y * Width + x] = TankCell;

            _pipes = new List<Pipe>(level.Pipes.Count);
            _byId = new Dictionary<string, Pipe>();
            for (int i = 0; i < level.Pipes.Count; i++)
            {
                var pipe = new Pipe(level.Pipes[i], i);
                _pipes.Add(pipe);
                _byId[pipe.Id] = pipe;
                foreach (var c in pipe.Cells) _occupancy[c.y * Width + c.x] = i;
            }
        }

        public bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
        public bool InBounds(Vector2Int c) => InBounds(c.x, c.y);
        public bool IsTank(int x, int y) => InBounds(x, y) && _occupancy[y * Width + x] == TankCell;

        /// <summary>Raw occupancy: pipe index, <see cref="Empty"/> or <see cref="TankCell"/>.</summary>
        public int OccupantAt(int x, int y) => _occupancy[y * Width + x];

        public Pipe PipeAt(int x, int y)
        {
            if (!InBounds(x, y)) return null;
            int o = _occupancy[y * Width + x];
            return o >= 0 ? _pipes[o] : null;
        }

        public Pipe PipeAt(Vector2Int c) => PipeAt(c.x, c.y);

        public Pipe Get(string id) => id != null && _byId.TryGetValue(id, out var p) ? p : null;

        public IEnumerable<Pipe> Remaining()
        {
            foreach (var p in _pipes)
                if (!p.Removed) yield return p;
        }

        /// <summary>Frees the pipe's cells immediately — the view animates the slide-out separately.</summary>
        public void Remove(Pipe pipe)
        {
            if (pipe.Removed) return;
            foreach (var c in pipe.Cells) _occupancy[c.y * Width + c.x] = Empty;
            pipe.Removed = true;
            pipe.PendingExit = false;
        }

        public ScanResult Scan(Pipe pipe) => PathScanner.Scan(this, pipe, pipe.Dir);
        public ScanResult Scan(Pipe pipe, Direction dir) => PathScanner.Scan(this, pipe, dir);
    }
}
