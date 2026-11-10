using System.Linq;
using NUnit.Framework;
using SuUstasi.Core;
using static SuUstasi.Tests.TestLevels;

namespace SuUstasi.Tests
{
    public class GameSessionTests
    {
        [Test]
        public void Exit_FreesCellsImmediately_SoChainTapsNeverWait()
        {
            var s = new GameSession(Level8());
            Assert.That(s.Tap("p6").Kind, Is.EqualTo(TapKind.Blocked));
            Assert.That(s.Tap("p2").Kind, Is.EqualTo(TapKind.Exit));
            Assert.That(s.Board.PipeAt(4, 4), Is.Null);
            Assert.That(s.Tap("p5").Kind, Is.EqualTo(TapKind.Exit));
            Assert.That(s.Tap("p6").Kind, Is.EqualTo(TapKind.Exit));
        }

        [Test]
        public void BlockedTap_CostsOneLeak_AndReportsBlockers()
        {
            var s = new GameSession(Level8());
            var r = s.Tap("p6");
            Assert.That(r.LeakCost, Is.EqualTo(1));
            Assert.That(r.BlockingPipeIds, Is.EqualTo(new[] { "p5" }));
            Assert.That(s.Leaks, Is.EqualTo(2));
            Assert.That(s.Mistakes, Is.EqualTo(1));
            Assert.That(s.Combo, Is.Zero);
        }

        [Test]
        public void TankRatio_IsRemainingOverInitialWaterCells()
        {
            var s = new GameSession(Level8()); // 13 water cells
            s.Tap("p1");
            Assert.That(s.WaterRatio, Is.EqualTo(11f / 13f).Within(1e-6));
            s.Tap("p3");
            s.Tap("p4");
            Assert.That(s.WaterRatio, Is.EqualTo(6f / 13f).Within(1e-6));
        }

        [Test]
        public void Combo_CountsConsecutiveExits()
        {
            var s = new GameSession(Level8());
            Assert.That(s.Tap("p1").Combo, Is.EqualTo(1));
            Assert.That(s.Tap("p2").Combo, Is.EqualTo(2));
            s.Tap("p6"); // blocked resets
            Assert.That(s.Tap("p3").Combo, Is.EqualTo(1));
        }

        [Test]
        public void Win_WhenAllWaterPipesCleared_DryMayRemain()
        {
            var s = Session(P("w", PipeType.Straight, Direction.Up, 0, 0, 1, 0), P("d", PipeType.Dry, Direction.Down, 0, 5, 1, 5));
            Assert.That(s.InitialWaterCells, Is.EqualTo(2));
            var r = s.Tap("w");
            Assert.That(s.Status, Is.EqualTo(SessionStatus.Won));
            Assert.That(r.Events.Any(e => e.Kind == GameEventKind.Won), Is.True);
        }

        [Test]
        public void DryPipe_BlocksUntilRemoved_AndDrainsNothing()
        {
            var s = Session(P("w", PipeType.Straight, Direction.Right, 0, 0, 0, 1), P("d", PipeType.Dry, Direction.Up, 4, 0, 4, 1));
            Assert.That(s.Tap("w").Kind, Is.EqualTo(TapKind.Blocked));
            Assert.That(s.Tap("d").Kind, Is.EqualTo(TapKind.Exit));
            Assert.That(s.WaterRatio, Is.EqualTo(1f));
            Assert.That(s.Tap("w").Kind, Is.EqualTo(TapKind.Exit));
        }

        [Test]
        public void Lose_AtZeroLeaks_ThenInputIgnored()
        {
            var s = new GameSession(Level8());
            s.Tap("p6");
            s.Tap("p6");
            var r = s.Tap("p6");
            Assert.That(s.Status, Is.EqualTo(SessionStatus.Lost));
            Assert.That(r.Events.Any(e => e.Kind == GameEventKind.Lost), Is.True);
            Assert.That(s.Tap("p1").Kind, Is.EqualTo(TapKind.Ignored));
        }

        [Test]
        public void Ftue_Levels1To3_NeverLoseLeaks()
        {
            var level = Level8();
            level.Level = 2;
            var s = new GameSession(level);
            Assert.That(s.Ftue, Is.True);
            for (int i = 0; i < 5; i++) Assert.That(s.Tap("p6").LeakCost, Is.Zero);
            Assert.That(s.Leaks, Is.EqualTo(3));
            Assert.That(s.Status, Is.EqualTo(SessionStatus.Playing));
            Assert.That(s.Mistakes, Is.EqualTo(5), "mistakes still count (no flawless bonus)");
        }

        [Test]
        public void Rusty_FirstTapBreaksRustForFree_EvenWhenBlocked()
        {
            var s = Session(P("r", PipeType.Rusty, Direction.Right, 0, 0, 0, 1), P("b", PipeType.Straight, Direction.Up, 3, 0, 3, 1));
            var r1 = s.Tap("r");
            Assert.That(r1.Kind, Is.EqualTo(TapKind.RustBroken));
            Assert.That(r1.LeakCost, Is.Zero);
            Assert.That(s.Leaks, Is.EqualTo(3));
            Assert.That(s.Tap("r").Kind, Is.EqualTo(TapKind.Blocked), "now behaves as a normal water pipe");
            s.Tap("b");
            Assert.That(s.Tap("r").Kind, Is.EqualTo(TapKind.Exit));
        }

