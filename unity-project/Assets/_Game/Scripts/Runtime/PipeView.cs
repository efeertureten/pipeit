using System.Collections.Generic;
using SuUstasi.Core;
using UnityEngine;

namespace SuUstasi.Runtime
{
    /// <summary>
    /// Visual for one pipe, assembled from tinted procedural sprites (outline, body, shading, flanges,
    /// direction chevrons + per-mechanic decorations). Purely cosmetic: the logical Board already freed
    /// the cells when an exit animation starts, so input is never blocked.
    /// </summary>
    public sealed class PipeView : MonoBehaviour
    {
        private const float T = 0.5f, Ow = 0.06f, Ext = 0.42f;
        private const int OrderBase = 10;

        private BoardView _board;
        private bool _hasLeaking;
        private Color? _pairTag;
        private Transform _visual, _spin, _chain;
        private readonly List<SpriteRenderer> _renderers = new List<SpriteRenderer>();
        private readonly List<Color> _baseColors = new List<Color>();
        private readonly List<SpriteRenderer> _glow = new List<SpriteRenderer>();
        private readonly List<Transform> _drips = new List<Transform>();
        private TextMesh _badge;
        private Transform _badgeRoot;

        private float _exitT0 = -1, _exitDur;
        private Direction _exitDir;
        private bool _hammered;
        private float _shakeT0 = -9, _flashT0 = -9, _rotT0 = -9, _chainT0 = -9, _badgeT0 = -9;
        private float _angleFrom, _angleTo;
        private int _orderBoost;

        public Pipe Pipe { get; private set; }
        public bool Finished { get; private set; }
        public bool XRay { get; set; }
        public bool Pending { get; set; }

        public void Init(BoardView board, Pipe pipe, bool hasLeaking, Color? pairTag)
        {
            _board = board;
            Pipe = pipe;
            _hasLeaking = hasLeaking;
            _pairTag = pairTag;
            _angleTo = _angleFrom = Angle(pipe.Dir);
            Rebuild();
        }

        private static float Now => Time.time;

        public static float Angle(Direction d) => d == Direction.Right ? 0 : d == Direction.Up ? 90 : d == Direction.Left ? 180 : -90;
        private static Vector3 WorldDir(Direction d) => d == Direction.Right ? Vector3.right : d == Direction.Left ? Vector3.left : d == Direction.Up ? Vector3.up : Vector3.down;

        // ------------------------------------------------------------------ build
        /// <summary>Recreates every sprite (called on state changes: rust broken, thawed, unlocked).</summary>
        public void Rebuild()
        {
            if (_visual != null) Destroy(_visual.gameObject);
            _renderers.Clear();
            _baseColors.Clear();
            _glow.Clear();
            _drips.Clear();
            _badge = null;
            _visual = new GameObject("visual").transform;
            _visual.SetParent(transform, false);
            var baseColor = Pipe.ColorOverride != null && Pipe.ColorOverride.StartsWith("#")
                ? PaletteColors.Hex(Pipe.ColorOverride)
                : PaletteColors.Hex(Palette.ColorFor(Pipe, _hasLeaking));
            if (Pipe.Shape == ShapeKind.Single) BuildValve(baseColor);
            else BuildTube(baseColor);
            BuildBadge();
        }

        private SpriteRenderer Add(Transform parent, Sprite sprite, Vector3 pos, Vector2 size, Color color, int order, float rotation = 0, bool sliced = false, bool glow = false)
        {
            var go = new GameObject(sprite == ProceduralSprites.Circle ? "circle" : "rect");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0, 0, rotation);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sprite = sprite;
            sr.color = color;
            sr.sortingOrder = OrderBase + order;
            if (sliced)
            {
                sr.drawMode = SpriteDrawMode.Sliced;
                sr.size = size;
            }
            else
            {
                go.transform.localScale = new Vector3(size.x, size.y, 1);
            }
            if (glow) _glow.Add(sr);
            else
            {
                _renderers.Add(sr);
                _baseColors.Add(color);
            }
            return sr;
        }

        private void Rect(Transform parent, Vector3 center, float along, float across, bool horizontal, Color color, int order, bool glow = false)
        {
            var size = horizontal ? new Vector2(along, across) : new Vector2(across, along);
            Add(parent, ProceduralSprites.RoundedRect, center, size, color, order, 0, true, glow);
        }

