using System;
using System.Collections.Generic;
using System.Linq;
using SuUstasi.Core;
using UnityEngine;

namespace SuUstasi.LevelGen
{
    public sealed class BoardSpec
    {
        public int W, H, TankW, TankH;
    }

    public sealed class MechanicPlan
    {
        public int Elbows, Dry, Rusty, Frozen, LockedPairs, Leaking, Valves;

        public int SpecialCount => Dry + Rusty + Frozen + LockedPairs * 2 + Leaking + Valves;
    }

    public sealed class GeneratedLevel
    {
        public LevelData Level;
        public SolverResult Result;
        public double Target;
        public double Error;
        public int Attempt;
        public uint Seed;
        /// <summary>The reverse-build order (last placed leaves first) — always a valid solution.</summary>
        public List<string> CanonicalOrder;
    }

    public sealed class BuildResult
    {
        public LevelData Level;
        public MechanicPlan Plan;
        public int TargetDepth;
        public List<string> CanonicalOrder;
    }

    /// <summary>
    /// Reverse-build generator (mirrors web-playable/js/generator.js bit-for-bit: same RNG,
    /// same iteration order, JS-compatible rounding, stable sorts).
    ///
    /// Pipes are placed in REVERSE exit order. The first pipe placed leaves last, so its exit
    /// path only has to avoid the tank. Every later pipe must keep its own swept exit path clear
    /// of the tank and of all pipes placed so far (they are still on the board when it leaves),
    /// while its body MAY sit inside earlier pipes' exit paths — creating the dependency DAG.
    /// Exiting in reverse placement order is therefore always a solution. Mechanics are overlaid
    /// so that order stays valid; each candidate is re-verified by <see cref="LevelSolver"/>.
    /// </summary>
    public static class LevelGenerator
    {
        private const int Empty = -1, Tank = -2;

        // ------------------------------------------------------------------ level bands
        public static BoardSpec BoardFor(int level)
        {
            if (level <= 3) return new BoardSpec { W = 5, H = 5, TankW = 2, TankH = 2 };
            if (level <= 14) return new BoardSpec { W = 6, H = 6, TankW = 2, TankH = 2 };
            if (level <= 29) return new BoardSpec { W = 7, H = 7, TankW = 3, TankH = 3 };
            if (level <= 49) return new BoardSpec { W = 7, H = 8, TankW = 3, TankH = 3 };
            if (level <= 79) return new BoardSpec { W = 8, H = 9, TankW = 3, TankH = 3 };
            return new BoardSpec { W = 9, H = 11, TankW = 3, TankH = 3 };
        }

        public static int PipeCountFor(int level, bool breather, BoardSpec bd)
        {
            if (level <= 3) return new[] { 3, 4, 5 }[level - 1];
            int n = 3 + (int)JsMath.Round(13 * (1 - Math.Exp(-(level - 1) / 28.0)));
            if (bd.W * bd.H >= 99) n += 2;
            if (breather) n = Math.Max(3, (int)JsMath.Round(n * 0.7));
            int free = bd.W * bd.H - bd.TankW * bd.TankH;
            return Math.Min(n, (int)Math.Floor(free / 3.2)); // keep room for clear exit lanes
        }

        public static void LengthLimits(int level, bool breather, out int straightMax, out int elbowMax)
        {
            straightMax = breather ? 3 : level < 8 ? 3 : level < 30 ? 4 : 5;
            elbowMax = breather ? 4 : level < 30 ? 4 : 5;
        }

        public static int TargetDepthFor(int level, double target, bool breather)
        {
            if (level == 1) return 1;
            if (level <= 3) return 2;
            int d = (int)JsMath.Round(1 + target * 0.55);
            return Math.Max(2, Math.Min(7, breather ? d - 1 : d));
        }

