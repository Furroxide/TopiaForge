using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class AuthoredMapTests
    {
        internal static void Run(string repo)
        {
            var folder = Path.Combine(repo, "tests", "fixtures", "maps");
            var json = File.ReadAllText(Path.Combine(folder, "authored-project.json"));
            Conformance(folder, json);
            MarkerJoin(json);
            Hostile(json);
            CoordinatesAndLifetime();
            Console.WriteLine("AuthoredMapTests passed (8 shared anchor outcomes, reader bounds, marker joins, coordinates and lifetime).");
        }
        private static void Conformance(string folder, string json)
        {
            using var map = FakeAuthoredMap.FromProject(json);
            using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "authored-expected.json")));
            var root = expected.RootElement;
            Assert(map.Entities.Select(e => e.Id).SequenceEqual(root.GetProperty("order").EnumerateArray().Select(e => e.GetString())), "shared parent-first order");
            Assert(map.Environment == "day" && map.SceneName == "Workshop" && map.Revision.ModifiedTime == "2026-01-01T00:00:00Z", "scene metadata");
            var child = map.Entity("child");
            Assert(child.ParentId == "root" && child.EditorTransform.QuaternionRotation.HasValue && !child.EditorTransform.EulerDegrees.HasValue,
                "quaternion and hierarchy survive parsing");
            Assert(map.Entity("root").Components == (AuthoredComponents.PrefabInstance | AuthoredComponents.Aoi | AuthoredComponents.Agent), "component flags");
            map.WithAnchorFile("anchors.json", File.ReadAllText(Path.Combine(folder, "authored-anchors.json")));
            var resolution = map.ResolveAnchors("anchors.json");
            Assert(resolution.IsComplete == root.GetProperty("isComplete").GetBoolean(), "shared completeness");
            var outcomes = root.GetProperty("outcomes").EnumerateArray().ToArray();
            Assert(resolution.Anchors.Count == outcomes.Length, "all shared outcomes");
            for (var i = 0; i < outcomes.Length; i++)
            {
                var item = outcomes[i]; var actual = resolution.Anchors[i];
                Assert(actual.Anchor.Key == item.GetProperty("key").GetString() && actual.Match.ToString() == item.GetProperty("match").GetString() &&
                    actual.Entity?.Id == item.GetProperty("entityId").GetString() && actual.Changed == item.GetProperty("changed").GetBoolean(), "shared anchor " + i);
            }
            Assert(resolution["near"].Id == "crate" && resolution.Report.Contains("changed since survey") && resolution.Report.Contains("re-survey"), "actionable drift");
            Expect<InvalidOperationException>(() => _ = resolution["far"], contains: "far");
            Assert(map.ResolveAnchors(new[] { new MapAnchor("optional", "absent", required: false) }).IsComplete, "optional miss is complete");
            Assert(map.ResolveAnchors(new[] { new MapAnchor("x", "entry"), new MapAnchor("x", "crate") }).ErrorCode == ModErrorCode.InvalidArgument, "duplicate keys refused");
            Assert(map.ResolveAnchors("../anchors.json").ErrorCode == ModErrorCode.InvalidArgument && map.ResolveAnchors("missing.json").ErrorCode == ModErrorCode.InvalidArgument, "safe file refusals");
            map.WithAnchorFile("bad.json", "{\"schemaVersion\":1,\"anchors\":[{\"key\":\"x\",\"entityId\":\"entry\",\"required\":3}]}");
            Assert(map.ResolveAnchors("bad.json").ErrorCode == ModErrorCode.InvalidArgument, "malformed anchor refusal");
        }
        private static void MarkerJoin(string json)
        {
            var project = AuthoredProject.Read(Encoding.UTF8.GetBytes(json));
            var entity = new FakeEntity("live-entry", "Entry", Vec3.Zero);
            using var map = new AuthoredMapIndex("example-map", project.Scenes["workshop"], new MapRevision(new string('a', 64), 100, ""),
                new[] { new AuthoredMarker("entry", entity, false), new AuthoredMarker("unknown", new FakeEntity("extra", "Extra", Vec3.Zero), true),
                    new AuthoredMarker("crate", new FakeEntity("one", "One", Vec3.Zero), true), new AuthoredMarker("crate", new FakeEntity("two", "Two", Vec3.Zero), true) },
                p => p, t => TransformState.Identity);
            Assert(map.Entities.Count == 7 && map.MissingMarkers == 6 && map.UnknownMarkers == 1 && map.DuplicateMarkers == 1, "records are retained without unique live markers");
            Assert(!map.Entity("crate").Entity.IsAlive && !map.Entity("entry").AssetResolved, "duplicate and unresolved assets never pretend success");
            Assert(map.TryGetAuthored(entity, out var found) && found!.Id == "entry", "reverse lookup");
            Assert(!map.TryGetAuthored(new FakeEntity("live-entry", "Imposter", Vec3.Zero), out _), "id alone cannot impersonate a handle");
            Assert(map.FindByModel("kit/box").Count == 2, "model lookup keeps records without markers");
            Assert(!map.ResolveAnchors(new[] { new MapAnchor("crate", "crate") }).IsComplete, "required anchor needs a usable live entity");
            var bound = map.ForPackage(_ => OperationResult<byte[]>.Success(Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"anchors\":[]}")));
            Assert(bound.ResolveAnchors("anchors.json").IsComplete, "consumer package reader bound");
            var retained = map.Entity("entry"); map.Dispose();
            Assert(!bound.IsAlive && !retained.Entity.IsAlive && !map.TryGetEntity("entry", out _) && !map.TryGetAuthored(entity, out _), "views share session lifetime");
        }
        private static void Hostile(string json)
        {
            var deep = string.Join(",", Enumerable.Range(0, 130).Select(i => "\"e" + i.ToString("D3") + "\":{\"components\":{},\"parent\":" +
                (i == 0 ? "null" : "\"e" + (i - 1).ToString("D3") + "\"") + "}"));
            Expect<FormatException>(() => AuthoredProject.Read(Project(deep)));
            foreach (var bad in new[] { json.Replace("\"child\":", "\"bad/id\":"), json.Replace("\"Group\"", "\"" + new string('n', 129) + "\""),
                json.Replace("\"parent\": \"root\"", "\"parent\": \"absent\""), json.Replace("\"parent\": \"root\"", "\"parent\": \"child\""),
                json.Replace("\"x\": 12", "\"x\": 1e50"), json.Replace("\"x\": 12", "\"x\": 12, \"x\": 13"),
                json.Replace("\"x\": 12", "\"x\": \"12\"") })
                Expect<FormatException>(() => FakeAuthoredMap.FromProject(bad));
            Expect<ArgumentException>(() => new MapAnchor("x", "entry", positionTolerance: float.NaN));
            Expect<ArgumentException>(() => new MapTransform(Vec3.Zero, new Quat(0, 0, 0, 0), new Vec3(1, 1, 1)));
            var document = new MapDocument(Encoding.UTF8.GetBytes(json));
            Assert(document.Authored.Scenes["workshop"].Records.Count == 7, "native import consumes the same validated reader");
        }
        private static void CoordinatesAndLifetime()
        {
            var map = new FakeAuthoredMap().AddEntity("door", "Door", "kit/door");
            var live = map.Entity("door").Entity;
            Assert(map.ToWorld(new Vec3(2, 3, 4)).Equals(new Vec3(-2, 3, 4)), "handedness fixture pin");
            var transform = map.ToWorld(new MapTransform(new Vec3(2, 3, 4), new Quat(0, 0.6f, 0, 0.8f), new Vec3(2, 3, 4)));
            Assert(transform.Rotation.Equals(new Quat(0, -0.6f, 0, 0.8f)) && transform.Scale.Equals(new Vec3(2, 3, 4)), "quaternion reflection and scale");
            var world = new WorldReadiness(new WorldSceneIdentity(1, "workshop"), TransformState.Identity, map);
            Assert(ReferenceEquals(world.Map, map), "readiness retains the imported map");
            Assert(ReferenceEquals(world.RequireMap(), map), "readiness exposes map");
            Expect<InvalidOperationException>(() => new WorldReadiness(new WorldSceneIdentity(1, "other"), TransformState.Identity).RequireMap(), contains: "map world");
            map.Dispose();
            Assert(!live.IsAlive && !map.IsAlive && !map.TryGetEntity("door", out _) && map.FindByModel("kit/door").Count == 0, "teardown kills handles");
            Expect<InvalidOperationException>(() => map.ToWorld(Vec3.Zero), contains: "ended");
            Assert(map.ResolveAnchors(Array.Empty<MapAnchor>()).ErrorCode == ModErrorCode.InvalidState, "ended map refuses anchors");
        }
    }
}