        private void BuildTube(Color baseColor)
        {
            var ink = PaletteColors.Hex(Palette.Ink);
            var path = Pipe.Path;
            int last = path.Count - 1;
            var pos = new List<Vector3>();
            foreach (var c in path) pos.Add(_board.CellLocal(c));

            // straight runs between direction changes
            var runs = new List<Vector2Int>();
            int start = 0;
            for (int i = 2; i <= last; i++)
            {
                var d0 = path[i - 1] - path[i - 2];
                var d1 = path[i] - path[i - 1];
                if (d0 != d1)
                {
                    runs.Add(new Vector2Int(start, i - 1));
                    start = i - 1;
                }
            }
            runs.Add(new Vector2Int(start, last));

            foreach (var run in runs)
            {
                Vector3 a = pos[run.x], b = pos[run.y];
                var u = (b - a).normalized;
                var s = run.x == 0 ? a - u * Ext : a;
                var e = run.y == last ? b + u * Ext : b;
                bool horiz = Mathf.Abs(u.x) > 0.5f;
                var mid = (s + e) / 2;
                float len = (e - s).magnitude;
                Rect(_visual, mid, len + Ow * 2 + 0.14f, T + Ow * 2 + 0.14f, horiz, Color.white, -1, true); // X-Ray glow
                Rect(_visual, mid, len + Ow * 2, T + Ow * 2, horiz, ink, 0);
                Rect(_visual, mid, len, T, horiz, baseColor, 1);
                var shadowOff = horiz ? new Vector3(0, -T * 0.22f) : new Vector3(T * 0.22f, 0);
                var lightOff = horiz ? new Vector3(0, T * 0.24f) : new Vector3(-T * 0.24f, 0);
                Rect(_visual, mid + shadowOff, Mathf.Max(0.05f, len - T * 0.5f), T * 0.22f, horiz, PaletteColors.Shade(baseColor, -0.16f), 2);
                Rect(_visual, mid + lightOff, Mathf.Max(0.05f, len - T * 0.6f), T * 0.14f, horiz, PaletteColors.Shade(baseColor, 0.42f), 2);
            }
            // rounded elbow corners
            for (int i = 1; i < last; i++)
            {
                if (path[i] - path[i - 1] == path[i + 1] - path[i]) continue;
                Add(_visual, ProceduralSprites.Circle, pos[i], Vector2.one * (T + Ow * 2), ink, 0);
                Add(_visual, ProceduralSprites.Circle, pos[i], Vector2.one * T, baseColor, 1);
            }
            // joint rings + end flanges
            for (int i = 0; i < last; i++)
            {
                bool horiz = path[i].y == path[i + 1].y;
                var m = (pos[i] + pos[i + 1]) / 2;
                Rect(_visual, m, 0.09f + Ow * 2, T * 1.16f + Ow * 2, horiz, ink, 3);
                Rect(_visual, m, 0.09f, T * 1.16f, horiz, PaletteColors.Shade(baseColor, -0.06f), 4);
            }
            void Flange(Vector3 p, Vector3 u)
            {
                bool horiz = Mathf.Abs(u.x) > 0.5f;
                var c = p + u * (Ext - 0.06f);
                Rect(_visual, c, 0.13f + Ow * 2, T * 1.28f + Ow * 2, horiz, ink, 3);
                Rect(_visual, c, 0.13f, T * 1.28f, horiz, PaletteColors.Shade(baseColor, -0.06f), 4);
            }
            Flange(pos[0], (pos[0] - pos[1]).normalized);
            Flange(pos[last], (pos[last] - pos[last - 1]).normalized);

            // direction chevrons
            var arrowColor = PaletteColors.Shade(baseColor, -0.38f);
            for (int i = 0; i <= last; i++)
            {
                bool corner = i > 0 && i < last && path[i] - path[i - 1] != path[i + 1] - path[i];
                if (corner || (path.Count >= 4 && i % 2 == 1)) continue;
                Add(_visual, ProceduralSprites.Triangle, pos[i], Vector2.one * 0.3f, arrowColor, 5, Angle(Pipe.Dir));
            }

            BuildDecorations(pos, baseColor);
        }