        [Test]
        public void Frozen_ThawsAfterOtherExits_EarlyTapCostsALeak()
        {
            var f = P("f", PipeType.Frozen, Direction.Up, 0, 0, 1, 0);
            f.IceCount = 2;
            var s = Session(f, P("a", PipeType.Straight, Direction.Down, 0, 5, 1, 5), P("b", PipeType.Straight, Direction.Right, 5, 4, 5, 5));
            Assert.That(s.Tap("f").Kind, Is.EqualTo(TapKind.Frozen));
            Assert.That(s.Leaks, Is.EqualTo(2));
            s.Tap("a");
            Assert.That(s.Board.Get("f").IceCount, Is.EqualTo(1));
            var r = s.Tap("b");
            Assert.That(r.Events.Any(e => e.Kind == GameEventKind.Thawed && e.PipeId == "f"), Is.True);
            Assert.That(s.Tap("f").Kind, Is.EqualTo(TapKind.Exit));
        }

        [Test]
        public void Locked_OpensOnlyAfterItsKeyValveExits()
        {
            var key = P("k", PipeType.KeyValve, Direction.Down, 0, 5, 1, 5);
            key.TargetId = "l";
            var s = Session(P("l", PipeType.Locked, Direction.Up, 0, 0, 1, 0), key);
            Assert.That(s.Tap("l").Kind, Is.EqualTo(TapKind.Locked));
            Assert.That(s.Leaks, Is.EqualTo(2));
            var r = s.Tap("k");
            Assert.That(r.Events.Any(e => e.Kind == GameEventKind.Unlocked && e.PipeId == "l" && e.By == "k"), Is.True);
            Assert.That(s.Tap("l").Kind, Is.EqualTo(TapKind.Exit));
            Assert.That(s.Status, Is.EqualTo(SessionStatus.Won));
        }

        [Test]
        public void Leaking_TimerDropsOnEveryTap_BurstsAndResets()
        {
            var g = P("g", PipeType.Leaking, Direction.Right, 0, 0, 0, 1);
            g.LeakTimerMoves = 3;
            var s = Session(g, P("blk", PipeType.Straight, Direction.Up, 3, 0, 3, 1), P("x", PipeType.Straight, Direction.Left, 0, 5, 1, 5));
            s.Tap("g"); // blocked: -1 leak, timer 2
            Assert.That(s.Board.Get("g").LeakTimer, Is.EqualTo(2));
            s.Tap("x"); // timer 1
            var r = s.Tap("blk"); // timer 0 -> burst
            Assert.That(r.Events.Any(e => e.Kind == GameEventKind.LeakBurst), Is.True);
            Assert.That(s.Leaks, Is.EqualTo(1));
            Assert.That(s.Board.Get("g").LeakTimer, Is.EqualTo(3));
            Assert.That(s.Tap("g").Kind, Is.EqualTo(TapKind.Exit));
        }

        [Test]
        public void RotaryValve_RotatesClockwiseForFree_ThenAutoExitsAfterDelay()
        {
            var s = Session(P("v", PipeType.RotaryValve, Direction.Left, 0, 0), P("b", PipeType.Straight, Direction.Right, 3, 0, 4, 0));
            var r = s.Tap("v");
            Assert.That(r.Kind, Is.EqualTo(TapKind.ValveRotated));
            Assert.That(s.Board.Get("v").Dir, Is.EqualTo(Direction.Up));
            Assert.That(r.AutoExit, Is.True);
            Assert.That(r.Delay, Is.EqualTo(0.5f));
            Assert.That(r.LeakCost, Is.Zero);
            Assert.That(s.Board.Get("v").PendingExit, Is.True);
            Assert.That(s.Tap("v").Kind, Is.EqualTo(TapKind.Ignored), "input locked while it slides");
            var events = s.CompleteValveExit("v");
            Assert.That(events.Any(e => e.Kind == GameEventKind.Removed), Is.True);
            Assert.That(s.Board.PipeAt(0, 0), Is.Null);
        }

        [Test]
        public void Boosters_Hammer_IsAnExitNotATap_Patch_CapsAtFour()
        {
            var f = P("f", PipeType.Frozen, Direction.Up, 0, 0, 1, 0);
            f.IceCount = 1;
            var s = Session(f, P("a", PipeType.Straight, Direction.Right, 0, 4, 0, 5), P("b", PipeType.Straight, Direction.Down, 2, 5, 3, 5));
            Assert.That(s.Tap("a").Kind, Is.EqualTo(TapKind.Blocked));
            s.Hammer("a");
            Assert.That(s.Board.Get("a").Removed, Is.True);
            Assert.That(s.Board.Get("f").IceCount, Is.Zero, "hammer counts as an exit for ice");
            Assert.That(s.Taps, Is.EqualTo(1));
            Assert.That(s.Patch(), Is.True);
            Assert.That(s.Leaks, Is.EqualTo(3));
            Assert.That(s.Patch(), Is.True);
            Assert.That(s.Patch(), Is.False);
            Assert.That(s.Leaks, Is.EqualTo(4));
        }

        [Test]
        public void XRay_ListsExactlyThePipesATapWouldRemove()
        {
            Assert.That(new GameSession(Level8()).ExitablePipeIds(), Is.EqualTo(new[] { "p1", "p2", "p3" }));
        }

        [Test]
        public void Revive_AfterLoss()
        {
            var s = new GameSession(Level8());
            s.Tap("p6"); s.Tap("p6"); s.Tap("p6");
            Assert.That(s.Revive(1), Is.True);
            Assert.That(s.Status, Is.EqualTo(SessionStatus.Playing));
            Assert.That(s.Leaks, Is.EqualTo(1));
        }
    }
}
