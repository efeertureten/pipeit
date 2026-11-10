using System.IO;
using System.Linq;
using NUnit.Framework;
using SuUstasi.Core;
using SuUstasi.LevelGen;
using static SuUstasi.Tests.TestLevels;

namespace SuUstasi.Tests
{
    public class LevelSolverTests
    {
        [Test]
        public void GddLevel8_IsSolvable_WithGddMetrics()
        {
            var r = LevelSolver.Solve(Level8());
            Assert.That(r.Solvable, Is.True);
            Assert.That(r.Perfect, Is.True);
            Assert.That(r.Metrics.MaxChainDepth, Is.EqualTo(3));
            Assert.That(r.Metrics.LayerSizes, Is.EqualTo(new[] { 3, 2, 1 }));
            Assert.That(r.Metrics.InitialExitableCount, Is.EqualTo(3));
            Assert.That(r.Metrics.DeceptiveArrowRatio, Is.EqualTo(0.333).Within(1e-9));
            Assert.That(r.Metrics.DifficultyScore, Is.InRange(1.0, 10.0));
            Assert.That(LevelSolver.Replay(Level8(), r.Solution.Select(m => m.PipeId), out _), Is.EqualTo(SessionStatus.Won));
        }

        [Test]
        public void DetectsDeadlock()
        {
            var r = LevelSolver.Solve(Make(P("a", PipeType.Straight, Direction.Right, 0, 0, 1, 0), P("b", PipeType.Straight, Direction.Left, 5, 0, 5, 1)));
            Assert.That(r.Valid, Is.True);
            Assert.That(r.Solvable, Is.False);
        }

        [Test]
        public void PlaysTheLeakingPipeFirst()
        {
            var g = P("g", PipeType.Leaking, Direction.Down, 0, 5, 1, 5);
            g.LeakTimerMoves = 1;
            var r = LevelSolver.Solve(Make(P("a", PipeType.Straight, Direction.Up, 0, 0, 1, 0), P("b", PipeType.Straight, Direction.Up, 2, 0, 3, 0),
                P("c", PipeType.Straight, Direction.Up, 4, 0, 5, 0), g));
            Assert.That(r.Solution[0].PipeId, Is.EqualTo("g"));
            Assert.That(r.LeaksLost, Is.Zero);
        }

        [Test]
        public void DifficultyCurve_IsASawtooth()
        {
            foreach (int l in new[] { 10, 25, 50, 100 })
            {
                Assert.That(DifficultyCurve.Target(l), Is.LessThan(DifficultyCurve.Target(l - 1)), "breather " + l);
                Assert.That(DifficultyCurve.Target(l), Is.LessThan(DifficultyCurve.Target(l + 1)));
            }
            for (int l = 1; l <= 300; l++) Assert.That(DifficultyCurve.Target(l), Is.InRange(1.0, 10.0));
        }
    }

    public class LevelGeneratorTests
    {
        [Test]
        public void IsDeterministic()
        {
            Assert.That(LevelJson.ToJson(LevelGenerator.Generate(23).Level), Is.EqualTo(LevelJson.ToJson(LevelGenerator.Generate(23).Level)));
        }

        [TestCase(9)]
        [TestCase(14)]
        [TestCase(21)]
        [TestCase(47)]
        public void CanonicalReverseBuildOrder_IsAValidSolution(int level)
        {
            var g = LevelGenerator.Generate(level);
            var s = new GameSession(g.Level, new GameSessionOptions { Ftue = false, InstantValves = true });
            foreach (var id in g.CanonicalOrder)
            {
                var p = s.Board.Get(id);
                for (int k = 0; k < 4 && !p.Removed; k++) s.Tap(id); // rust: 2 taps, valves: rotate until out
            }
            Assert.That(s.Status, Is.EqualTo(SessionStatus.Won));
            Assert.That(s.Mistakes, Is.Zero, "canonical order must never need a blocked tap");
        }

        [TestCase(1)]
        [TestCase(12)]
        [TestCase(22)]
        [TestCase(30)]
        [TestCase(35)]
        [TestCase(61)]
        [TestCase(88)]
        public void GeneratedLevels_AreSolvablePerfectAndRespectUnlocks(int level)
        {
            var g = LevelGenerator.Generate(level);
            Assert.That(g.Result.Solvable && g.Result.Perfect, Is.True);
            Assert.That(LevelValidator.Validate(g.Level), Is.Empty);
            foreach (var p in g.Level.Pipes) Assert.That(level, Is.GreaterThanOrEqualTo(p.Type.UnlockLevel()), p.Type + " too early");
        }

        [Test]
        public void IntroLevels_ShowcaseTheirMechanic()
        {
            Assert.That(LevelGenerator.Generate(16).Result.Metrics.CountOf(PipeType.Rusty), Is.GreaterThan(0));
            Assert.That(LevelGenerator.Generate(22).Result.Metrics.CountOf(PipeType.Frozen), Is.GreaterThan(0));
            Assert.That(LevelGenerator.Generate(30).Result.Metrics.CountOf(PipeType.Locked), Is.GreaterThan(0));
            Assert.That(LevelGenerator.Generate(35).Result.Metrics.CountOf(PipeType.Leaking), Is.GreaterThan(0));
            Assert.That(LevelGenerator.Generate(61).Result.Metrics.CountOf(PipeType.RotaryValve), Is.GreaterThan(0));
        }

        [Test]
        public void BoardSizes_Scale5x5To9x11()
        {
            var a = LevelGenerator.BoardFor(1);
            var b = LevelGenerator.BoardFor(120);
            Assert.That(new[] { a.W, a.H, a.TankW, a.TankH }, Is.EqualTo(new[] { 5, 5, 2, 2 }));
            Assert.That(new[] { b.W, b.H, b.TankW, b.TankH }, Is.EqualTo(new[] { 9, 11, 3, 3 }));
        }

        [Test]
        public void Rng_MatchesJsMulberry32()
        {
            // reference values from web-playable/js/core.js mulberry32(42)
            var r = new DeterministicRandom(42);
            Assert.That(r.Next(), Is.EqualTo(0.6011037519201636).Within(1e-15));
            Assert.That(r.Next(), Is.EqualTo(0.44829055899754167).Within(1e-15));
        }
    }

    public class LevelPackTests
    {
        [Test]
        public void ShippedPack_IsValidSolvableAndCoversAllMechanics()
        {
            var failures = LevelPackBuilder.ValidateDirectory(LevelsDirectory(), out int n);
            Assert.That(failures, Is.Empty);
            Assert.That(n, Is.GreaterThanOrEqualTo(20));
        }

        [Test]
        public void ShippedLevel8_IsTheExactGddReference()
        {
            var text = File.ReadAllText(Path.Combine(LevelsDirectory(), "level_008.json")).Replace("\r\n", "\n");
            Assert.That(text, Is.EqualTo(GddReferenceLevels.Level8));
        }
    }
}
