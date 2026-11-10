using SuUstasi.Services;
using UnityEngine;

namespace SuUstasi.Runtime
{
    /// <summary>Hex palette → Color helpers.</summary>
    public static class PaletteColors
    {
        public static Color Hex(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.magenta;
        }

        /// <summary>amount &gt; 0 lightens toward white, &lt; 0 darkens toward black.</summary>
        public static Color Shade(Color c, float amount)
        {
            if (amount >= 0) return Color.Lerp(c, Color.white, amount);
            return Color.Lerp(c, Color.black, -amount);
        }

        public static Color WithAlpha(Color c, float a)
        {
            c.a = a;
            return c;
        }
    }

    public sealed class UnityClock : IClock
    {
        public double Now => Time.realtimeSinceStartupAsDouble;
    }

    public sealed class UnityLogSink : ILogSink
    {
        public void Log(string message) => Debug.Log(message);
    }

    /// <summary>Loads the sliced reference sprites from Resources/Sprites (tools/slice_sprites.py).</summary>
    public static class SpriteLibrary
    {
        /// <summary>
        /// Works whether Unity imported the PNG as Sprite (SpriteImportPostprocessor) or as a plain
        /// Texture2D (first import before scripts compiled): falls back to Sprite.Create.
        /// </summary>
        public static Sprite Load(string resourcesPath, float pixelsPerUnit = 100f)
        {
            var sprite = Resources.Load<Sprite>(resourcesPath);
            if (sprite != null) return sprite;
            var tex = Resources.Load<Texture2D>(resourcesPath);
            if (tex == null)
            {
                Debug.LogWarning("[SuUstasi] missing sprite Resources/" + resourcesPath + " — run tools/slice_sprites.py");
                return null;
            }
            return Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
        }

        public static Font UiFont()
        {
#if UNITY_2022_2_OR_NEWER
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
#else
            var f = Resources.GetBuiltinResource<Font>("Arial.ttf");
#endif
            return f != null ? f : Font.CreateDynamicFontFromOSFont("Arial", 32);
        }
    }

    /// <summary>Runtime-generated, anti-aliased white shapes tinted per use (no art files needed for the board).</summary>
    public static class ProceduralSprites
    {
        private const int Size = 64;
        private static Sprite _rounded, _circle, _triangle, _panel;

        /// <summary>9-sliced rounded rectangle, 1×1 world unit at scale 1.</summary>
        public static Sprite RoundedRect => _rounded ? _rounded : (_rounded = Make(RoundedAlpha(20), new Vector4(22, 22, 22, 22), Size));

        /// <summary>Disc of diameter 1 world unit.</summary>
        public static Sprite Circle => _circle ? _circle : (_circle = Make(CircleAlpha, Vector4.zero, Size));

        /// <summary>Right-pointing triangle, 1 world unit.</summary>
        public static Sprite Triangle => _triangle ? _triangle : (_triangle = Make(TriangleAlpha, Vector4.zero, Size));

        /// <summary>UI panel (sliced) with a dark border baked in, for uGUI Images.</summary>
        public static Sprite Panel => _panel ? _panel : (_panel = MakePanel());

        private delegate float AlphaFn(float x, float y);

        private static AlphaFn RoundedAlpha(float r) => (x, y) =>
        {
            float dx = Mathf.Max(Mathf.Max(r - x, x - (Size - r)), 0);
            float dy = Mathf.Max(Mathf.Max(r - y, y - (Size - r)), 0);
            return Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy) + 0.5f);
        };

        private static float CircleAlpha(float x, float y)
        {
            float c = Size / 2f;
            return Mathf.Clamp01(c - Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) + 0.5f - 1f);
        }

        private static float TriangleAlpha(float x, float y)
        {
            // apex at right-middle; edges from (8,8)/(8,56) to (58,32)
            float t = (x - 8f) / 50f;
            if (t < 0 || t > 1) return 0;
            float half = 24f * (1 - t);
            return Mathf.Clamp01(half - Mathf.Abs(y - 32f) + 0.5f);
        }

        private static Sprite Make(AlphaFn alpha, Vector4 border, int ppu)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                    px[y * Size + x] = new Color32(255, 255, 255, (byte)(alpha(x + 0.5f, y + 0.5f) * 255));
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), ppu, 0, SpriteMeshType.FullRect, border);
        }

        private static Sprite MakePanel()
        {
            var ink = PaletteColors.Hex(Core.Palette.Ink);
            var fill = PaletteColors.Hex("#FBE7C6");
            var rounded = RoundedAlpha(20);
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var px = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float a = rounded(x + 0.5f, y + 0.5f);
                    float inner = Mathf.Clamp01(Mathf.Min(Mathf.Min(x, y), Mathf.Min(Size - 1 - x, Size - 1 - y)) - 3f);
                    var c = Color.Lerp(ink, fill, inner);
                    c.a = a;
                    px[y * Size + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100, 0, SpriteMeshType.FullRect, new Vector4(24, 24, 24, 24));
        }
    }
}
