using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Replays tests/fixtures/multiplayer-admission/cases.json, which the Dart planner replays too, so both
    /// languages must produce identical admission reports, messages and order included.
    /// </summary>
    internal static class MultiplayerAdmissionParityTests
    {
        public static void Run()
        {
            var path = Path.Combine(
                Program.FindRepoRoot(), "tests", "fixtures", "multiplayer-admission", "cases.json");
            var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            Assert(document["schemaVersion"]!.GetValue<int>() == 1, "unknown parity fixture schemaVersion");

            var manifestBase = document["manifestBase"]!.AsObject();
            var mods = new Dictionary<string, MultiplayerAdmissionMod>(StringComparer.Ordinal);
            foreach (var entry in document["mods"]!.AsObject())
            {
                mods.Add(entry.Key, ReadMod(manifestBase, entry.Value!.AsObject()));
            }

            var defaults = document["defaults"]!.AsObject();
            var names = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var node in document["cases"]!.AsArray())
            {
                var testCase = node!.AsObject();
                var name = testCase["name"]!.GetValue<string>();
                Assert(names.Add(name), "duplicate parity case name: " + name);
                var report = MultiplayerAdmissionPlanner.Evaluate(
                    ReadProfile("server", testCase["server"]!.AsObject(), defaults, mods),
                    ReadProfile("client", testCase["client"]!.AsObject(), defaults, mods),
                    ReadPolicy(testCase["policy"]!.GetValue<string>()),
                    ReadServerRole(testCase["serverRole"]!.GetValue<string>()));
                AssertReport(name, testCase["expect"]!.AsObject(), report);
                count++;
            }

            Assert(count > 0, "the parity fixture must contain cases");
            Console.WriteLine("MultiplayerAdmissionParityTests passed (" + count + " cases).");
        }

        private static MultiplayerAdmissionMod ReadMod(JsonObject manifestBase, JsonObject mod)
        {
            var manifest = (JsonObject)manifestBase.DeepClone();
            foreach (var field in mod["manifest"]!.AsObject())
            {
                manifest[field.Key] = field.Value?.DeepClone();
            }

            return new MultiplayerAdmissionMod(
                ModManifestJson.Deserialize(manifest.ToJsonString()),
                mod["packageSha256"]!.GetValue<string>());
        }

        private static MultiplayerAdmissionProfile ReadProfile(
            string peerId,
            JsonObject profile,
            JsonObject defaults,
            IReadOnlyDictionary<string, MultiplayerAdmissionMod> mods)
        {
            string Field(string key) => (profile[key] ?? defaults[key])!.GetValue<string>();
            var profileMods = profile["mods"]!.AsArray()
                .Select(key => mods.TryGetValue(key!.GetValue<string>(), out var mod)
                    ? mod
                    : throw new InvalidOperationException("Unknown parity fixture mod: " + key))
                .ToArray();
            return new MultiplayerAdmissionProfile(
                peerId,
                Field("gameBuild"),
                Field("protocolVersion"),
                Field("peerVersionRange"),
                profileMods);
        }

        private static MultiplayerAdmissionPolicy ReadPolicy(string value) => value switch
        {
            "compatible" => MultiplayerAdmissionPolicy.Compatible,
            "exactProfile" => MultiplayerAdmissionPolicy.ExactProfile,
            _ => throw new InvalidOperationException("Unknown parity fixture policy: " + value)
        };

        private static MultiplayerAdmissionServerRole ReadServerRole(string value) => value switch
        {
            "dedicatedServer" => MultiplayerAdmissionServerRole.DedicatedServer,
            "listenHost" => MultiplayerAdmissionServerRole.ListenHost,
            _ => throw new InvalidOperationException("Unknown parity fixture server role: " + value)
        };

        private static void AssertReport(string name, JsonObject expected, MultiplayerAdmissionReport report)
        {
            Assert(expected["admitted"]!.GetValue<bool>() == report.IsAdmitted, name + ": admitted");
            var active = expected["activeSessionMods"]!.AsArray().Select(item => item!.GetValue<string>());
            Assert(active.SequenceEqual(report.ActiveSessionMods, StringComparer.Ordinal),
                name + ": active session mods were [" + string.Join(", ", report.ActiveSessionMods) + "]");
            AssertMismatches(name + ": mismatches", expected["mismatches"]!.AsArray(), report.Mismatches);
            AssertMismatches(
                name + ": inactive session reasons",
                expected["inactiveSessionReasons"]!.AsArray(),
                report.InactiveSessionReasons);
        }

        private static void AssertMismatches(
            string label,
            JsonArray expected,
            IReadOnlyList<MultiplayerAdmissionMismatch> actual)
        {
            var actualRows = actual.Select(Describe).ToArray();
            var expectedRows = expected.Select(item =>
            {
                var row = item!.AsObject();
                return string.Join(
                    " | ",
                    row["code"]!.GetValue<string>(),
                    row["modId"]!.GetValue<string>(),
                    row["message"]!.GetValue<string>(),
                    row["serverValue"]!.GetValue<string>(),
                    row["clientValue"]!.GetValue<string>());
            }).ToArray();
            Assert(
                expectedRows.SequenceEqual(actualRows, StringComparer.Ordinal),
                label + " differ." + Environment.NewLine +
                "expected:" + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", expectedRows) +
                Environment.NewLine + "actual:" + Environment.NewLine + "  " +
                string.Join(Environment.NewLine + "  ", actualRows));
        }

        private static string Describe(MultiplayerAdmissionMismatch item)
        {
            var code = item.Code.ToString();
            return string.Join(
                " | ",
                char.ToLowerInvariant(code[0]) + code.Substring(1),
                item.ModId,
                item.Message,
                item.ServerValue,
                item.ClientValue);
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Multiplayer admission parity failed: " + message);
        }
    }
}
