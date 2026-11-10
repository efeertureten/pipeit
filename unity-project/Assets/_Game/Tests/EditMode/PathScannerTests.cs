using NUnit.Framework;
using SuUstasi.Core;
using UnityEngine;
using static SuUstasi.Tests.TestLevels;

namespace SuUstasi.Tests
{
    public class PathScannerTests
    {
        [Test]
        public void ClearStraightPipe_CanExit_AndReportsSweptCellsToTheEdge()
        {
            var s = Session(P("a", PipeType.Straight, Direction.Right, 0, 0, 1, 0));
            var r = s.Board.Scan(s.Board.Get("a"));
            Assert.That(r.CanExit, Is.True);
            Assert.That(r.BlockingPipeIds, Is.Empty);
            Assert.That(r.SweptCells, Is.EqualTo(new[] { new Vector2Int(2, 0), new Vector2Int(3, 0), new Vector2Int(4, 0), new Vector2Int(5, 0) }));
        }

        [Test]
        public void OwnCells_NeverBlock()
        {
            var s = Session(P("a", PipeType.Straight, Direction.Left, 0, 0, 1, 0, 2, 0));
            Assert.That(s.Board.Scan(s.Board.Get("a")).CanExit, Is.True);
        }

        [Test]
        public void Blockers_AreNearestFirst_AndDistinct()
        {
            var s = Session(
                P("a", PipeType.Straight, Direction.Right, 0, 0, 0, 1),
                P("far", PipeType.Straight, Direction.Up, 5, 0, 5, 1),
                P("near", PipeType.Straight, Direction.Up, 2, 1, 3, 1));
            var r = s.Board.Scan(s.Board.Get("a"));
            Assert.That(r.CanExit, Is.False);
            Assert.That(r.BlockingPipeIds, Is.EqualTo(new[] { "near", "far" }));
        }

        [Test]
        public void Elbow_WholeBodySweeps_GddLevel8()
        {
            var s = new GameSession(Level8());
            var r = s.Board.Scan(s.Board.Get("p4"));
            Assert.That(r.CanExit, Is.False);
            Assert.That(r.BlockingPipeIds, Is.EqualTo(new[] { "p3" }));
            Assert.That(r.SweptCells, Is.EqualTo(new[] { new Vector2Int(5, 1) }));
        }

        [Test]
        public void PerpendicularSlide_SweepsEveryBodyCell()
        {
            var s = Session(P("a", PipeType.Straight, Direction.Down, 0, 4, 1, 4), P("b", PipeType.Straight, Direction.Right, 1, 5, 2, 5));
            Assert.That(s.Board.Scan(s.Board.Get("a")).BlockingPipeIds, Is.EqualTo(new[] { "b" }));
        }

        [Test]
        public void TankCells_Block_AndAreFlagged()
        {
            var s = Session(P("a", PipeType.Straight, Direction.Left, 0, 2, 1, 2));
            var r = s.Board.Scan(s.Board.Get("a"), Direction.Right);
            Assert.That(r.HitsTank, Is.True);
            Assert.That(r.CanExit, Is.False);
        }

        [Test]
        public void DirectionRotation_IsClockwise()
        {
            Assert.That(Direction.Up.RotateCW(), Is.EqualTo(Direction.Right));
            Assert.That(Direction.Right.RotateCW(), Is.EqualTo(Direction.Down));
            Assert.That(Direction.Down.RotateCW(), Is.EqualTo(Direction.Left));
            Assert.That(Direction.Left.RotateCW(), Is.EqualTo(Direction.Up));
            Assert.That(Direction.Up.ToOffset(), Is.EqualTo(new Vector2Int(0, -1)), "y grows down");
        }
    }
}
