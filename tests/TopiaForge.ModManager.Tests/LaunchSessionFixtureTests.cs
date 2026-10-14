using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Replays tests/fixtures/multiplayer/{launch-v5,progress-v2,channels}, which the launcher's Dart tests replay too, so
    /// the game and the launcher read and write the same session documents byte for byte.
    /// </summary>
    internal static class LaunchSessionFixtureTests
    {
        internal static void Run(string repositoryRoot)
        {
            var failures = new List<Exception>();
            var root = Path.Combine(repositoryRoot, "tests", "fixtures", "multiplayer");
            using var launch = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "launch-v5", "cases.json")));
            using var outputs = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "progress-v2", "cases.json")));
            using var channels = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "channels", "cases.json")));
            var runs = 0;
            void Check(string name, Action test)
            {
                runs++;
                try { test(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(name + ": " + error.Message, error)); }
            }

            Replay("launch-v5", launch.RootElement, text => LaunchTransportJson.WriteProfileV5(LaunchTransportJson.ReadProfileV5(text)), Check);
            foreach (var item in launch.RootElement.GetProperty("valid").EnumerateArray())
            {
                Check("launch-v5 " + item.GetProperty("id").GetString() + " fields", () => LaunchFields(item));
            }

            Replay("progress-v2", outputs.RootElement.GetProperty("progress"),
                text => LaunchTransportJson.WriteProgress(LaunchTransportJson.ReadProgress(text)), Check);
            Replay("outcome-v2", outputs.RootElement.GetProperty("outcome"),
                text => LaunchTransportJson.WriteOutcome(LaunchTransportJson.ReadOutcome(text)), Check);
            Check("failure vocabulary", () =>
            {
                var codes = outputs.RootElement.GetProperty("failureCodes");
                Assert(Strings(codes.GetProperty("join")).SequenceEqual(LaunchContractValues.JoinFailures)
                    && Strings(codes.GetProperty("host")).SequenceEqual(LaunchContractValues.HostFailures)
                    && Strings(outputs.RootElement.GetProperty("endReasons")).SequenceEqual(LaunchContractValues.EndReasons),
                    "The game's failure and end-reason vocabularies are the fixture's.");
            });

            Replay("handoff", channels.RootElement.GetProperty("handoff"),
                text => LaunchTransportJson.WriteSessionHandoff(LaunchTransportJson.ReadSessionHandoff(text)), Check);
            Replay("join", channels.RootElement.GetProperty("join"),
                text => LaunchTransportJson.WriteSessionJoin(LaunchTransportJson.ReadSessionJoin(text)), Check);
            Replay("preferences", channels.RootElement.GetProperty("preferences"),
                text => LaunchTransportJson.WritePreferences(LaunchTransportJson.ReadPreferences(text)), Check);
            Replay("journal", channels.RootElement.GetProperty("journal"),
                text => LaunchTransportJson.WritePreferencesJournal(LaunchTransportJson.ReadPreferencesJournal(text)), Check);
            foreach (var item in channels.RootElement.GetProperty("journalApply").EnumerateArray())
            {
                // The launcher applies journals; the game only reads both documents, so each must read here too.
                Check("journal apply " + item.GetProperty("id").GetString(), () =>
                {
                    _ = LaunchTransportJson.ReadPreferences(item.GetProperty("projection").GetString()!);
                    _ = LaunchTransportJson.ReadPreferencesJournal(item.GetProperty("journal").GetString()!);
                    _ = LaunchTransportJson.ReadPreferences(item.GetProperty("result").GetString()!);
                });
            }

            Console.WriteLine("Launch session fixtures: " + (runs - failures.Count) + "/" + runs + " PASS");
            if (failures.Count != 0) throw new AggregateException("Launch session fixtures failed.", failures);
        }

        private static void Replay(string label, JsonElement section, Func<string, string> roundTrip, Action<string, Action> check)
        {
            foreach (var item in section.GetProperty("valid").EnumerateArray())
            {
                var id = label + " " + item.GetProperty("id").GetString();
                var bytes = item.GetProperty("bytes").GetString()!;
                check(id + " writes back exactly", () => Assert(roundTrip(bytes) == bytes, "The writer must reproduce the fixture's bytes."));
                check(id + " reads in any order", () =>
                    Assert(roundTrip(item.GetProperty("reordered").GetString()!) == bytes, "A reordered document must normalize to the bytes."));
            }

            foreach (var item in section.GetProperty("invalid").EnumerateArray())
            {
                var id = label + " " + item.GetProperty("id").GetString();
                check(id + " is refused", () =>
                {
                    try { roundTrip(item.GetProperty("text").GetString()!); }
                    catch (InvalidDataException) { return; }
                    throw new InvalidOperationException("Accepted: " + item.GetProperty("about").GetString());
                });
            }
        }

        private static void LaunchFields(JsonElement item)
        {
            var bytes = item.GetProperty("bytes").GetString()!;
            var request = LaunchTransportJson.ReadProfileV5(bytes);
            var expected = item.GetProperty("fields");
            var session = request.Session;
            string? Optional(string name) => expected.GetProperty(name).ValueKind == JsonValueKind.Null ? null : expected.GetProperty(name).GetString();
            Assert(request.Command == expected.GetProperty("command").GetString() && session.Role == expected.GetProperty("role").GetString()
                && request.Executable.Command == expected.GetProperty("executableCommand").GetString()
                && session.IntentId == expected.GetProperty("intentId").GetString()
                && session.DisplayName == expected.GetProperty("displayName").GetString()
                && session.Transport.Privacy == expected.GetProperty("privacy").GetString()
                && (session.Transport.DirectConsent != null) == expected.GetProperty("consented").GetBoolean()
                && session.Transport.Port == expected.GetProperty("port").GetInt32()
                && session.ServiceOrigin == Optional("serviceOrigin") && session.Target?.Kind == Optional("targetKind")
                && session.Host?.Name == Optional("hostName") && request.Packages.Count == expected.GetProperty("packageCount").GetInt32(),
                "Both readers must extract the same fields.");
            Assert(!request.Executable.SafeMode, "A session launch never runs in safe mode.");
            var consumed = LaunchTransportJson.ReadLaunchRequest(bytes);
            Assert(consumed.SchemaVersion == 5 && consumed.Session != null && consumed.Profile.Command == request.Executable.Command,
                "The request reader takes V5 with its session.");
            try { LaunchTransportJson.ReadProfile(bytes); }
            catch (InvalidDataException) { return; }
            throw new InvalidOperationException("The V4 reader must refuse V5.");
        }

        private static IEnumerable<string> Strings(JsonElement array) => array.EnumerateArray().Select(item => item.GetString()!);

        private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    }
}
