using System;

namespace SuUstasi.Gameplay
{
    /// <summary>
    /// Procedural SFX (mono float PCM, no audio files) — the same recipes as web-playable/js/audio.js.
    /// Water rises 2 semitones per consecutive successful exit, bonk is inharmonic metal, drain is a
    /// falling gurgle. Pure + deterministic so it is unit-testable; Unity wraps samples in AudioClips.
    /// </summary>
    public static class SfxRecipes
    {
        public const int SampleRate = 44100;

        public static float[] Water(int combo)
        {
            int semis = Math.Min(Math.Max(combo - 1, 0), 7) * 2;
            double f = 330 * Math.Pow(2, semis / 12.0);
            var b = Buffer(0.5);
            Noise(b, 0, 0.42, 700, 2600, NoiseFilter.Band, 0.35, 11);
            Tone(b, 0.02, 0.16, f, f * 1.7, Wave.Sine, 0.42);
            Tone(b, 0.09, 0.12, f * 2, f * 2.6, Wave.Triangle, 0.16);
            Noise(b, 0.18, 0.25, 1600, 500, NoiseFilter.Low, 0.12, 12);
            return Normalize(b);
        }

        public static float[] Bonk()
        {
            var b = Buffer(0.4);
            double[] ratios = { 1, 2.62, 4.24, 6.08, 8.3 };
            for (int i = 0; i < ratios.Length; i++)
                Tone(b, 0, 0.32 - i * 0.04, 165 * ratios[i], 0, i % 2 == 1 ? Wave.Square : Wave.Sine, 0.22 / (i + 1));
            Tone(b, 0, 0.12, 110, 55, Wave.Sine, 0.5);
            Noise(b, 0, 0.05, 3000, 0, NoiseFilter.High, 0.18, 21);
            return Normalize(b);
        }

        public static float[] Drain()
        {
            var b = Buffer(1.9);
            Noise(b, 0, 1.7, 2200, 180, NoiseFilter.Low, 0.32, 31);
            // gurgle: falling sine with a 9 Hz vibrato
            int n = (int)(1.6 * SampleRate);
            double phase = 0;
            for (int i = 0; i < n && i < b.Length; i++)
            {
                double t = (double)i / SampleRate;
                double f = 320 * Math.Pow(70.0 / 320, t / 1.6) + 40 * Math.Sin(2 * Math.PI * 9 * t);
                phase += 2 * Math.PI * f / SampleRate;
                b[i] += (float)(Math.Sin(phase) * Envelope(t, 0.08, 1.6) * 0.25);
            }
            return Normalize(b);
        }

        public static float[] Crack()
        {
            var b = Buffer(0.25);
            Noise(b, 0, 0.06, 1800, 0, NoiseFilter.High, 0.35, 41);
            Noise(b, 0.045, 0.06, 2400, 0, NoiseFilter.High, 0.35, 42);
            Noise(b, 0.1, 0.06, 3000, 0, NoiseFilter.High, 0.35, 43);
            Tone(b, 0, 0.08, 240, 120, Wave.Square, 0.12);
            return Normalize(b);
        }

        public static float[] Ice()
        {
            var b = Buffer(0.4);
            double[] fs = { 2093, 2637, 3136 };
            for (int i = 0; i < fs.Length; i++) Tone(b, i * 0.03, 0.25, fs[i], 0, Wave.Sine, 0.12);
            return Normalize(b);
        }

        public static float[] Unlock()
        {
            var b = Buffer(0.4);
            Tone(b, 0, 0.03, 1200, 0, Wave.Square, 0.15);
            Tone(b, 0.06, 0.03, 900, 0, Wave.Square, 0.15);
            foreach (var d in new[] { 0.1, 0.15, 0.21, 0.26 }) Noise(b, d, 0.05, 5200, 0, NoiseFilter.Band, 0.2, (int)(d * 1000));
            return Normalize(b);
        }

        public static float[] Rotate()
        {
            var b = Buffer(0.25);
            for (int i = 0; i < 3; i++) Tone(b, i * 0.05, 0.018, 900 - i * 80, 0, Wave.Square, 0.12);
            Tone(b, 0, 0.18, 520, 780, Wave.Sine, 0.08);
            return Normalize(b);
        }

        public static float[] Leak()
        {
            var b = Buffer(0.5);
            Tone(b, 0, 0.08, 1500, 480, Wave.Sine, 0.3);
            Tone(b, 0.11, 0.08, 1300, 420, Wave.Sine, 0.25);
            Noise(b, 0, 0.4, 1500, 700, NoiseFilter.Band, 0.25, 51);
            return Normalize(b);
        }

        public static float[] Hammer()
        {
            var b = Buffer(0.35);
            Tone(b, 0, 0.25, 90, 40, Wave.Sine, 0.7);
            Noise(b, 0, 0.18, 2500, 300, NoiseFilter.Low, 0.5, 61);
            Tone(b, 0, 0.2, 300, 0, Wave.Triangle, 0.12);
            Tone(b, 0, 0.2, 828, 0, Wave.Triangle, 0.06);
            return Normalize(b);
        }

