using System;
using System.Collections.Generic;
using SuUstasi.Core;

namespace SuUstasi.LevelGen
{
    public sealed class SolverMove
    {
        public string PipeId;
        /// <summary>"exit" | "break_rust" | "rotate"</summary>
        public string Action;

        public override string ToString() => Action == "exit" ? PipeId : PipeId + "(" + Action + ")";
    }

    public sealed class DifficultyMetrics
    {
        public int PipeCount;
        public int WaterPipeCount;
        public int WaterCells;
        /// <summary>Longest dependency chain = number of simultaneous-removal layers.</summary>
        public int MaxChainDepth;
        public List<int> LayerSizes = new List<int>();
        /// <summary>Pipes a first tap would remove.</summary>
        public int InitialExitableCount;
        /// <summary>Pipes whose arrow points at their nearest board edge yet are blocked, / all non-valve pipes.</summary>
        public double DeceptiveArrowRatio;
        /// <summary>Pipe count per type, in first-appearance order.</summary>
        public List<KeyValuePair<PipeType, int>> Mechanics = new List<KeyValuePair<PipeType, int>>();
        public double MechanicLoad;
        public int SolutionTaps;
        public int LeaksLostPerfectPlay;
        /// <summary>1–10, see <see cref="DifficultyScorer"/>.</summary>
        public double DifficultyScore;

        public int CountOf(PipeType t)
        {
            foreach (var kv in Mechanics)
                if (kv.Key == t) return kv.Value;
            return 0;
        }
    }

    public sealed class SolverResult
    {
        public bool Valid;
        public List<string> Errors = new List<string>();
        /// <summary>Clearable from the initial state without running out of leak lives.</summary>
        public bool Solvable;
        /// <summary>Solvable with zero lives lost (leak timers included).</summary>
        public bool Perfect;
        public List<SolverMove> Solution = new List<SolverMove>();
        public int LeaksLost;
        public DifficultyMetrics Metrics;
    }

    /// <summary>1..10 difficulty from structural metrics (same weights as solver.js difficultyScore).</summary>
    public static class DifficultyScorer
    {
        private static double Clamp01(double v) => Math.Max(0, Math.Min(1, v));

        public static double Score(DifficultyMetrics m)
        {
            double fCount = Clamp01((m.PipeCount - 3) / 17.0);
            double fDepth = Clamp01((m.MaxChainDepth - 1) / 7.0);
            double fOpen = 1 - Clamp01((double)m.InitialExitableCount / Math.Max(1, m.PipeCount));
            double fDecep = Clamp01(m.DeceptiveArrowRatio / 0.5);
            double fMech = Clamp01(m.MechanicLoad / 6);
            double raw = 0.22 * fCount + 0.28 * fDepth + 0.18 * fOpen + 0.12 * fDecep + 0.2 * fMech;
            return JsMath.Round1(1 + 9 * raw);
        }

        public static double MechanicWeight(PipeType t)
        {
            switch (t)
            {
                case PipeType.Elbow: return 0.15;
                case PipeType.Dry: return 0.3;
                case PipeType.Rusty: return 0.45;
                case PipeType.Frozen: return 0.8;
                case PipeType.Locked: return 0.9;
                case PipeType.KeyValve: return 0.2;
                case PipeType.Leaking: return 1.1;
                case PipeType.RotaryValve: return 0.8;
                default: return 0;
            }
        }
    }

    /// <summary>
    /// Deterministic solver. Removal is monotone (an exit only frees cells, thaws ice and opens
    /// locks), so "repeatedly remove anything removable" reaches the unique fixpoint — greedy is
    /// exact for solvability. Leak timers are the only order-sensitive rule, so the play-out
    /// orders moves by urgency and reports how many lives that perfect play loses.
    /// Mirrors web-playable/js/solver.js.
    /// </summary>
    public static class LevelSolver
    {
        private const int MaxTaps = 2000;

