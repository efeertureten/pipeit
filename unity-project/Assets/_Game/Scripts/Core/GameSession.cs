using System.Collections.Generic;
using UnityEngine;

namespace SuUstasi.Core
{
    public enum SessionStatus
    {
        Playing,
        Won,
        Lost,
    }

    public enum TapKind
    {
        Ignored,
        Exit,
        Blocked,
        Locked,
        Frozen,
        RustBroken,
        ValveRotated,
    }

    public enum ExitCause
    {
        Tap,
        Valve,
        Hammer,
        Analysis,
    }

    public enum GameEventKind
    {
        Removed,
        IceCracked,
        Thawed,
        Unlocked,
        LeakBurst,
        ValveExit,
        Won,
        Lost,
    }

    /// <summary>Side effect produced by the rules; the view layer plays these as animations/SFX.</summary>
    public sealed class GameEvent
    {
        public GameEventKind Kind;
        public string PipeId;
        public ExitCause Cause;
        public bool Water;
        public int IceCount;
        public int LeakCost;
        public int Combo;
        /// <summary>Unlocked: the key valve that opened the lock.</summary>
        public string By;

        public override string ToString() => Kind + (PipeId != null ? ":" + PipeId : "");
    }

    /// <summary>What a single tap did.</summary>
    public sealed class TapOutcome
    {
        public TapKind Kind;
        public string PipeId;
        public int LeakCost;
        public int Combo;
        public List<string> BlockingPipeIds = new List<string>();
        public List<Vector2Int> SweptCells = new List<Vector2Int>();
        public bool HitsTank;
        /// <summary>ValveRotated: new nozzle direction / will it slide out after <see cref="Delay"/>.</summary>
        public Direction Dir;
        public bool AutoExit;
        public float Delay;
        public List<GameEvent> Events = new List<GameEvent>();
    }

    public sealed class GameSessionOptions
    {
        /// <summary>null = derive from level number (levels 1..3).</summary>
        public bool? Ftue;
        /// <summary>Solver/tests: rotary valves leave immediately instead of after 0.5s.</summary>
        public bool InstantValves;
        /// <summary>Override the level's starting leak lives (the solver uses a large pool to count losses).</summary>
        public int? Leaks;
    }

    /// <summary>
    /// Pure, deterministic rules for one level attempt (mirrors web-playable/js/core.js GameSession).
    /// Views call Tap / Hammer / Patch and play the returned events; logic never waits for animations.
    /// </summary>
    public sealed class GameSession
    {
        public LevelData Level { get; }
        public int LevelNumber => Level.Level;
        public Board Board { get; }
        public int Leaks { get; private set; }
        public int MaxLeaks => GameRules.MaxLeaks;
        public bool Ftue { get; }
        public bool InstantValves { get; }
        public int InitialWaterCells { get; }
        public int RemainingWaterCells { get; private set; }
        public bool HasLeaking { get; }
        public int Taps { get; private set; }
        public int Mistakes { get; private set; }
        public int Exits { get; private set; }
        public int Combo { get; private set; }
        public SessionStatus Status { get; private set; } = SessionStatus.Playing;

        /// <summary>Tank fill = remaining water-bearing cells / initial water-bearing cells.</summary>
        public float WaterRatio => InitialWaterCells > 0 ? (float)RemainingWaterCells / InitialWaterCells : 0f;

        public int RemainingWaterPipes
        {
            get
            {
                int n = 0;
                foreach (var p in Board.Pipes)
                    if (p.IsWater && !p.Removed) n++;
                return n;
            }
        }

        public GameSession(LevelData level, GameSessionOptions options = null)
        {
            options = options ?? new GameSessionOptions();
            Level = level;
            Board = new Board(level);
            Leaks = options.Leaks ?? level.Leaks;
            Ftue = options.Ftue ?? level.Level <= GameRules.FtueLevels;
            InstantValves = options.InstantValves;
            foreach (var p in Board.Pipes)
            {
                if (p.IsWater) InitialWaterCells += p.Size;
                if (p.Type == PipeType.Leaking) HasLeaking = true;
            }
            RemainingWaterCells = InitialWaterCells;
        }

