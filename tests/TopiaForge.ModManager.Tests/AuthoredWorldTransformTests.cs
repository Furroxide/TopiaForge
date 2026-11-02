using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class AuthoredWorldTransformTests
    {
        internal static void Run()
        {
            using var map = new FakeAuthoredMap()
                .AddEntity("root", "Root", editorTransform: new MapTransform(new Vec3(10, 0, 0), new Vec3(0, 0, 90), new Vec3(2, 3, 4)))
                .AddEntity("generator", "Generator", "kit/machine", new MapTransform(new Vec3(1, 2, 3), new Vec3(0, 0, 45), new Vec3(1, 2, 1)), "root")
                .AddEntity("vent", "Vent", editorTransform: new MapTransform(new Vec3(1, 1, 0), Vec3.Zero, new Vec3(1, 1, 1)), parentId: "generator", hasMarker: false);
            var root = map.Entity("root"); var child = map.Entity("generator"); var vent = map.Entity("vent");
            Near(root.WorldTransform.Position, new Vec3(-10, 0, 0), "root conversion");
            Near(child.WorldTransform.Position, new Vec3(-4, 2, 12), "rotated nonuniform parent");
            var sqrt2 = (float)Math.Sqrt(2);
            Near(vent.WorldTransform.Position, new Vec3(-4 + 4.5f * sqrt2, 2 - sqrt2, 12), "nested shear retained for position without marker");
            Near(child.WorldTransform.Scale, new Vec3((float)Math.Sqrt(6.5), (float)Math.Sqrt(26), 4), "lossy scale from full basis");
            Near(child.WorldTransform.Rotation * new Vec3(1, 0, 0), new Vec3(-sqrt2 / 2, -sqrt2 / 2, 0), "quaternion chain");
            Near(child.Entity.Position, child.WorldTransform.Position, "fake live initial pose composes parents");
            Assert(!vent.Entity.IsAlive, "no synthetic live handle for absent marker");
            Near(child.EditorTransform.Position, new Vec3(1, 2, 3), "editor remains parent-local");
            Near(map.ToWorld(child.EditorTransform).Position, new Vec3(-1, 2, 3), "legacy conversion remains root-level");
            var anchor = new MapAnchor("machine", "old-id", "Generator", "kit/machine", new Vec3(1, 2, 3), positionTolerance: 0);
            Assert(map.ResolveAnchors(new[] { anchor })["machine"].Id == "generator", "anchor fallback retains parent-local coordinates");
            ((FakeEntity)child.Entity).Position = new Vec3(99, 98, 97);
            Near(child.WorldTransform.Position, new Vec3(-4, 2, 12), "authored pose does not track physics");
            map.Dispose();
            Expect<InvalidOperationException>(() => _ = child.WorldTransform, contains: "ended");
            Expect<InvalidOperationException>(() => _ = vent.WorldTransform, contains: "ended");
            ReflectionAndQuaternion();
            ImportRootAndLifetime();
            UnrepresentablePoseKeepsMap();
            LargeFiniteQuaternion();
            Console.WriteLine("Authored world transforms passed (affine parents, shear, reflection, quaternion, missing marker, root and teardown).");
        }
        private static void LargeFiniteQuaternion()
        {
            var quarterTurn = new MapTransform(Vec3.Zero, new Quat(1e20f, 0, 0, 1e20f), new Vec3(1, 1, 1));
            using var map = new FakeAuthoredMap()
                .AddEntity("large-rotation", "Large rotation", editorTransform: quarterTurn)
                .AddEntity("child", "Child", editorTransform: new MapTransform(new Vec3(0, 1, 0), Vec3.Zero, new Vec3(1, 1, 1)), parentId: "large-rotation")
                .AddEntity("second-turn", "Second turn", editorTransform: quarterTurn, parentId: "large-rotation")
                .AddEntity("grandchild", "Grandchild", editorTransform: new MapTransform(new Vec3(0, 1, 0), Vec3.Zero, new Vec3(1, 1, 1)), parentId: "second-turn");
            Near(map.Entity("child").WorldTransform.Position, new Vec3(0, 0, 1), "large finite quaternion preserves child placement");
            Near(map.Entity("child").Entity.Position, new Vec3(0, 0, 1), "large quaternion fake initial position");
            Near(map.Entity("grandchild").WorldTransform.Position, new Vec3(0, -1, 0), "large quaternion ancestor products stay normalized");
            Near(map.Entity("second-turn").WorldTransform.Rotation * new Vec3(0, 1, 0), new Vec3(0, -1, 0), "large quaternion world rotation");
            Near(map.ToWorld(quarterTurn).Rotation * new Vec3(0, 1, 0), new Vec3(0, 0, 1), "legacy conversion normalizes finite quaternion safely");
            using var parsed = FakeAuthoredMap.FromProject("{\"scenes\":{\"scene\":{\"entities\":{\"parent\":{\"components\":{\"transform\":{\"rotation\":[1e20,0,0,1e20]}}},\"child\":{\"parent\":\"parent\",\"components\":{\"transform\":{\"position\":[0,1,0]}}}}}}}");
            Near(parsed.Entity("child").WorldTransform.Position, new Vec3(0, 0, 1), "reader large quaternion parity");
            var huge = AuthoredAffine.From(Vec3.Zero, new Quat(float.MaxValue, 0, 0, float.MaxValue), new Vec3(1, 1, 1));
            Near(huge.Vector(new Vec3(0, 1, 0)), new Vec3(0, 0, 1), "largest finite quaternion normalizes without overflow");
            var scaled = AuthoredAffine.From(Vec3.Zero, new Quat(1, 0, 0, 1), new Vec3(1, 3e38f, 1));
            Assert(scaled.Y.IsFinite && Math.Abs(scaled.Y.Z / 3e38f - 1) < 0.0001f, "basis rotation avoids intermediate float overflow");
            using var cancellation = new FakeAuthoredMap()
                .AddEntity("parent", "Parent", editorTransform: new MapTransform(new Vec3(-3e38f, 0, 0), Vec3.Zero, new Vec3(3e38f, 1, 1)))
                .AddEntity("child", "Child", editorTransform: new MapTransform(new Vec3(2, 0, 0), Vec3.Zero, new Vec3(1, 1, 1)), parentId: "parent");
            var position = cancellation.Entity("child").WorldTransform.Position;
            Assert(position.IsFinite && Math.Abs(position.X / -3e38f - 1) < 0.0001f, "affine translation cancels before narrowing to float");
        }
        private static void UnrepresentablePoseKeepsMap()
        {
            using var map = new FakeAuthoredMap()
                .AddEntity("collapsed", "Collapsed", editorTransform: new MapTransform(new Vec3(10, 0, 0), Vec3.Zero, new Vec3(0, 2, 3)))
                .AddEntity("child", "Child", editorTransform: new MapTransform(new Vec3(1, 2, 3), new Vec3(0, 0, 45), new Vec3(1, 1, 1)), parentId: "collapsed")
                .AddEntity("large", "Large", editorTransform: new MapTransform(Vec3.Zero, Vec3.Zero, new Vec3(1e20f, 1, 1)))
                .AddEntity("overflow", "Overflow", editorTransform: new MapTransform(Vec3.Zero, Vec3.Zero, new Vec3(1e20f, 1, 1)), parentId: "large", hasMarker: false);
            Assert(map.IsAlive && map.Entities.Count == 4, "collapsed and overflow records do not reject map load");
            Near(map.Entity("child").Entity.Position, new Vec3(-10, 4, 9), "collapsed ancestor still composes fake position");
            Expect<InvalidOperationException>(() => _ = map.Entity("collapsed").WorldTransform, contains: "collapsed");
            Expect<InvalidOperationException>(() => _ = map.Entity("child").WorldTransform, contains: "collapsed");
            Expect<InvalidOperationException>(() => _ = map.Entity("overflow").WorldTransform, contains: "overflow");
            Assert(map.Entity("large").WorldTransform.Scale.X == 1e20f, "large finite scale survives extraction");
            using var parsed = FakeAuthoredMap.FromProject("{\"scenes\":{\"scene\":{\"entities\":{\"flat\":{\"components\":{\"transform\":{\"scale\":[0,1,1]}}}}}}}");
            Assert(parsed.Entities.Count == 1, "runtime reader accepts collapsed authored records");
            Expect<InvalidOperationException>(() => _ = parsed.Entity("flat").WorldTransform, contains: "collapsed");
        }
        private static void ReflectionAndQuaternion()
        {
            var half = (float)Math.Sqrt(0.5);
            using var map = new FakeAuthoredMap()
                .AddEntity("reflected", "Reflected", editorTransform: new MapTransform(new Vec3(10, 0, 0), Vec3.Zero, new Vec3(2, -3, 4)))
                .AddEntity("child", "Child", editorTransform: new MapTransform(new Vec3(1, 2, 3), Vec3.Zero, new Vec3(1, 1, 1)), parentId: "reflected")
                .AddEntity("euler", "Euler", editorTransform: new MapTransform(Vec3.Zero, new Vec3(0, 90, 0), new Vec3(1, 1, 1)))
                .AddEntity("quaternion", "Quaternion", editorTransform: new MapTransform(Vec3.Zero, new Quat(0, half * 2, 0, half * 2), new Vec3(1, 1, 1)))
                .AddEntity("e-child", "Euler child", editorTransform: new MapTransform(new Vec3(1, 2, 3), Vec3.Zero, new Vec3(1, 1, 1)), parentId: "euler")
                .AddEntity("q-child", "Quaternion child", editorTransform: new MapTransform(new Vec3(1, 2, 3), Vec3.Zero, new Vec3(1, 1, 1)), parentId: "quaternion");
            Near(map.Entity("child").WorldTransform.Position, new Vec3(-12, -6, 12), "reflected parent position");
            Near(map.Entity("child").WorldTransform.Scale, new Vec3(-2, 3, 4), "odd reflection sign assigned to X");
            Near(map.Entity("e-child").WorldTransform.Position, new Vec3(-3, 2, -1), "Euler converted before hierarchy composition");
            Near(map.Entity("e-child").WorldTransform.Position, map.Entity("q-child").WorldTransform.Position, "normalized quaternion/Euler parity");
        }
        private static void ImportRootAndLifetime()
        {
            var record = new AuthoredRecord("root", "scene", "Root", null, null, null,
                new MapTransform(new Vec3(1, 2, 3), Vec3.Zero, new Vec3(1, 1, 1)), AuthoredComponents.None);
            var scene = new AuthoredScene("scene", "Scene", "", new[] { record });
            // An already sheared/reflected import root; never reconstruct it from a lossy TRS.
            var import = new AuthoredAffine(new Vec3(20, 30, 40), new Vec3(0, 2, 0), new Vec3(3, 1, 0), new Vec3(0, 0, 4), Quat.Identity);
            var alive = true;
            using var map = new AuthoredMapIndex("map", scene, new MapRevision(new string('a', 64), 1, ""), Array.Empty<AuthoredMarker>(),
                AuthoredAffine.Compose(scene.Records, value => AuthoredAffine.From(new Vec3(-value.Position.X, value.Position.Y, value.Position.Z), Quat.Identity, value.Scale), import),
                value => value, value => TransformState.Identity, () => alive);
            var retained = map.Entity("root");
            Near(retained.WorldTransform.Position, new Vec3(26, 30, 52), "full import-root basis");
            Near(retained.WorldTransform.Scale, new Vec3(-2, (float)Math.Sqrt(10), 4), "root lossy scale");
            var bound = map.ForPackage(_ => OperationResult<byte[]>.Success(Array.Empty<byte>()));
            Assert(ReferenceEquals(retained, bound.Entity("root")), "package facade shares imported pose");
            alive = false;
            Expect<InvalidOperationException>(() => _ = retained.WorldTransform, contains: "ended");
        }
        private static void Near(Vec3 actual, Vec3 expected, string reason) =>
            Assert((actual - expected).Length < 0.0001f, reason + ": expected " + expected + ", got " + actual);
    }
}
