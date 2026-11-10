// Headless stand-in for UnityEngine.Vector2Int.
// Inside Unity the real type is used (this file compiles to nothing); outside Unity
// (dotnet / Roslyn CI build, see tools/run_csharp_tests.mjs) it lets the pure-C# Core,
// LevelGen, Services and Gameplay assemblies — and their NUnit tests — build unchanged.
#if !UNITY_2017_2_OR_NEWER
namespace UnityEngine
{
    public struct Vector2Int : System.IEquatable<Vector2Int>
    {
        public int x;
        public int y;

        public Vector2Int(int x, int y)
        {
            this.x = x;
            this.y = y;
        }

        public static Vector2Int zero => new Vector2Int(0, 0);

        public static Vector2Int operator +(Vector2Int a, Vector2Int b) => new Vector2Int(a.x + b.x, a.y + b.y);
        public static Vector2Int operator -(Vector2Int a, Vector2Int b) => new Vector2Int(a.x - b.x, a.y - b.y);
        public static Vector2Int operator *(Vector2Int a, int k) => new Vector2Int(a.x * k, a.y * k);
        public static bool operator ==(Vector2Int a, Vector2Int b) => a.x == b.x && a.y == b.y;
        public static bool operator !=(Vector2Int a, Vector2Int b) => !(a == b);

        public bool Equals(Vector2Int other) => this == other;
        public override bool Equals(object obj) => obj is Vector2Int v && this == v;
        public override int GetHashCode() => x * 73856093 ^ y * 19349663;
        public override string ToString() => "(" + x + ", " + y + ")";
    }
}
#endif
