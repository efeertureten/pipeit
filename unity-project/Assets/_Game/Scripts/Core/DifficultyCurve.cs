using System;

namespace SuUstasi.Core
{
    /// <summary>Sawtooth target difficulty (1–10): smooth rise with level, every 5th level dips (breather).</summary>
    public static class DifficultyCurve
    {
        private static readonly double[] Saw = { -0.6, -0.2, 0.2, 0.6, -1.8 };

        public static double Target(int level)
        {
            double b = 1.6 + 7.4 * (1 - Math.Exp(-(level - 1) / 45.0));
            double v = Math.Min(10, Math.Max(1, b + Saw[(level - 1) % 5]));
            return JsMath.Round1(v);
        }
    }
}