        public static SolverResult Solve(string json)
        {
            if (!LevelJson.TryParse(json, out var level, out var errors)) return new SolverResult { Valid = false, Errors = errors };
            return Solve(level);
        }

        public static SolverResult Solve(LevelData level)
        {
            var errors = LevelValidator.Validate(level);
            if (errors.Count > 0) return new SolverResult { Valid = false, Errors = errors };

            // --- perfect-play simulation (no FTUE protection: honest leak accounting)
            var s = new GameSession(level, new GameSessionOptions { Ftue = false, InstantValves = true, Leaks = 99 });
            var solution = new List<SolverMove>();
            while (s.Status == SessionStatus.Playing && solution.Count < MaxTaps)
            {
                var mv = ChooseMove(s);
                if (mv == null) break;
                var res = s.Tap(mv.PipeId);
                if (res.Kind == TapKind.Ignored || res.LeakCost > 0) break; // planner never makes losing taps
                solution.Add(new SolverMove
                {
                    PipeId = mv.PipeId,
                    Action = res.Kind == TapKind.Exit ? "exit" : res.Kind == TapKind.RustBroken ? "break_rust" : "rotate",
                });
            }
            bool won = s.Status == SessionStatus.Won;
            int leaksLost = 99 - s.Leaks;

            // --- metrics on a fresh board
            var fresh = new GameSession(level, new GameSessionOptions { Ftue = false, InstantValves = true });
            var peel = PeelLayers(level);
            var m = new DifficultyMetrics
            {
                PipeCount = fresh.Board.Pipes.Count,
                WaterCells = fresh.InitialWaterCells,
                MaxChainDepth = peel.Layers,
                LayerSizes = peel.LayerSizes,
                InitialExitableCount = fresh.ExitablePipeIds().Count,
                DeceptiveArrowRatio = JsMath.Round3(DeceptiveArrowRatio(fresh)),
                SolutionTaps = solution.Count,
                LeaksLostPerfectPlay = leaksLost,
            };
            var counts = new List<KeyValuePair<PipeType, int>>();
            foreach (var p in fresh.Board.Pipes)
            {
                if (p.IsWater) m.WaterPipeCount++;
                int i = counts.FindIndex(kv => kv.Key == p.Type);
                if (i < 0) counts.Add(new KeyValuePair<PipeType, int>(p.Type, 1));
                else counts[i] = new KeyValuePair<PipeType, int>(p.Type, counts[i].Value + 1);
            }
            m.Mechanics = counts;
            double load = 0;
            foreach (var kv in counts) load = load + DifficultyScorer.MechanicWeight(kv.Key) * kv.Value;
            m.MechanicLoad = JsMath.Round2(load);
            m.DifficultyScore = DifficultyScorer.Score(m);

            return new SolverResult
            {
                Valid = true,
                Solvable = won && peel.Complete && leaksLost < level.Leaks,
                Perfect = won && leaksLost == 0,
                Solution = solution,
                LeaksLost = leaksLost,
                Metrics = m,
            };
        }

        /// <summary>Replays tap ids on a fresh session (FTUE off). Returns the final status.</summary>
        public static SessionStatus Replay(LevelData level, IEnumerable<string> pipeIds, out GameSession session)
        {
            session = new GameSession(level, new GameSessionOptions { Ftue = false, InstantValves = true });
            foreach (var id in pipeIds)
                if (session.Tap(id).Kind == TapKind.Ignored) break;
            return session.Status;
        }

        // ------------------------------------------------------------------ planner
        private static int ValveExitTaps(Board board, Pipe pipe)
        {
            var d = pipe.Dir;
            for (int k = 1; k <= 4; k++)
            {
                d = d.RotateCW();
                if (board.Scan(pipe, d).CanExit) return k;
            }
            return 0;
        }