        private void BuildDecorations(List<Vector3> pos, Color baseColor)
        {
            var rnd = new System.Random(Pipe.Index * 7919 + 7);
            var ink = PaletteColors.Hex(Palette.Ink);
            Vector3 Along(float f)
            {
                float k = f * (pos.Count - 1);
                int a = Mathf.Clamp(Mathf.FloorToInt(k), 0, pos.Count - 1), b = Mathf.Min(pos.Count - 1, a + 1);
                return Vector3.Lerp(pos[a], pos[b], k - a);
            }
            bool HorizAt(float f)
            {
                int a = Mathf.Clamp(Mathf.FloorToInt(f * (pos.Count - 1)), 0, pos.Count - 2);
                return Mathf.Abs(pos[a].y - pos[a + 1].y) < 0.01f;
            }
            int n = Pipe.Size * 2;
            switch (Pipe.Type)
            {
                case PipeType.Rusty when Pipe.RustIntact:
                    for (int i = 0; i < n + 2; i++)
                    {
                        var p = Along((i + 0.5f) / (n + 2)) + new Vector3((float)(rnd.NextDouble() - 0.5) * T * 0.7f, (float)(rnd.NextDouble() - 0.5) * T * 0.5f);
                        float s = T * (0.14f + (float)rnd.NextDouble() * 0.16f);
                        Add(_visual, ProceduralSprites.Circle, p, new Vector2(s, s * 0.65f), i % 2 == 0 ? new Color(0.35f, 0.18f, 0.08f, 0.6f) : new Color(0.8f, 0.48f, 0.25f, 0.5f), 6, (float)rnd.NextDouble() * 180);
                    }
                    break;
                case PipeType.Dry:
                    for (int i = 0; i < n + 3; i++)
                    {
                        var p = Along((i + 0.5f) / (n + 3)) + new Vector3((float)(rnd.NextDouble() - 0.5) * T * 0.7f, (float)(rnd.NextDouble() - 0.5) * T * 0.5f);
                        Add(_visual, ProceduralSprites.Circle, p, Vector2.one * 0.04f, new Color(0.27f, 0.3f, 0.36f, 0.4f), 6);
                    }
                    break;
                case PipeType.Frozen when Pipe.IceCount > 0:
                    for (int i = 0; i < n + 1; i++)
                    {
                        float f = (i + 0.5f) / (n + 1);
                        bool horiz = HorizAt(f);
                        var p = Along(f) + (horiz ? new Vector3(0, T * 0.42f) : new Vector3(-T * 0.42f, 0));
                        Add(_visual, ProceduralSprites.Circle, p, horiz ? new Vector2(T * 0.5f, T * 0.26f) : new Vector2(T * 0.26f, T * 0.5f), Color.white, 6);
                        if (horiz && i % 2 == 0)
                            Add(_visual, ProceduralSprites.Triangle, Along(f) + new Vector3(0, -T * 0.62f), new Vector2(T * 0.5f, T * 0.25f), new Color(1, 1, 1, 0.95f), 6, -90);
                    }
                    break;
                case PipeType.Leaking:
                    for (int i = 0; i < 2; i++)
                    {
                        var drip = Add(_visual, ProceduralSprites.Circle, Along(0.3f + 0.4f * i), new Vector2(0.1f, 0.14f), PaletteColors.Hex(Palette.Water), 6);
                        _drips.Add(drip.transform);
                    }
                    break;
                case PipeType.Locked when Pipe.IsLocked:
                    _chain = new GameObject("chain").transform;
                    _chain.SetParent(_visual, false);
                    for (int i = 0; i < Pipe.Size * 3; i++)
                    {
                        float f = (i + 0.5f) / (Pipe.Size * 3);
                        bool horiz = HorizAt(f);
                        var off = horiz ? new Vector3(0, i % 2 == 0 ? T * 0.12f : -T * 0.12f) : new Vector3(i % 2 == 0 ? T * 0.12f : -T * 0.12f, 0);
                        float rot = (horiz ? 0 : 90) + (i % 2 == 0 ? 30 : -30);
                        Add(_chain, ProceduralSprites.RoundedRect, Along(f) + off, new Vector2(T * 0.46f, T * 0.24f), ink, 7, rot, true);
                        Add(_chain, ProceduralSprites.RoundedRect, Along(f) + off, new Vector2(T * 0.34f, T * 0.12f), new Color(0.6f, 0.63f, 0.67f), 8, rot, true);
                    }
                    var lockPos = Along(0.5f) + new Vector3(0, -T * 0.1f);
                    Add(_chain, ProceduralSprites.Circle, lockPos + new Vector3(0, T * 0.24f), Vector2.one * T * 0.42f, ink, 8);
                    Add(_chain, ProceduralSprites.RoundedRect, lockPos, new Vector2(T * 0.66f, T * 0.54f), ink, 9, 0, true);
                    Add(_chain, ProceduralSprites.RoundedRect, lockPos, new Vector2(T * 0.56f, T * 0.44f), new Color(0.55f, 0.58f, 0.62f), 10, 0, true);
                    Add(_chain, ProceduralSprites.Circle, lockPos, Vector2.one * T * 0.14f, ink, 11);
                    if (_pairTag.HasValue) Add(_chain, ProceduralSprites.Circle, lockPos + new Vector3(T * 0.3f, T * 0.24f), Vector2.one * T * 0.24f, _pairTag.Value, 11);
                    break;
                case PipeType.KeyValve:
                    var keyColor = _pairTag ?? PaletteColors.Hex(Palette.Error);
                    var kp = Along(0.5f);
                    Add(_visual, ProceduralSprites.RoundedRect, kp + new Vector3(0, T * 0.38f), new Vector2(T * 0.3f, T * 0.84f), ink, 7, 0, true);
                    Add(_visual, ProceduralSprites.RoundedRect, kp + new Vector3(0, T * 0.76f), new Vector2(T * 0.9f, T * 0.3f), ink, 7, 0, true);
                    Add(_visual, ProceduralSprites.RoundedRect, kp + new Vector3(0, T * 0.38f), new Vector2(T * 0.18f, T * 0.74f), keyColor, 8, 0, true);
                    Add(_visual, ProceduralSprites.RoundedRect, kp + new Vector3(0, T * 0.76f), new Vector2(T * 0.78f, T * 0.18f), keyColor, 8, 0, true);
                    Add(_visual, ProceduralSprites.Circle, kp, Vector2.one * T * 0.4f, ink, 8);
                    Add(_visual, ProceduralSprites.Circle, kp, Vector2.one * T * 0.24f, PaletteColors.Shade(keyColor, 0.2f), 9);
                    break;
            }
        }