        /// <summary>How many of each mechanic a level gets; intro levels showcase the new type.</summary>
        public static MechanicPlan PlanMechanics(int level, int n, DeterministicRandom rng, bool breather)
        {
            bool Unlocked(PipeType t) => level >= t.UnlockLevel();
            int Since(PipeType t) => level - t.UnlockLevel();
            int PickCount(PipeType t, int introCount, int lo, int hi)
            {
                if (!Unlocked(t)) return 0;
                if (Since(t) <= 1) return introCount;
                if (Since(t) <= 4) return Math.Max(1, rng.Int(lo, hi));
                return rng.Int(lo, hi);
            }

            var plan = new MechanicPlan();
            if (Unlocked(PipeType.Elbow))
                plan.Elbows = Since(PipeType.Elbow) <= 1 ? 2 : Math.Max(1, (int)JsMath.Round(n * (0.18 + rng.Next() * 0.14)));
            plan.Dry = PickCount(PipeType.Dry, 2, 0, 2);
            plan.Rusty = PickCount(PipeType.Rusty, 2, 0, 2);
            plan.Frozen = PickCount(PipeType.Frozen, 2, 0, 2);
            plan.LockedPairs = PickCount(PipeType.Locked, 1, 0, 1) + (level >= 70 && rng.Next() < 0.4 ? 1 : 0);
            plan.Leaking = PickCount(PipeType.Leaking, 1, 0, 1);
            plan.Valves = PickCount(PipeType.RotaryValve, 2, 0, 2);

            if (breather)
            {
                if (Since(PipeType.Dry) > 1) plan.Dry /= 2;
                if (Since(PipeType.Rusty) > 1) plan.Rusty /= 2;
                if (Since(PipeType.Frozen) > 1) plan.Frozen /= 2;
                if (Since(PipeType.Leaking) > 1) plan.Leaking /= 2;
                if (Since(PipeType.RotaryValve) > 1) plan.Valves /= 2;
                if (Since(PipeType.Locked) > 1) plan.LockedPairs = 0;
            }

            // Keep ~40% plain pipes so mechanics stay readable; trim in order dry → ... → valves.
            int budget = Math.Max(1, (int)Math.Floor(n * 0.6));
            void Trim(ref int field, PipeType t)
            {
                while (plan.SpecialCount > budget && field > 0)
                {
                    if (Since(t) <= 1 && field <= 1) break; // never drop the showcase instance
                    field--;
                }
            }
            Trim(ref plan.Dry, PipeType.Dry);
            Trim(ref plan.Rusty, PipeType.Rusty);
            Trim(ref plan.Frozen, PipeType.Frozen);
            Trim(ref plan.LockedPairs, PipeType.Locked);
            Trim(ref plan.Leaking, PipeType.Leaking);
            Trim(ref plan.Valves, PipeType.RotaryValve);
            return plan;
        }

        // ------------------------------------------------------------------ shapes
        public static List<Vector2Int[]> LineShapes(int maxLen)
        {
            var list = new List<Vector2Int[]>();
            for (int len = 2; len <= maxLen; len++)
            {
                var h = new Vector2Int[len];
                var v = new Vector2Int[len];
                for (int i = 0; i < len; i++)
                {
                    h[i] = new Vector2Int(i, 0);
                    v[i] = new Vector2Int(0, i);
                }
                list.Add(h);
                list.Add(v);
            }
            return list;
        }

        public static List<Vector2Int[]> LShapes(int maxCells)
        {
            var list = new List<Vector2Int[]>();
            var signs = new[] { new Vector2Int(1, 1), new Vector2Int(-1, 1), new Vector2Int(1, -1), new Vector2Int(-1, -1) };
            for (int a = 2; a <= 4; a++)
            {
                for (int b = 2; b <= 4; b++)
                {
                    if (a + b - 1 > maxCells || a + b - 1 < 3) continue;
                    foreach (var s in signs)
                    {
                        var cells = new List<Vector2Int>();
                        for (int i = 0; i < a; i++) cells.Add(new Vector2Int(i * s.x, 0));
                        for (int j = 1; j < b; j++) cells.Add(new Vector2Int(0, j * s.y));
                        int minX = cells.Min(c => c.x), minY = cells.Min(c => c.y);
                        list.Add(cells.Select(c => new Vector2Int(c.x - minX, c.y - minY)).ToArray());
                    }
                }
            }
            return list;
        }

        // ------------------------------------------------------------------ one attempt
        private enum Kind { Single, Line, L }

        private sealed class Placed
        {
            public Vector2Int[] Cells;
            public Direction Dir;
            public Kind Kind;
            public int Height;
            public List<int> Blocks;
            public HashSet<int> Path;
        }

