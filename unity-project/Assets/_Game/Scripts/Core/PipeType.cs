namespace SuUstasi.Core
{
    /// <summary>The 8 GDD pipe mechanics (locked + key_valve form the 6th mechanic pair).</summary>
    public enum PipeType
    {
        Straight,
        Elbow,
        Dry,
        Rusty,
        Frozen,
        Locked,
        KeyValve,
        Leaking,
        RotaryValve,
    }

    public static class PipeTypes
    {
        public static readonly PipeType[] All =
        {
            PipeType.Straight, PipeType.Elbow, PipeType.Dry, PipeType.Rusty, PipeType.Frozen,
            PipeType.Locked, PipeType.KeyValve, PipeType.Leaking, PipeType.RotaryValve,
        };

        /// <summary>Dry pipes carry no water and never count toward the win condition.</summary>
        public static bool IsWater(this PipeType t) => t != PipeType.Dry;

        /// <summary>First level a type may appear in (GDD unlock table).</summary>
        public static int UnlockLevel(this PipeType t)
        {
            switch (t)
            {
                case PipeType.Straight: return 1;
                case PipeType.Elbow: return 8;
                case PipeType.Dry: return 12;
                case PipeType.Rusty: return 16;
                case PipeType.Frozen: return 22;
                case PipeType.Locked:
                case PipeType.KeyValve: return 30;
                case PipeType.Leaking: return 35;
                default: return 61; // RotaryValve
            }
        }

        public static string ToJson(this PipeType t)
        {
            switch (t)
            {
                case PipeType.Straight: return "straight";
                case PipeType.Elbow: return "elbow";
                case PipeType.Dry: return "dry";
                case PipeType.Rusty: return "rusty";
                case PipeType.Frozen: return "frozen";
                case PipeType.Locked: return "locked";
                case PipeType.KeyValve: return "key_valve";
                case PipeType.Leaking: return "leaking";
                default: return "rotary_valve";
            }
        }

        public static bool TryParse(string s, out PipeType t)
        {
            foreach (var candidate in All)
            {
                if (candidate.ToJson() == s)
                {
                    t = candidate;
                    return true;
                }
            }
            t = PipeType.Straight;
            return false;
        }
    }
}