        private void BuildValve(Color brass)
        {
            var ink = PaletteColors.Hex(Palette.Ink);
            var center = _board.CellLocal(Pipe.Cells[0]);
            _spin = new GameObject("spin").transform;
            _spin.SetParent(_visual, false);
            _spin.localPosition = center;
            _spin.localRotation = Quaternion.Euler(0, 0, _angleTo);
            Add(_spin, ProceduralSprites.Circle, Vector3.zero, Vector2.one * 1.0f, Color.white, -2, 0, false, true); // glow
            const float stub = 0.34f;
            for (int k = 0; k < 4; k++)
            {
                float len = k == 0 ? 0.48f : 0.38f;
                float a = k * 90f;
                var dir = Quaternion.Euler(0, 0, a) * Vector3.right;
                Add(_spin, ProceduralSprites.RoundedRect, dir * (len / 2), new Vector2(len + Ow, stub + Ow * 2), ink, 0, a, true);
                Add(_spin, ProceduralSprites.RoundedRect, dir * (len / 2), new Vector2(len, stub), k == 0 ? PaletteColors.Shade(brass, 0.08f) : PaletteColors.Shade(brass, -0.08f), 1, a, true);
                Add(_spin, ProceduralSprites.RoundedRect, dir * (len - 0.04f), new Vector2(0.1f + Ow * 2, stub * 1.25f + Ow * 2), ink, 2, a, true);
                Add(_spin, ProceduralSprites.RoundedRect, dir * (len - 0.04f), new Vector2(0.1f, stub * 1.25f), PaletteColors.Shade(brass, -0.06f), 3, a, true);
            }
            Add(_spin, ProceduralSprites.Circle, Vector3.zero, Vector2.one * (0.5f + Ow * 2), ink, 4);
            Add(_spin, ProceduralSprites.Circle, Vector3.zero, Vector2.one * 0.5f, PaletteColors.Shade(brass, -0.15f), 5);
            Add(_spin, ProceduralSprites.Circle, Vector3.zero, Vector2.one * 0.36f, PaletteColors.Shade(brass, 0.25f), 6);
            Add(_spin, ProceduralSprites.Circle, Vector3.zero, Vector2.one * 0.28f, PaletteColors.Shade(brass, -0.15f), 7);
            for (int k = 0; k < 3; k++)
                Add(_spin, ProceduralSprites.RoundedRect, Quaternion.Euler(0, 0, 30 + k * 120) * Vector3.right * 0.09f, new Vector2(0.18f, 0.05f), ink, 8, 30 + k * 120, true);
            Add(_spin, ProceduralSprites.Circle, Vector3.zero, Vector2.one * 0.11f, PaletteColors.Shade(brass, 0.3f), 9);
            Add(_spin, ProceduralSprites.Triangle, Vector3.right * 0.36f, Vector2.one * 0.22f, ink, 10);
        }

