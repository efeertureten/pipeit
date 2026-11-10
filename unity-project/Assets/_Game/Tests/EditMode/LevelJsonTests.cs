using System.Linq;
using NUnit.Framework;
using SuUstasi.Core;
using SuUstasi.Core.Json;
using SuUstasi.LevelGen;
using UnityEngine;
using static SuUstasi.Tests.TestLevels;

namespace SuUstasi.Tests
{
    public class LevelJsonTests
    {
        [Test]
        public void ParsesGddLevel8()
        {
            var l = Level8();
            Assert.That(l.Level, Is.EqualTo(8));
            Assert.That(l.Width, Is.EqualTo(6));
            Assert.That(l.Tank.W, Is.EqualTo(2));
            Assert.That(l.Pipes.Count, Is.EqualTo(6));
            Assert.That(l.Pipes[3].Type, Is.EqualTo(PipeType.Elbow));
            Assert.That(l.Pipes[3].Cells, Is.EqualTo(new[] { new Vector2Int(4, 1), new Vector2Int(4, 2), new Vector2Int(5, 2) }));
        }

        [Test]
        public void Writer_RoundTripsByteForByte()
        {
            Assert.That(LevelJson.ToJson(Level8()), Is.EqualTo(GddReferenceLevels.Level8));
        }

        [Test]
        public void RejectsExitPathThroughTank()
        {
            var level = Make(P("a", PipeType.Straight, Direction.Right, 0, 2, 1, 2));
            Assert.That(LevelValidator.Validate(level).Any(e => e.Contains("crosses the tank")), Is.True);
        }

        [Test]
        public void RejectsOverlapsTankCellsBadShapesAndDanglingKeys()
        {
            Assert.That(LevelValidator.Validate(Make(P("a", PipeType.Straight, Direction.Up, 0, 0, 1, 0), P("b", PipeType.Straight, Direction.Up, 1, 0, 1, 1))), Is.Not.Empty);
            Assert.That(LevelValidator.Validate(Make(P("a", PipeType.Straight, Direction.Up, 2, 2, 2, 1))), Is.Not.Empty);
            Assert.That(LevelValidator.Validate(Make(P("a", PipeType.Straight, Direction.Up, 0, 0, 2, 0))), Is.Not.Empty);
            Assert.That(LevelValidator.Validate(Make(P("a", PipeType.Elbow, Direction.Up, 0, 0, 1, 0))), Is.Not.Empty);
            var key = P("k", PipeType.KeyValve, Direction.Up, 0, 0, 1, 0);
            key.TargetId = "nope";
            Assert.That(LevelValidator.Validate(Make(key)), Is.Not.Empty);
            Assert.That(LevelValidator.Validate(Make(P("l", PipeType.Locked, Direction.Up, 0, 0, 1, 0))), Is.Not.Empty);
        }

        [Test]
        public void ReportsSchemaErrorsInsteadOfThrowing()
        {
            Assert.That(LevelJson.TryParse("{\"level\":1}", out _, out var errors), Is.False);
            Assert.That(errors, Is.Not.Empty);
            Assert.That(LevelJson.TryParse("not json", out _, out errors), Is.False);
            Assert.Throws<LevelFormatException>(() => LevelJson.Parse("{\"level\":1,\"grid\":[6,6],\"tank\":{\"x\":2,\"y\":2,\"w\":2,\"h\":2},\"pipes\":[]}"));
        }

        [Test]
        public void ShapeAnalyzer_OrdersLArmCornerArm()
        {
            var kind = ShapeAnalyzer.Analyze(new[] { new Vector2Int(4, 1), new Vector2Int(5, 2), new Vector2Int(4, 2) }, out var path);
            Assert.That(kind, Is.EqualTo(ShapeKind.L));
            Assert.That(path, Is.EqualTo(new[] { new Vector2Int(5, 2), new Vector2Int(4, 2), new Vector2Int(4, 1) }));
        }

        [Test]
        public void MiniJson_RoundTrip()
        {
            var o = MiniJson.Parse("{\"a\":[1,2.5,\"x\\n\",true,null],\"b\":{\"c\":-3}}");
            Assert.That(MiniJson.Serialize(o), Is.EqualTo("{\"a\":[1,2.5,\"x\\n\",true,null],\"b\":{\"c\":-3}}"));
        }
    }
}