        public static float[] Win()
        {
            var b = Buffer(1.2);
            double[] notes = { 523.25, 659.25, 783.99, 1046.5 };
            for (int i = 0; i < notes.Length; i++) Tone(b, 0.1 + i * 0.11, 0.22, notes[i], 0, Wave.Triangle, 0.28);
            Tone(b, 0.54, 0.6, 1046.5, 0, Wave.Sine, 0.2);
            return Normalize(b);
        }

        public static float[] Lose()
        {
            var b = Buffer(1.0);
            Tone(b, 0, 0.9, 392, 150, Wave.Saw, 0.2);
            Noise(b, 0.1, 0.8, 1200, 400, NoiseFilter.Band, 0.2, 71);
            return Normalize(b);
        }

        public static float[] Coin()
        {
            var b = Buffer(0.3);
            Tone(b, 0, 0.08, 1318.5, 0, Wave.Sine, 0.2);
            Tone(b, 0.07, 0.18, 1760, 0, Wave.Sine, 0.2);
            return Normalize(b);
        }

        public static float[] Click()
        {
            var b = Buffer(0.06);
            Tone(b, 0, 0.04, 660, 0, Wave.Triangle, 0.1);
            return Normalize(b);
        }

        // ------------------------------------------------------------------ DSP helpers
        private enum Wave { Sine, Square, Triangle, Saw }

        private enum NoiseFilter { Low, High, Band }

        private static float[] Buffer(double seconds) => new float[(int)(seconds * SampleRate)];

        /// <summary>Exponential attack/decay envelope (matches the Web Audio exponentialRamp shape).</summary>
        private static double Envelope(double t, double attack, double decay)
        {
            if (t < 0) return 0;
            if (t < attack) return Math.Pow(t / attack, 2);
            double k = (t - attack) / decay;
            return k >= 1 ? 0 : Math.Exp(-6.9 * k); // ~-60 dB at the end
        }

        /// <summary>Tone with optional exponential glide f0 → f1 (f1 = 0 means constant).</summary>
        private static void Tone(float[] buf, double start, double dur, double f0, double f1, Wave wave, double peak)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)((dur + 0.02) * SampleRate);
            double attack = Math.Min(0.01, dur / 4);
            double phase = 0;
            for (int i = 0; i < n && s0 + i < buf.Length; i++)
            {
                double t = (double)i / SampleRate;
                double f = f1 > 0 ? f0 * Math.Pow(f1 / f0, Math.Min(1, t / dur)) : f0;
                phase += f / SampleRate;
                double p = phase - Math.Floor(phase);
                double v;
                switch (wave)
                {
                    case Wave.Square: v = p < 0.5 ? 1 : -1; break;
                    case Wave.Triangle: v = 4 * Math.Abs(p - 0.5) - 1; break;
                    case Wave.Saw: v = 2 * p - 1; break;
                    default: v = Math.Sin(2 * Math.PI * p); break;
                }
                buf[s0 + i] += (float)(v * peak * Envelope(t, attack, dur));
            }
        }

        /// <summary>Seeded white noise through a swept one-pole low/high pass (band = both).</summary>
        private static void Noise(float[] buf, double start, double dur, double f0, double f1, NoiseFilter filter, double peak, int seed)
        {
            int s0 = (int)(start * SampleRate);
            int n = (int)((dur + 0.02) * SampleRate);
            uint state = (uint)(seed * 2654435761u + 1);
            double lp = 0, hpPrev = 0, hpOut = 0;
            double attack = Math.Min(0.02, dur / 5);
            for (int i = 0; i < n && s0 + i < buf.Length; i++)
            {
                double t = (double)i / SampleRate;
                state = state * 1664525u + 1013904223u;
                double white = (state >> 8) / 8388608.0 - 1.0;
                double fc = f1 > 0 ? f0 * Math.Pow(f1 / f0, Math.Min(1, t / dur)) : f0;
                double a = 1 - Math.Exp(-2 * Math.PI * fc / SampleRate);
                double v;
                if (filter == NoiseFilter.Low)
                {
                    lp += a * (white - lp);
                    v = lp;
                }
                else
                {
                    // one-pole high pass, then (band) a gentle low pass at 2x the cutoff
                    hpOut = (1 - a) * (hpOut + white - hpPrev);
                    hpPrev = white;
                    v = hpOut;
                    if (filter == NoiseFilter.Band)
                    {
                        double a2 = 1 - Math.Exp(-2 * Math.PI * fc * 2 / SampleRate);
                        lp += a2 * (v - lp);
                        v = lp * 2;
                    }
                }
                buf[s0 + i] += (float)(v * peak * Envelope(t, attack, dur));
            }
        }

        private static float[] Normalize(float[] b, float target = 0.85f)
        {
            float max = 0;
            foreach (var v in b) max = Math.Max(max, Math.Abs(v));
            if (max < 1e-6f) return b;
            float k = target / max;
            for (int i = 0; i < b.Length; i++) b[i] *= k;
            return b;
        }
    }
}
