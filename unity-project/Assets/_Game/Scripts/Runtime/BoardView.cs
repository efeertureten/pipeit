using System.Collections.Generic;
using SuUstasi.Core;
using UnityEngine;

namespace SuUstasi.Runtime
{
    /// <summary>Theme colours for the board frame and tiles (cycle every 20 levels).</summary>
    public struct BoardTheme
    {
        public Color Frame, Edge, Cell;

        public static BoardTheme For(string theme)
        {
            switch (theme)
            {
                case "Mutfak": return Make("#EBBE9E", "#B9785A", "#FBE6D4");
                case "Apartman Bodrumu": return Make("#D2C0AB", "#8F7A66", "#F1E6D7");
                case "Fabrika": return Make("#C4CAD1", "#7D8792", "#EEF0F3");
                case "Şehir Kanalı": return Make("#BFD2B5", "#7C946F", "#EAF1E4");
                case "Arıtma Tesisi": return Make("#AFD6D9", "#5F9EA3", "#E5F4F5");
                default: return Make("#E7C9A1", "#B8875F", "#F8E6CA"); // Banyo
            }
        }

        private static BoardTheme Make(string f, string e, string c) =>
            new BoardTheme { Frame = PaletteColors.Hex(f), Edge = PaletteColors.Hex(e), Cell = PaletteColors.Hex(c) };
    }

    /// <summary>Board frame, tiles, tank, pipe views, swept-path hint, spray particles and the FTUE pointer.</summary>
    public sealed class BoardView : MonoBehaviour
    {
        private readonly Dictionary<string, PipeView> _pipes = new Dictionary<string, PipeView>();
        private readonly List<SpriteRenderer> _sweep = new List<SpriteRenderer>();
        private readonly Dictionary<int, SpriteRenderer> _sweepByCell = new Dictionary<int, SpriteRenderer>();
        private readonly List<Particle> _particles = new List<Particle>();
        private float _sweepT0 = -9;
        private TankView _tank;
        private Transform _hand;
        private int _width, _height;

        private sealed class Particle
        {
            public Transform T;
            public SpriteRenderer R;
            public Vector3 V;
            public float Life, Max;
        }

        public TankView Tank => _tank;
        public int Width => _width;
        public int Height => _height;

        public void Build(GameSession session, BoardTheme theme)
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                var child = transform.GetChild(i).gameObject;
                child.SetActive(false);
                Destroy(child);
            }
            _pipes.Clear();
            _sweep.Clear();
            _sweepByCell.Clear();
            _particles.Clear();
            var board = session.Board;
            _width = board.Width;
            _height = board.Height;