        /// <summary>Pipes that must leave before <paramref name="pipe"/> can (transitive blockers + key valves).</summary>
        public static HashSet<string> BlockerClosure(GameSession session, Pipe pipe)
        {
            var board = session.Board;
            var result = new HashSet<string>();
            var stack = new Stack<Pipe>();
            stack.Push(pipe);
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                List<string> blockers;
                if (p.Type == PipeType.RotaryValve)
                {
                    List<string> best = null;
                    foreach (var d in DirectionExtensions.All)
                    {
                        var s = board.Scan(p, d);
                        if (s.HitsTank) continue;
                        if (best == null || s.BlockingPipeIds.Count < best.Count) best = s.BlockingPipeIds;
                    }
                    blockers = best ?? new List<string>();
                }
                else
                {
                    blockers = board.Scan(p).BlockingPipeIds;
                }
                var extra = new List<string>();
                if (p.Type == PipeType.Locked && p.IsLocked)
                {
                    foreach (var k in board.Pipes)
                    {
                        if (!k.Removed && k.Type == PipeType.KeyValve && k.TargetId == p.Id) { extra.Add(k.Id); break; }
                    }
                }
                foreach (var id in Concat(blockers, extra))
                {
                    if (!result.Add(id)) continue;
                    stack.Push(board.Get(id));
                }
            }
            return result;
        }

        private static IEnumerable<string> Concat(List<string> a, List<string> b)
        {
            foreach (var x in a) yield return x;
            foreach (var x in b) yield return x;
        }

        private static int DependentsCount(GameSession session, Pipe pipe)
        {
            var board = session.Board;
            int n = 0;
            foreach (var o in board.Pipes)
            {
                if (o.Removed || o == pipe) continue;
                if (o.Type == PipeType.Locked && o.IsLocked && pipe.Type == PipeType.KeyValve && pipe.TargetId == o.Id) { n++; continue; }
                if (o.Type == PipeType.RotaryValve)
                {
                    foreach (var d in DirectionExtensions.All)
                        if (board.Scan(o, d).BlockingPipeIds.Contains(pipe.Id)) { n++; break; }
                }
                else if (board.Scan(o).BlockingPipeIds.Contains(pipe.Id))
                {
                    n++;
                }
            }
            return n;
        }

        private static SolverMove ChooseMove(GameSession session)
        {
            var board = session.Board;
            var live = new List<Pipe>();
            foreach (var p in board.Pipes)
                if (!p.Removed) live.Add(p);

            Pipe urgent = null;
            foreach (var p in live)
            {
                if (p.Type != PipeType.Leaking) continue;
                if (urgent == null || p.LeakTimer < urgent.LeakTimer || (p.LeakTimer == urgent.LeakTimer && p.Index < urgent.Index)) urgent = p;
            }
            var urgentSet = urgent != null ? BlockerClosure(session, urgent) : new HashSet<string>();
            bool iceWaiting = live.Exists(p => p.Type == PipeType.Frozen && p.IceCount > 0);

            // 1) direct exits
            Pipe best = null;
            double bestScore = double.NegativeInfinity;
            foreach (var p in live)
            {
                if (!session.WouldExitOnTap(p)) continue;
                int deps = DependentsCount(session, p);
                double score = 0;
                if (urgent != null && p == urgent) score += 10000;
                if (urgentSet.Contains(p.Id)) score += 5000;
                if (p.IsWater) score += 100;
                if (deps > 0) score += 50 + deps;
                if (p.Type == PipeType.KeyValve) score += 40;
                if (iceWaiting) score += 10;
                if (!p.IsWater && deps == 0 && !iceWaiting) continue; // useless dry pipe
                score -= p.Index * 1e-3;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = p;
                }
            }
            if (best != null) return new SolverMove { PipeId = best.Id, Action = best.Type == PipeType.RotaryValve ? "rotate" : "exit" };

            // 2) break rust (free), preferring pipes that could leave right after
            var rusty = live.FindAll(p => p.Type == PipeType.Rusty && p.RustIntact);
            if (rusty.Count > 0)
            {
                Func<Pipe, int> rank = p => (board.Scan(p).CanExit ? 2 : 0) + (urgentSet.Contains(p.Id) ? 1 : 0);
                rusty.Sort((a, b) => rank(b) != rank(a) ? rank(b) - rank(a) : a.Index - b.Index);
                return new SolverMove { PipeId = rusty[0].Id, Action = "break_rust" };
            }

