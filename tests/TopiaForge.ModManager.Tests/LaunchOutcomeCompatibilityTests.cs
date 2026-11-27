using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class LaunchOutcomeCompatibilityTests
    {
        internal static void Run(string repositoryRoot)
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot,
                "tests", "fixtures", "multiplayer", "progress-v2", "forward-compat.json")));
            var root = Directory.CreateTempSubdirectory("topiaforge-outcome-compat-");
            var paths = new ManagerPaths(Path.Combine(root.FullName, "BepInEx"));
            var store = new LaunchStagingStore(paths);
            var count = 0;
            foreach (var item in fixture.RootElement.GetProperty("valid").EnumerateArray())
            {
                var id = item.GetProperty("id").GetString()!;
                var value = LaunchTransportJson.ReadOutcome(item.GetProperty("text").GetString()!);
                var expected = item.GetProperty("expected").GetString()!;
                Assert(LaunchTransportJson.WriteOutcome(value) == expected, id + " normalizes without private text");
                Assert(value.Multiplayer!.Detail == LaunchOutcomeMultiplayer.OtherDetail, id + " has fixed advice");
                // The game store is a producer, not an outcome reader. Rewriting a parsed record through
                // its real atomic writer must publish only canonical values, never the future raw reason.
                store.WriteOutcome(value);
                var staged = File.ReadAllText(store.OutcomePath(value.RequestId, value.Kind == "session"));
                Assert(staged == expected, id + " stages canonical bytes");
                Assert(LaunchTransportJson.WriteOutcome(LaunchTransportJson.ReadOutcome(staged)) == expected,
                    id + " is stable when read again");
                count++;
            }
            foreach (var item in fixture.RootElement.GetProperty("invalid").EnumerateArray())
            {
                try
                {
                    LaunchTransportJson.ReadOutcome(item.GetProperty("text").GetString()!);
                }
                catch (InvalidDataException error)
                {
                    Assert(error.InnerException == null, "Refusals do not retain untrusted parser exceptions.");
                    Assert(!new[] { "203.0.113.77", "secret.example.org", "fixture-secret", "future-private" }
                        .Any(marker => error.ToString().Contains(marker, StringComparison.Ordinal)), "refusals do not repeat private input");
                    count++;
                    continue;
                }
                throw new InvalidOperationException(item.GetProperty("id").GetString() + " must remain invalid");
            }
            foreach (var role in new[] { "host", "join" })
            {
                foreach (var end in new[] { false, true })
                {
                    try
                    {
                        _ = new LaunchOutcomeMultiplayer(role, "intent", failure: end ? null : "future-value",
                            endReason: end ? "future-value" : null);
                    }
                    catch (ArgumentException) { count++; continue; }
                    throw new InvalidOperationException("Writers must not accept unknown reasons.");
                }
            }
            Console.WriteLine("Outcome compatibility: " + count + " cases passed (readers, canonical staging, strict refusals).");
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