        private void BuildBadge()
        {
            bool frozen = Pipe.Type == PipeType.Frozen && Pipe.IceCount > 0;
            bool leaking = Pipe.Type == PipeType.Leaking;
            if (!frozen && !leaking) return;
            var mid = Pipe.Path[(Pipe.Path.Count - 1) / 2];
            _badgeRoot = new GameObject("badge").transform;
            _badgeRoot.SetParent(_visual, false);
            _badgeRoot.localPosition = _board.CellLocal(mid) + new Vector3(0.28f, 0.3f, 0);
            Add(_badgeRoot, ProceduralSprites.Circle, Vector3.zero, Vector2.one * 0.46f, PaletteColors.Hex(Palette.Ink), 20);
            Add(_badgeRoot, ProceduralSprites.Circle, Vector3.zero, Vector2.one * 0.38f, frozen ? new Color(0.91f, 0.97f, 1f) : Color.white, 21);
            var go = new GameObject("count");
            go.transform.SetParent(_badgeRoot, false);
            _badge = go.AddComponent<TextMesh>();
            _badge.font = SpriteLibrary.UiFont();
            _badge.fontSize = 64;
            _badge.characterSize = 0.045f;
            _badge.anchor = TextAnchor.MiddleCenter;
            _badge.alignment = TextAlignment.Center;
            _badge.fontStyle = FontStyle.Bold;
            _badge.color = PaletteColors.Hex(Palette.Ink);
            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _badge.font.material;
            mr.sortingOrder = OrderBase + 22;
            RefreshBadge();
        }

        public void RefreshBadge()
        {
            if (_badge == null) return;
            int value = Pipe.Type == PipeType.Frozen ? Pipe.IceCount : Pipe.LeakTimer;
            _badge.text = value.ToString();
            bool danger = Pipe.Type == PipeType.Leaking && Pipe.LeakTimer <= 2;
            var bg = _badgeRoot.GetChild(1).GetComponent<SpriteRenderer>();
            var c = danger ? PaletteColors.Hex(Palette.Error) : Pipe.Type == PipeType.Frozen ? new Color(0.91f, 0.97f, 1f) : Color.white;
            bg.color = c;
            int i = _renderers.IndexOf(bg);
            if (i >= 0) _baseColors[i] = c;
            _badge.color = danger ? Color.white : PaletteColors.Hex(Palette.Ink);
        }

        // ------------------------------------------------------------------ animation triggers
        public void PlayExit(ExitCause cause)
        {
            _exitT0 = Now;
            _exitDir = Pipe.Dir;
            _hammered = cause == ExitCause.Hammer;
            _exitDur = _hammered ? 0.45f : GameTiming.ExitDuration;
            _orderBoost = 100;
            foreach (var r in _renderers) r.sortingOrder += _orderBoost;
            if (_badge != null) _badge.GetComponent<MeshRenderer>().sortingOrder += _orderBoost;
        }

        public void PlayShake() => _shakeT0 = Now;
        public void PlayFlash() => _flashT0 = Now;
        public void PlayBadgePop() { _badgeT0 = Now; RefreshBadge(); }

        public void PlayRotate()
        {
            _angleFrom = _angleTo; // continue from the logical angle (eulerAngles wrap to 0..360)
            _angleTo -= 90;        // clockwise on screen
            _rotT0 = Now;
        }

        public void PlayUnlock() => _chainT0 = Now;