        /// <summary>True when tapping this pipe now would make it leave the board (used by XRay).</summary>
        public bool WouldExitOnTap(Pipe pipe)
        {
            if (pipe == null || pipe.Removed || pipe.PendingExit) return false;
            if (pipe.Type == PipeType.Locked && pipe.IsLocked) return false;
            if (pipe.Type == PipeType.Frozen && pipe.IceCount > 0) return false;
            if (pipe.Type == PipeType.Rusty && pipe.RustIntact) return false;
            if (pipe.Type == PipeType.RotaryValve) return Board.Scan(pipe, pipe.Dir.RotateCW()).CanExit;
            return Board.Scan(pipe).CanExit;
        }

        public List<string> ExitablePipeIds()
        {
            var ids = new List<string>();
            foreach (var p in Board.Pipes)
                if (WouldExitOnTap(p)) ids.Add(p.Id);
            return ids;
        }

        public TapOutcome Tap(string id)
        {
            var pipe = Board.Get(id);
            if (Status != SessionStatus.Playing || pipe == null || pipe.Removed || pipe.PendingExit)
                return new TapOutcome { Kind = TapKind.Ignored, PipeId = id };

            Taps++;
            var events = new List<GameEvent>();
            TapOutcome outcome;
            if (pipe.Type == PipeType.Locked && pipe.IsLocked)
            {
                outcome = Penalize(pipe, TapKind.Locked, new List<string>());
            }
            else if (pipe.Type == PipeType.Frozen && pipe.IceCount > 0)
            {
                outcome = Penalize(pipe, TapKind.Frozen, new List<string>());
            }
            else if (pipe.Type == PipeType.Rusty && pipe.RustIntact)
            {
                pipe.RustIntact = false; // free: never costs a leak, even when blocked
                outcome = new TapOutcome { Kind = TapKind.RustBroken, PipeId = id };
            }
            else if (pipe.Type == PipeType.RotaryValve)
            {
                pipe.Dir = pipe.Dir.RotateCW();
                var scan = Board.Scan(pipe);
                outcome = new TapOutcome
                {
                    Kind = TapKind.ValveRotated, PipeId = id, Dir = pipe.Dir, AutoExit = scan.CanExit, Delay = GameTiming.ValveAutoExitDelay,
                };
                if (scan.CanExit)
                {
                    if (InstantValves) ExitPipe(pipe, ExitCause.Valve, events);
                    else pipe.PendingExit = true;
                }
            }
            else
            {
                var scan = Board.Scan(pipe);
                if (scan.CanExit)
                {
                    Combo++;
                    outcome = new TapOutcome { Kind = TapKind.Exit, PipeId = id, Combo = Combo, SweptCells = scan.SweptCells };
                    ExitPipe(pipe, ExitCause.Tap, events);
                }
                else
                {
                    outcome = Penalize(pipe, TapKind.Blocked, scan.BlockingPipeIds);
                    outcome.HitsTank = scan.HitsTank;
                    outcome.SweptCells = scan.SweptCells;
                }
            }

            if (Status == SessionStatus.Playing) TickLeakTimers(events);
            CheckLose(events);
            outcome.Events = events;
            return outcome;
        }

        /// <summary>Rotary valve slide-out, called by the view GameTiming.ValveAutoExitDelay after rotation.</summary>
        public List<GameEvent> CompleteValveExit(string id)
        {
            var events = new List<GameEvent>();
            var pipe = Board.Get(id);
            if (pipe == null || pipe.Removed || !pipe.PendingExit || Status != SessionStatus.Playing) return events;
            Combo++;
            events.Add(new GameEvent { Kind = GameEventKind.ValveExit, PipeId = id, Combo = Combo });
            ExitPipe(pipe, ExitCause.Valve, events);
            return events;
        }