        public static BuildResult BuildOnce(int levelNumber, DeterministicRandom rng, double target)
        {
            bool breather = Progression.IsBreather(levelNumber);
            var bd = BoardFor(levelNumber);
            int W = bd.W, H = bd.H;
            var tank = new TankData((W - bd.TankW) / 2, (H - bd.TankH) / 2, bd.TankW, bd.TankH);
            int n = PipeCountFor(levelNumber, breather, bd);
            var plan = PlanMechanics(levelNumber, n, rng, breather);
            LengthLimits(levelNumber, breather, out int straightMax, out int elbowMax);
            int targetDepth = TargetDepthFor(levelNumber, target, breather);

            int lCount = Math.Min(n - plan.Valves, plan.Elbows + plan.Rusty + plan.Leaking);
            var kinds = new List<Kind>();
            for (int i = 0; i < plan.Valves; i++) kinds.Add(Kind.Single);
            for (int i = 0; i < lCount; i++) kinds.Add(Kind.L);
            while (kinds.Count < n) kinds.Add(Kind.Line);
            rng.Shuffle(kinds);

            var occ = new int[W * H];
            for (int i = 0; i < occ.Length; i++) occ[i] = Empty;
            for (int y = tank.Y; y < tank.Y + tank.H; y++)
                for (int x = tank.X; x < tank.X + tank.W; x++)
                    occ[y * W + x] = Tank;

            var placed = new List<Placed>();
            var single = new List<Vector2Int[]> { new[] { new Vector2Int(0, 0) } };
            var lines = LineShapes(straightMax);
            var ls = LShapes(elbowMax);
            var shorts = LineShapes(2);

            // Dependency appetite: breathers avoid stacking, harder levels seek it.
            double depWeight = levelNumber == 1 ? -3 : breather ? (levelNumber < 20 ? -0.4 : 0.1 + target * 0.06) : 0.35 + target * 0.12;

            Placed FindBest(Kind kind, List<Vector2Int[]> shapes)
            {
                Placed best = null;
                double bestScore = double.NegativeInfinity;
                foreach (var shape in shapes)
                {
                    int sw = shape.Max(c => c.x) + 1, sh = shape.Max(c => c.y) + 1;
                    for (int oy = 0; oy + sh <= H; oy++)
                    {
                        for (int ox = 0; ox + sw <= W; ox++)
                        {
                            var cells = new Vector2Int[shape.Length];
                            bool free = true;
                            for (int i = 0; i < shape.Length; i++)
                            {
                                cells[i] = new Vector2Int(shape[i].x + ox, shape[i].y + oy);
                                if (occ[cells[i].y * W + cells[i].x] != Empty) free = false;
                            }
                            if (!free) continue;
                            var own = new HashSet<int>(cells.Select(c => c.y * W + c.x));
                            foreach (var dir in DirectionExtensions.All)
                            {
                                var step = dir.ToOffset();
                                var path = new HashSet<int>();
                                bool clear = true;
                                foreach (var c in cells)
                                {
                                    for (int x = c.x + step.x, y = c.y + step.y; x >= 0 && y >= 0 && x < W && y < H; x += step.x, y += step.y)
                                    {
                                        int idx = y * W + x;
                                        if (own.Contains(idx)) continue;
                                        if (occ[idx] != Empty) { clear = false; break; }
                                        path.Add(idx);
                                    }
                                    if (!clear) break;
                                }
                                if (!clear) continue;

                                var blocks = new List<int>();
                                for (int j = 0; j < placed.Count; j++)
                                    if (cells.Any(c => placed[j].Path.Contains(c.y * W + c.x))) blocks.Add(j);
                                int height = 1;
                                foreach (int j in blocks) height = Math.Max(height, 1 + placed[j].Height);
                                int adj = 0;
                                foreach (var c in cells)
                                {
                                    foreach (var a in new[] { new Vector2Int(c.x + 1, c.y), new Vector2Int(c.x - 1, c.y), new Vector2Int(c.x, c.y + 1), new Vector2Int(c.x, c.y - 1) })
                                    {
                                        if (a.x < 0 || a.y < 0 || a.x >= W || a.y >= H) continue;
                                        int ai = a.y * W + a.x;
                                        if (!own.Contains(ai) && occ[ai] != Empty) adj++;
                                    }
                                }
                                bool horizontalShape = shape[0].y == (shape.Length > 1 ? shape[1] : shape[0]).y;
                                bool axial = kind == Kind.Line && ((dir == Direction.Left || dir == Direction.Right) == horizontalShape);

                                double score = 0;
                                score += depWeight * (blocks.Count > 0 ? 1 : 0) + 0.2 * Math.Min(blocks.Count, 3) * Math.Sign(depWeight);
                                score += height <= targetDepth ? 0.7 * ((double)height / targetDepth) : -0.9 * (height - targetDepth);
                                score += 0.22 * adj + 0.12 * cells.Length + (axial ? 0.25 : 0);
                                score += rng.Next() * 0.9;
                                if (score > bestScore)
                                {
                                    bestScore = score;
                                    best = new Placed { Cells = cells, Dir = dir, Kind = kind, Height = height, Blocks = blocks, Path = path };
                                }
                            }
                        }
                    }
                }
                return best;
            }

            foreach (var kind in kinds)
            {
                var shapes = kind == Kind.Single ? single : kind == Kind.Line ? lines : ls;
                // Crowded late placements fall back to a short straight rather than dropping the pipe.
                var best = FindBest(kind, shapes) ?? (kind != Kind.Single ? FindBest(Kind.Line, shorts) : null);
                if (best == null) continue;
                foreach (var c in best.Cells) occ[c.y * W + c.x] = placed.Count;
                placed.Add(best);
            }
            if (placed.Count < Math.Max(3, n - 2)) return null;

            // ------------------------------------------------------------ mechanics overlay
            int N = placed.Count;
            int ExitStep(int k) => N - 1 - k; // canonical order: last placed leaves first
            var blockedBy = new List<List<int>>();
            for (int k = 0; k < N; k++) blockedBy.Add(new List<int>());
            for (int j = 0; j < N; j++)
                foreach (int k in placed[j].Blocks) blockedBy[k].Add(j);
            int Ancestors(int k)
            {
                var seen = new HashSet<int>();
                var st = new List<int>(blockedBy[k]);
                while (st.Count > 0)
                {
                    int j = st[st.Count - 1];
                    st.RemoveAt(st.Count - 1);
                    if (!seen.Add(j)) continue;
                    st.AddRange(blockedBy[j]);
                }
                return seen.Count;
            }

            var type = new PipeType?[N];
            var ice = new int[N];
            var target2 = new int[N];
            List<int> Free(Func<int, bool> pred)
            {
                var list = new List<int>();
                for (int k = 0; k < N; k++)
                    if (type[k] == null && pred(k)) list.Add(k);
                return list;
            }
            int PickPref(List<int> list, Func<int, bool> prefer)
            {
                var good = list.Where(prefer).ToList();
                if (good.Count > 0) return rng.Pick(good);
                return list.Count > 0 ? rng.Pick(list) : -1;
            }

            for (int k = 0; k < N; k++)
            {
                if (placed[k].Kind != Kind.Single) continue;
                type[k] = PipeType.RotaryValve;
                var d = placed[k].Dir;
                int r = rng.Int(1, 3);
                for (int i = 0; i < r; i++) d = d.RotateCCW();
                placed[k].Dir = d;
            }
            for (int i = 0; i < plan.LockedPairs; i++)
            {
                int lockK = PickPref(Free(k => ExitStep(k) >= 1 && placed[k].Kind != Kind.Single), k => blockedBy[k].Count == 0);
                if (lockK < 0) break;
                type[lockK] = PipeType.Locked;
                int keyK = PickPref(Free(k => k > lockK && placed[k].Kind != Kind.Single), k => placed[k].Kind == Kind.Line);
                if (keyK < 0)
                {
                    type[lockK] = null;
                    break;
                }
                type[keyK] = PipeType.KeyValve;
                target2[keyK] = lockK;
            }
            for (int i = 0; i < plan.Frozen; i++)
            {
                int k = PickPref(Free(q => ExitStep(q) >= 1 && placed[q].Kind == Kind.Line), q => blockedBy[q].Count == 0);
                if (k < 0) break;
                type[k] = PipeType.Frozen;
                ice[k] = rng.Int(1, Math.Min(ExitStep(k), 3));
            }
            for (int i = 0; i < plan.Leaking; i++)
            {
                var pool = Free(q => placed[q].Kind != Kind.Single).OrderBy(Ancestors).ThenBy(ExitStep).ToList();
                var lPool = pool.Where(q => placed[q].Kind == Kind.L && Ancestors(q) <= 2).ToList();
                int k = lPool.Count > 0 ? lPool[0] : pool.Count > 0 && Ancestors(pool[0]) <= 2 ? pool[0] : -1;
                if (k < 0) break;
                type[k] = PipeType.Leaking;
            }
            for (int i = 0; i < plan.Rusty; i++)
            {
                int k = PickPref(Free(q => placed[q].Kind != Kind.Single), q => placed[q].Kind == Kind.L);
                if (k < 0) break;
                type[k] = PipeType.Rusty;
            }
            for (int i = 0; i < plan.Dry; i++)
            {
                int k = PickPref(Free(q => placed[q].Kind != Kind.Single), q => placed[q].Blocks.Count > 0);
                if (k < 0) break;
                type[k] = PipeType.Dry;
            }
            for (int k = 0; k < N; k++)
                if (type[k] == null) type[k] = placed[k].Kind == Kind.L ? PipeType.Elbow : PipeType.Straight;
            if (!type.Any(t => t.Value.IsWater())) return null;

            // ------------------------------------------------------------ ids in reading order
            var order = Enumerable.Range(0, N).ToList();
            Func<int, int> topY = k => placed[k].Cells.Min(c => c.y);
            Func<int, int> topX = k => placed[k].Cells.Where(c => c.y == topY(k)).Min(c => c.x);
            order.Sort((a, b) => topY(a) != topY(b) ? topY(a) - topY(b) : topX(a) - topX(b));
            var idOf = new string[N];
            for (int i = 0; i < N; i++) idOf[order[i]] = "p" + (i + 1);

            var level = new LevelData { Level = levelNumber, Width = W, Height = H, Tank = tank, Leaks = GameRules.DefaultLeaks };
            foreach (int k in order)
            {
                ShapeAnalyzer.Analyze(placed[k].Cells, out var path);
                var pd = new PipeData { Id = idOf[k], Type = type[k].Value, Cells = path, Dir = placed[k].Dir };
                if (pd.Type == PipeType.Frozen) pd.IceCount = ice[k];
                if (pd.Type == PipeType.KeyValve) pd.TargetId = idOf[target2[k]];
                if (pd.Type == PipeType.Leaking) pd.LeakTimerMoves = GameRules.DefaultLeakTimer;
                level.Pipes.Add(pd);
            }
            var canonical = new List<string>();
            for (int k = N - 1; k >= 0; k--) canonical.Add(idOf[k]);
            return new BuildResult { Level = level, Plan = plan, TargetDepth = targetDepth, CanonicalOrder = canonical };
        }