        // ------------------------------------------------------------------ per-frame
        private void Update()
        {
            if (Finished) return;
            float now = Now;
            var offset = Vector3.zero;
            float alpha = 1f, scale = 1f;

            if (_exitT0 >= 0)
            {
                float k = Mathf.Clamp01((now - _exitT0) / _exitDur);
                if (k >= 1f)
                {
                    Finished = true;
                    gameObject.SetActive(false);
                    return;
                }
                if (_hammered)
                {
                    scale = 1 - 0.6f * k;
                    alpha = 1 - k;
                }
                else
                {
                    int travel = _board.TravelCells(Pipe, _exitDir);
                    offset = WorldDir(_exitDir) * (k * k * travel);
                    alpha = k > 0.75f ? 1 - (k - 0.75f) / 0.25f : 1f;
                    if (k < 0.55f && Pipe.IsWater) _board.Spray(Pipe, offset, _exitDir);
                }
            }

            float sk = now - _shakeT0;
            if (sk < GameTiming.ShakeDuration)
            {
                var d = WorldDir(Pipe.Dir);
                var perp = new Vector3(-d.y, d.x, 0);
                offset += d * (Mathf.Sin(Mathf.Min(1, sk / 0.12f) * Mathf.PI) * 0.14f) + perp * (Mathf.Sin(sk * 70) * 0.06f * (1 - sk / GameTiming.ShakeDuration));
            }

            _visual.localPosition = offset;
            if (scale != 1f)
            {
                var c = _board.CellLocal(Pipe.Path[Pipe.Path.Count / 2]);
                _visual.localScale = Vector3.one * scale;
                _visual.localPosition = offset + c * (1 - scale);
            }

            if (_spin != null)
            {
                float rk = Mathf.Clamp01((now - _rotT0) / 0.22f);
                float e = 1 + 2.70158f * Mathf.Pow(rk - 1, 3) + 1.70158f * Mathf.Pow(rk - 1, 2); // easeOutBack
                _spin.localRotation = Quaternion.Euler(0, 0, Mathf.LerpUnclamped(_angleFrom, _angleTo, e));
            }

            if (_chain != null && _chainT0 > 0)
            {
                float f = now - _chainT0;
                _chain.localPosition = Vector3.down * (f * f * 3f);
                if (f > 0.6f)
                {
                    Destroy(_chain.gameObject);
                    _chain = null;
                    Rebuild();
                }
            }

            for (int i = 0; i < _drips.Count; i++)
            {
                if (_drips[i] == null) continue;
                float ph = (now * 0.9f + i * 0.5f + Pipe.Index * 0.13f) % 1f;
                var sr = _drips[i].GetComponent<SpriteRenderer>();
                var basePos = _board.CellLocal(Pipe.Path[Mathf.Min(i, Pipe.Path.Count - 1)]);
                _drips[i].localPosition = basePos + new Vector3(i == 0 ? -0.08f : 0.1f, -0.25f - ph * 0.6f, 0);
                sr.color = PaletteColors.WithAlpha(sr.color, 1 - ph);
            }

            if (_badgeRoot != null)
            {
                float pop = Mathf.Max(0, 1 - (now - _badgeT0) / 0.35f);
                float pulse = Pipe.Type == PipeType.Leaking && Pipe.LeakTimer <= 2 ? 0.08f * Mathf.Sin(now * 10) : 0;
                _badgeRoot.localScale = Vector3.one * (1 + pop * 0.45f + pulse);
            }

            // tint: red flash on blockers, fade on exit
            float fk = (now - _flashT0) / GameTiming.FlashDuration;
            float flash = fk >= 0 && fk < 1 && _exitT0 < 0 ? 0.85f * (1 - fk) * (0.6f + 0.4f * Mathf.Sin(fk * 26)) : 0;
            var error = PaletteColors.Hex(Palette.Error);
            for (int i = 0; i < _renderers.Count; i++)
            {
                if (_renderers[i] == null) continue;
                var c = flash > 0 ? Color.Lerp(_baseColors[i], error, flash) : _baseColors[i];
                if (_drips.Contains(_renderers[i].transform)) continue;
                c.a = _baseColors[i].a * alpha;
                _renderers[i].color = c;
            }
            if (_badge != null) _badge.color = PaletteColors.WithAlpha(_badge.color, alpha);
            float glow = XRay && _exitT0 < 0 ? 0.55f + 0.35f * Mathf.Sin(now * 8) : Pending ? 0.35f + 0.25f * Mathf.Sin(now * 20) : 0f;
            var glowColor = Pending ? PaletteColors.Hex(Palette.Water) : Color.white;
            foreach (var g in _glow)
            {
                g.enabled = glow > 0;
                g.color = PaletteColors.WithAlpha(glowColor, glow);
            }
        }
    }
}