        /// <summary>Hammer booster: removes any pipe regardless of blockers. Counts as an exit, not a tap.</summary>
        public List<GameEvent> Hammer(string id)
        {
            var pipe = Board.Get(id);
            if (Status != SessionStatus.Playing || pipe == null || pipe.Removed) return null;
            var events = new List<GameEvent>();
            ExitPipe(pipe, ExitCause.Hammer, events);
            return events;
        }

        /// <summary>Patch booster: +1 leak life, capped at <see cref="GameRules.MaxLeaks"/>.</summary>
        public bool Patch()
        {
            if (Status != SessionStatus.Playing || Leaks >= MaxLeaks) return false;
            Leaks++;
            return true;
        }

        /// <summary>Continue after a loss (rewarded ad or a Patch on the fail screen).</summary>
        public bool Revive(int lives = 1)
        {
            if (Status != SessionStatus.Lost) return false;
            Leaks = System.Math.Min(MaxLeaks, System.Math.Max(1, lives));
            Status = SessionStatus.Playing;
            return true;
        }

        /// <summary>Removes a pipe with full exit side effects. Used by the solver's layered peeling.</summary>
        public List<GameEvent> ForceExit(Pipe pipe, ExitCause cause = ExitCause.Analysis)
        {
            var events = new List<GameEvent>();
            if (!pipe.Removed) ExitPipe(pipe, cause, events);
            return events;
        }

        private TapOutcome Penalize(Pipe pipe, TapKind kind, List<string> blockers)
        {
            Combo = 0;
            Mistakes++;
            int cost = Ftue ? 0 : 1;
            Leaks -= cost;
            return new TapOutcome { Kind = kind, PipeId = pipe.Id, LeakCost = cost, BlockingPipeIds = blockers };
        }

        private void ExitPipe(Pipe pipe, ExitCause cause, List<GameEvent> events)
        {
            Board.Remove(pipe);
            Exits++;
            if (pipe.IsWater) RemainingWaterCells -= pipe.Size;
            events.Add(new GameEvent { Kind = GameEventKind.Removed, PipeId = pipe.Id, Cause = cause, Water = pipe.IsWater });

            foreach (var other in Board.Pipes)
            {
                if (other.Removed || other.Type != PipeType.Frozen || other.IceCount <= 0) continue;
                other.IceCount--;
                events.Add(new GameEvent
                {
                    Kind = other.IceCount == 0 ? GameEventKind.Thawed : GameEventKind.IceCracked, PipeId = other.Id, IceCount = other.IceCount,
                });
            }

            if (pipe.Type == PipeType.KeyValve && pipe.TargetId != null)
            {
                var target = Board.Get(pipe.TargetId);
                if (target != null && !target.Removed && target.IsLocked)
                {
                    target.IsLocked = false;
                    events.Add(new GameEvent { Kind = GameEventKind.Unlocked, PipeId = target.Id, By = pipe.Id });
                }
            }

            if (Status == SessionStatus.Playing && RemainingWaterPipes == 0)
            {
                Status = SessionStatus.Won;
                events.Add(new GameEvent { Kind = GameEventKind.Won });
            }
        }

        private void TickLeakTimers(List<GameEvent> events)
        {
            foreach (var p in Board.Pipes)
            {
                if (p.Removed || p.Type != PipeType.Leaking) continue;
                p.LeakTimer--;
                if (p.LeakTimer > 0) continue;
                Mistakes++;
                int cost = Ftue ? 0 : 1;
                Leaks -= cost;
                p.LeakTimer = p.LeakTimerMax; // GDD: deduct one life and reset
                events.Add(new GameEvent { Kind = GameEventKind.LeakBurst, PipeId = p.Id, LeakCost = cost });
            }
        }

        private void CheckLose(List<GameEvent> events)
        {
            if (Status != SessionStatus.Playing || Leaks > 0) return;
            Leaks = 0;
            Status = SessionStatus.Lost;
            events.Add(new GameEvent { Kind = GameEventKind.Lost });
        }
    }
}