            Tile(Vector3.zero, new Vector2(_width + 0.4f, _height + 0.4f), theme.Edge, -30);
            Tile(Vector3.zero, new Vector2(_width + 0.3f, _height + 0.3f), theme.Frame, -29);
            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    if (board.IsTank(x, y)) continue;
                    Tile(CellLocal(new Vector2Int(x, y)), Vector2.one * 0.91f, theme.Cell, -28);
                    var s = Tile(CellLocal(new Vector2Int(x, y)), Vector2.one * 0.82f, PaletteColors.WithAlpha(PaletteColors.Hex(Palette.Error), 0f), -27);
                    s.name = "sweep_" + x + "_" + y;
                    _sweep.Add(s);
                    _sweepByCell[y * _width + x] = s;
                }
            }

            var tankGo = new GameObject("Tank");
            tankGo.transform.SetParent(transform, false);
            _tank = tankGo.AddComponent<TankView>();
            _tank.Build(this, board.Tank);

            // key/lock pair colours
            var pairTags = new Dictionary<string, Color>();
            string[] tags = { Palette.Error, "#8E5BD9", "#2F9C9A", Palette.Orange };
            int t = 0;
            foreach (var p in board.Pipes)
                if (p.Type == PipeType.KeyValve && p.TargetId != null) pairTags[p.TargetId] = PaletteColors.Hex(tags[t++ % tags.Length]);

            foreach (var p in board.Pipes)
            {
                var go = new GameObject("Pipe_" + p.Id);
                go.transform.SetParent(transform, false);
                var view = go.AddComponent<PipeView>();
                Color? tag = null;
                if (p.Type == PipeType.Locked && pairTags.TryGetValue(p.Id, out var lt)) tag = lt;
                if (p.Type == PipeType.KeyValve && p.TargetId != null && pairTags.TryGetValue(p.TargetId, out var kt)) tag = kt;
                view.Init(this, p, session.HasLeaking, tag);
                _pipes[p.Id] = view;
            }

            _hand = new GameObject("FtueHand").transform;
            _hand.SetParent(transform, false);
            var ring = _hand.gameObject.AddComponent<SpriteRenderer>();
            ring.sprite = ProceduralSprites.Circle;
            ring.color = new Color(1, 1, 1, 0.6f);
            ring.sortingOrder = 400;
            _hand.gameObject.SetActive(false);
        }

        private SpriteRenderer Tile(Vector3 pos, Vector2 size, Color color, int order)
        {
            var go = new GameObject("tile");
            go.transform.SetParent(transform, false);
            go.transform.localPosition = pos;
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = ProceduralSprites.RoundedRect;
            sr.drawMode = SpriteDrawMode.Sliced;
            sr.size = size;
            sr.color = color;
            sr.sortingOrder = order;
            return sr;
        }

        /// <summary>Grid (x right, y DOWN) → board-local position (y up).</summary>
        public Vector3 CellLocal(Vector2Int c) => new Vector3(c.x - _width / 2f + 0.5f, -(c.y - _height / 2f + 0.5f), 0);

        public bool TryGetCell(Vector3 world, out Vector2Int cell)
        {
            var local = transform.InverseTransformPoint(world);
            int x = Mathf.FloorToInt(local.x + _width / 2f);
            int y = Mathf.FloorToInt(-local.y + _height / 2f);
            cell = new Vector2Int(x, y);
            return x >= 0 && y >= 0 && x < _width && y < _height;
        }

        /// <summary>Distance (in cells) a pipe must travel to fully leave the board.</summary>
        public int TravelCells(Pipe p, Direction dir)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            foreach (var c in p.Cells)
            {
                minX = Mathf.Min(minX, c.x); maxX = Mathf.Max(maxX, c.x);
                minY = Mathf.Min(minY, c.y); maxY = Mathf.Max(maxY, c.y);
            }
            switch (dir)
            {
                case Direction.Right: return _width - minX + 1;
                case Direction.Left: return maxX + 2;
                case Direction.Down: return _height - minY + 1;
                default: return maxY + 2;
            }
        }

        public PipeView ViewFor(string id) => id != null && _pipes.TryGetValue(id, out var v) ? v : null;
        public IEnumerable<PipeView> Pipes => _pipes.Values;

        /// <summary>Red swept-path hint after a blocked tap.</summary>
        public void ShowSweep(IEnumerable<Vector2Int> cells)
        {
            foreach (var s in _sweep) s.color = PaletteColors.WithAlpha(s.color, 0);
            foreach (var c in cells)
                if (_sweepByCell.TryGetValue(c.y * _width + c.x, out var s)) s.color = PaletteColors.WithAlpha(PaletteColors.Hex(Palette.Error), 0.3f);
            _sweepT0 = Time.time;
        }

        public void ShowHand(string pipeId)
        {
            var v = ViewFor(pipeId);
            _hand.gameObject.SetActive(v != null);
            if (v != null) _hand.localPosition = CellLocal(v.Pipe.Path[v.Pipe.Path.Count / 2]);
        }

        /// <summary>Water droplets from the leading edge of a sliding pipe.</summary>
        public void Spray(Pipe p, Vector3 offset, Direction dir)
        {
            if (Random.value > 0.8f) return;
            var d = dir == Direction.Right ? Vector3.right : dir == Direction.Left ? Vector3.left : dir == Direction.Up ? Vector3.up : Vector3.down;
            var lead = p.Cells[0];
            var step = dir.ToOffset();
            foreach (var c in p.Cells)
                if (c.x * step.x + c.y * step.y > lead.x * step.x + lead.y * step.y) lead = c;
            var origin = CellLocal(lead) + offset + d * 0.5f;
            Burst(origin, d, PaletteColors.Hex(Random.value < 0.5f ? Palette.Water : "#8FD0FF"), 1, 3.5f);
        }

        public void Burst(Pipe p, Color color, int count, float power)
        {
            for (int i = 0; i < count; i++)
            {
                var c = p.Cells[i % p.Cells.Count];
                float a = Random.value * Mathf.PI * 2;
                Burst(CellLocal(c), new Vector3(Mathf.Cos(a), Mathf.Sin(a)), color, 1, (1.5f + Random.value * 3f) * power);
            }
        }

        private void Burst(Vector3 origin, Vector3 dir, Color color, int count, float speed)
        {
            for (int i = 0; i < count; i++)
            {
                var go = new GameObject("drop");
                go.transform.SetParent(transform, false);
                go.transform.localPosition = origin;
                float s = 0.07f + Random.value * 0.08f;
                go.transform.localScale = new Vector3(s, s * 1.2f, 1);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = ProceduralSprites.Circle;
                sr.color = color;
                sr.sortingOrder = 300;
                var v = dir * speed + new Vector3((Random.value - 0.5f) * 2.5f, (Random.value - 0.5f) * 2.5f + 1.2f, 0);
                _particles.Add(new Particle { T = go.transform, R = sr, V = v, Life = 0.6f, Max = 0.6f });
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = _particles.Count - 1; i >= 0; i--)
            {
                var p = _particles[i];
                p.Life -= dt;
                if (p.Life <= 0 || p.T == null)
                {
                    if (p.T != null) Destroy(p.T.gameObject);
                    _particles.RemoveAt(i);
                    continue;
                }
                p.V += Vector3.down * 14f * dt;
                p.T.localPosition += p.V * dt;
                p.R.color = PaletteColors.WithAlpha(p.R.color, Mathf.Min(1, p.Life / p.Max * 1.6f));
            }

            float k = (Time.time - _sweepT0) / 0.75f;
            if (k >= 0 && k <= 1.05f)
                foreach (var s in _sweep)
                    if (s.color.a > 0) s.color = PaletteColors.WithAlpha(s.color, Mathf.Max(0, 0.3f * (1 - k)));

            if (_hand != null && _hand.gameObject.activeSelf)
            {
                float pulse = (Mathf.Sin(Time.time * 5) + 1) / 2;
                _hand.localScale = Vector3.one * (0.5f + 0.4f * pulse);
                _hand.GetComponent<SpriteRenderer>().color = new Color(1, 1, 1, 0.85f - 0.5f * pulse);
            }
        }
    }
}
