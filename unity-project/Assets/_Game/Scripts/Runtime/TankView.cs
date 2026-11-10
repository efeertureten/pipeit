using SuUstasi.Core;
using UnityEngine;

namespace SuUstasi.Runtime
{
    /// <summary>Central glass tank: water level eases toward the session's water ratio; drains on win.</summary>
    public sealed class TankView : MonoBehaviour
    {
        private SpriteRenderer _water, _surface;
        private Transform[] _bubbles;
        private float _innerW, _innerH, _bottom;
        private float _shown = 1f, _target = 1f;
        private float _drainT0 = -1, _drainFrom;

        public void Build(BoardView board, TankData tank)
        {
            var c0 = board.CellLocal(new Vector2Int(tank.X, tank.Y));
            var c1 = board.CellLocal(new Vector2Int(tank.X + tank.W - 1, tank.Y + tank.H - 1));
            transform.localPosition = (c0 + c1) / 2;
            float w = tank.W - 0.2f, h = tank.H - 0.2f;
            _innerW = w - 0.16f;
            _innerH = h - 0.16f;
            _bottom = -_innerH / 2;
            Rect(Vector3.zero, new Vector2(w + 0.12f, h + 0.12f), PaletteColors.Hex(Palette.Ink), 1);
            Rect(Vector3.zero, new Vector2(w, h), PaletteColors.Hex("#E4F3F9"), 2);
            _water = Rect(Vector3.zero, new Vector2(_innerW, _innerH), PaletteColors.Hex(Palette.Water), 3);
            _surface = Rect(Vector3.zero, new Vector2(_innerW, 0.08f), PaletteColors.Shade(PaletteColors.Hex(Palette.Water), 0.3f), 4);
            Rect(new Vector3(-w * 0.32f, h * 0.12f), new Vector2(w * 0.1f, h * 0.55f), new Color(1, 1, 1, 0.45f), 7);
            _bubbles = new Transform[6];
            for (int i = 0; i < _bubbles.Length; i++)
            {
                var go = new GameObject("bubble");
                go.transform.SetParent(transform, false);
                go.transform.localScale = Vector3.one * (0.06f + (i % 3) * 0.03f);
                var sr = go.AddComponent<SpriteRenderer>();
                sr.sprite = ProceduralSprites.Circle;
                sr.color = new Color(1, 1, 1, 0.55f);
                sr.sortingOrder = 5;
                _bubbles[i] = go.transform;
            }
            Apply(1f);
        }

        private SpriteRenderer Rect(Vector3 pos, Vector2 size, Color color, int order)
        {
            var go = new GameObject("tank_part");
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

        public void SetLevel(float ratio) => _target = Mathf.Clamp01(ratio);

        public void Drain()
        {
            _drainT0 = Time.time;
            _drainFrom = _shown;
        }

        private void Apply(float level)
        {
            float h = Mathf.Max(0.001f, _innerH * level);
            _water.size = new Vector2(_innerW, h);
            _water.transform.localPosition = new Vector3(0, _bottom + h / 2, 0);
            _water.enabled = level > 0.005f;
            _surface.transform.localPosition = new Vector3(0, _bottom + h - 0.04f + Mathf.Sin(Time.time * 3) * 0.015f, 0);
            _surface.enabled = level > 0.02f;
        }

        private void Update()
        {
            float target = _drainT0 >= 0 ? Mathf.Max(0, _drainFrom * (1 - (Time.time - _drainT0) / 1.3f)) : _target;
            _shown += (target - _shown) * Mathf.Min(1, Time.deltaTime * (_drainT0 >= 0 ? 30 : 5));
            Apply(_shown);
            float top = _bottom + _innerH * _shown;
            for (int i = 0; i < _bubbles.Length; i++)
            {
                float ph = (Time.time * (0.25f + i * 0.05f) + i * 0.37f) % 1f;
                float x = -_innerW / 2 + _innerW * (0.15f + (i * 0.27f) % 0.7f) + Mathf.Sin(Time.time * 2 + i) * 0.03f;
                _bubbles[i].localPosition = new Vector3(x, Mathf.Lerp(_bottom, top, ph), 0);
                _bubbles[i].gameObject.SetActive(_shown > 0.05f);
            }
        }
    }
}
