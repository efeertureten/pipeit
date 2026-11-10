using System.Collections.Generic;

namespace SuUstasi.Core
{
    /// <summary>
    /// mulberry32 PRNG — bit-identical to core.js mulberry32, so the C# and JS level
    /// generators produce the same levels for the same seed.
    /// </summary>
    public sealed class DeterministicRandom
    {
        private uint _state;

        public DeterministicRandom(uint seed)
        {
            _state = seed;
        }

        /// <summary>Uniform double in [0, 1).</summary>
        public double Next()
        {
            unchecked
            {
                _state += 0x6D2B79F5u;
                uint t = _state;
                t = (t ^ (t >> 15)) * (t | 1u);
                t ^= t + (t ^ (t >> 7)) * (t | 61u);
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }

        /// <summary>Inclusive integer range.</summary>
        public int Int(int lo, int hi) => lo + (int)System.Math.Floor(Next() * (hi - lo + 1));

        public T Pick<T>(IReadOnlyList<T> list) => list[(int)System.Math.Floor(Next() * list.Count)];

        public void Shuffle<T>(IList<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = (int)System.Math.Floor(Next() * (i + 1));
                T tmp = list[i];
                list[i] = list[j];
                list[j] = tmp;
            }
        }
    }
}