            // 3) rotate a valve toward a clear direction 2+ taps away
            Pipe bestValve = null;
            int bestK = 0;
            foreach (var p in live)
            {
                if (p.Type != PipeType.RotaryValve || p.PendingExit) continue;
                int k = ValveExitTaps(board, p);
                if (k == 0) continue;
                if (bestValve == null || Better(p, k, bestValve, bestK, urgentSet))
                {
                    bestValve = p;
                    bestK = k;
                }
            }
            if (bestValve != null) return new SolverMove { PipeId = bestValve.Id, Action = "rotate" };

            // 4) fallback: any remaining exit — never harmful, keeps parity with PeelLayers
            foreach (var p in live)
                if (session.WouldExitOnTap(p)) return new SolverMove { PipeId = p.Id, Action = p.Type == PipeType.RotaryValve ? "rotate" : "exit" };
            return null;
        }

        private static bool Better(Pipe a, int ka, Pipe b, int kb, HashSet<string> urgentSet)
        {
            int ua = urgentSet.Contains(a.Id) ? 1 : 0, ub = urgentSet.Contains(b.Id) ? 1 : 0;
            if (ua != ub) return ua > ub;
            if (ka != kb) return ka < kb;
            return a.Index < b.Index;
        }

        // ------------------------------------------------------------------ metrics
        public struct PeelResult
        {
            public int Layers;
            public List<int> LayerSizes;
            public bool Complete;
        }

        /// <summary>Remove every removable pipe simultaneously, repeat; layers until no water = MaxChainDepth.</summary>
        public static PeelResult PeelLayers(LevelData level)
        {
            var s = new GameSession(level, new GameSessionOptions { Ftue = false, InstantValves = true });
            var b = s.Board;
            var result = new PeelResult { LayerSizes = new List<int>() };
            while (s.RemainingWaterPipes > 0)
            {
                var layer = new List<Pipe>();
                foreach (var p in b.Pipes)
                {
                    if (p.Removed) continue;
                    if (p.Type == PipeType.Locked && p.IsLocked) continue;
                    if (p.Type == PipeType.Frozen && p.IceCount > 0) continue;
                    if (p.Type == PipeType.RotaryValve)
                    {
                        bool any = false;
                        foreach (var d in DirectionExtensions.All) any |= b.Scan(p, d).CanExit;
                        if (any) layer.Add(p);
                    }
                    else if (b.Scan(p).CanExit)
                    {
                        layer.Add(p); // rust is assumed broken: it never blocks progress
                    }
                }
                if (layer.Count == 0)
                {
                    result.Complete = false;
                    return result;
                }
                foreach (var p in layer) s.ForceExit(p);
                result.Layers++;
                result.LayerSizes.Add(layer.Count);
            }
            result.Complete = true;
            return result;
        }

        public static double DeceptiveArrowRatio(GameSession session)
        {
            var b = session.Board;
            int candidates = 0, deceptive = 0;
            foreach (var p in b.Pipes)
            {
                if (p.Type == PipeType.RotaryValve) continue;
                candidates++;
                double cx = 0, cy = 0;
                foreach (var c in p.Cells) { cx += c.x; cy += c.y; }
                cx /= p.Size;
                cy /= p.Size;
                double left = cx, right = b.Width - 1 - cx, up = cy, down = b.Height - 1 - cy;
                double min = Math.Min(Math.Min(left, right), Math.Min(up, down));
                double toward = p.Dir == Direction.Left ? left : p.Dir == Direction.Right ? right : p.Dir == Direction.Up ? up : down;
                if (Math.Abs(toward - min) < 1e-9 && !b.Scan(p).CanExit) deceptive++;
            }
            return candidates == 0 ? 0 : (double)deceptive / candidates;
        }
    }
}