        // ------------------------------------------------------------------ acceptance + driver
        public static bool MeetsConstraints(int level, SolverResult res, MechanicPlan plan)
        {
            if (!res.Valid || !res.Solvable || !res.Perfect) return false;
            var m = res.Metrics;
            if (m.InitialExitableCount < 1) return false;
            if (level <= 3 && m.MaxChainDepth > 2) return false;
            if (Progression.IsBreather(level) && (double)m.InitialExitableCount / m.PipeCount < 0.4) return false;
            bool Showcased(PipeType t, int planned) =>
                !(level - t.UnlockLevel() <= 1 && level >= t.UnlockLevel() && planned > 0 && m.CountOf(t) == 0);
            return Showcased(PipeType.Elbow, plan.Elbows) && Showcased(PipeType.Dry, plan.Dry) && Showcased(PipeType.Rusty, plan.Rusty)
                && Showcased(PipeType.Frozen, plan.Frozen) && Showcased(PipeType.Locked, plan.LockedPairs)
                && Showcased(PipeType.Leaking, plan.Leaking) && Showcased(PipeType.RotaryValve, plan.Valves);
        }

        public static uint DefaultSeed(int level) => unchecked((uint)((level * 7919) ^ 0x5eed));

        /// <summary>
        /// Generates one validated level: tries <paramref name="attempts"/> deterministic sub-seeds and keeps the
        /// candidate whose DifficultyScore is closest to <see cref="DifficultyCurve.Target"/>.
        /// </summary>
        public static GeneratedLevel Generate(int levelNumber, int attempts = 40, uint? seed = null)
        {
            uint s = seed ?? DefaultSeed(levelNumber);
            double target = DifficultyCurve.Target(levelNumber);
            GeneratedLevel best = null;
            for (int a = 0; a < attempts; a++)
            {
                var rng = new DeterministicRandom(unchecked(s + (uint)a * 104729u));
                var built = BuildOnce(levelNumber, rng, target);
                if (built == null) continue;
                var res = LevelSolver.Solve(built.Level);
                if (!MeetsConstraints(levelNumber, res, built.Plan)) continue;
                double err = Math.Abs(res.Metrics.DifficultyScore - target);
                if (best == null || err < best.Error - 1e-9)
                {
                    best = new GeneratedLevel
                    {
                        Level = built.Level, Result = res, Target = target, Error = err, Attempt = a, Seed = s, CanonicalOrder = built.CanonicalOrder,
                    };
                }
                if (err <= 0.25) break;
            }
            if (best == null) throw new InvalidOperationException("could not generate a valid level " + levelNumber + " in " + attempts + " attempts");
            return best;
        }
    }
}
