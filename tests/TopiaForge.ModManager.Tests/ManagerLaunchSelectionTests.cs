using System;
using System.IO;
using System.Text.Json.Nodes;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class ManagerLaunchSelectionTests
    {
        internal static void Run()
        {
            foreach (var raw in new[] { "{}", "{\"worldLaunch\":null}",
                "{\"worldLaunch\":{\"selectedGamemodeId\":\"missing.mode\",\"loadMode\":\"future-transition\",\"autoLoadOnStart\":true,\"extra\":{\"nested\":[null,true,5]}}}" })
            {
                var state = ManagerStateLaunchPersistence.Parse(raw);
                state.Normalize();
                if (state.LaunchSelection?.Kind != "unresolved-legacy") throw new InvalidDataException("Missing or legacy manager choices must stay unresolved until unique validation succeeds.");
                if (!JsonNode.DeepEquals(JsonNode.Parse(raw), JsonNode.Parse(state.LaunchSelection.LegacyJson!)))
                    throw new InvalidDataException("Legacy values, nulls, unknown fields and property presence must survive normalization.");
                var again = ManagerStateLaunchPersistence.Parse(ManagerStateLaunchPersistence.Serialize(state));
                if (!JsonNode.DeepEquals(JsonNode.Parse(raw), JsonNode.Parse(again.LaunchSelection!.LegacyJson!)))
                    throw new InvalidDataException("Persisting a legacy selection must preserve its complete JSON value.");
            }
            foreach (var value in new[] { "null", "42", "[]", "{\"selectedWorldId\":42,\"autoLoadOnStart\":\"true\"}", "{\"future\":1234567890123456789012345678901234567890,\"nested\":[null,{\"value\":null}]}" })
            {
                var raw = "{\"mods\":[{\"id\":\"example.healthy\",\"enabled\":true}],\"worldLaunch\":" + value + "}";
                var state = ManagerStateLaunchPersistence.Parse(raw);
                state.Normalize();
                if (state.Find("example.healthy")?.Enabled != true || state.AutoLoadOnStart)
                    throw new InvalidDataException("Malformed legacy selection must not discard enabled packages or coerce autoload.");
                var expected = JsonNode.Parse("{\"worldLaunch\":" + value + "}");
                var again = ManagerStateLaunchPersistence.Parse(ManagerStateLaunchPersistence.Serialize(state));
                if (!JsonNode.DeepEquals(expected, JsonNode.Parse(again.LaunchSelection!.LegacyJson!)))
                    throw new InvalidDataException("Malformed or large-number legacy JSON must survive saving exactly as values.");
            }
            foreach (var length in new[] { 65, 96, 97 })
            {
                var id = "mode." + new string('x', length - 5);
                var json = "{\"schemaVersion\":1,\"kind\":\"target\",\"request\":{\"targetId\":\"" + id + "\"}}";
                try
                {
                    var selection = LaunchTransportJson.ReadSelection(json);
                    if (length == 97 || selection.Request!.TargetId != id) throw new InvalidOperationException("Durable declaration ID boundary mismatch.");
                }
                catch (InvalidDataException) { if (length != 97) throw; }
            }
            RejectMalformedPrimaryAndEnvelope();
            Console.WriteLine("Manager launch selection preservation: PASS");
        }
        private static void RejectMalformedPrimaryAndEnvelope()
        {
            var directory = Path.Combine(Path.GetTempPath(), "topiaforge-manager-state-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                var path = Path.Combine(directory, "state.json");
                File.WriteAllText(path, "{broken latest state");
                File.WriteAllText(path + ".bak", "{\"mods\":[{\"id\":\"old.mod\",\"enabled\":true}]}");
                var rejected = false;
                try { ManagerStateLaunchPersistence.Load(path); } catch (Exception error) when (error is InvalidDataException || error is FormatException) { rejected = true; }
                if (!rejected) throw new InvalidDataException("A malformed primary must not silently become an older writable manager state.");
                foreach (var raw in new[] { "{\"schemaVersion\":\"1\"}", "{\"schemaVersion\":null}", "{\"schemaVersion\":1.5}",
                    "{\"schemaVersion\":2}", "{\"mods\":[{}]}", "{\"mods\":[{\"id\":\"bad/identity\",\"enabled\":true}]}", "{\"mods\":null}", "{\"mods\":{}}", "{\"mods\":[null]}",
                    "{\"mods\":[{\"id\":\"example.mod\",\"enabled\":\"true\"}]}",
                    "{\"mods\":[{\"id\":\"example.mod\",\"version\":42}]}" })
                {
                    rejected = false;
                    try { ManagerStateLaunchPersistence.Parse(raw); } catch (Exception error) when (error is InvalidDataException || error is FormatException || error is System.Runtime.Serialization.SerializationException) { rejected = true; }
                    if (!rejected) throw new InvalidDataException("Malformed raw manager envelope must not be coerced before recovery: " + raw);
                }
                var recovery = ManagerStateStartupStore.Open(path);
                if (recovery.CanSave || recovery.State.Mods.Count != 0 || recovery.Failure.Length == 0)
                    throw new InvalidDataException("Malformed primary permits only an empty read-only recovery state.");
                recovery.State.Mods.Add(new InstalledModState { Id = "invented.mod", Enabled = true });
                if (recovery.Save() || recovery.Save()) throw new InvalidDataException("A recovery state cannot write, including teardown retries.");
                if (!File.ReadAllText(path + ".bak").Contains("old.mod")) throw new InvalidDataException("Recovery must also preserve the original backup.");
                var safe = new ProfileLaunchConfigurationV4("safe", 0, "safe-request", "main-menu", Array.Empty<PackageIdentity>(),
                    PackageSetDigest.Of(Array.Empty<PackageIdentity>()), true, false, Array.Empty<string>(), new System.Collections.Generic.Dictionary<string, string>());
                var startup = StartupRecoveryPolicy.Prepare(safe, recovery.State, StartupRecoveryDecision.None, DateTime.UtcNow);
                var effective = startup.CreateEffectiveState(recovery.State);
                if (!startup.SafeMode || effective.Mods.Exists(mod => mod.Enabled) || startup.Requested != safe)
                    throw new InvalidDataException("Explicit safe menu retains command correlation and never enables recovered packages.");
                var directoryState = Path.Combine(directory, "not-a-file"); Directory.CreateDirectory(directoryState);
                rejected = false;
                try { ManagerStateStartupStore.Open(directoryState); } catch (IOException) { rejected = true; }
                if (!rejected) throw new InvalidDataException("An invalid state path cannot bypass storage protection through recovery.");
                if (File.ReadAllText(path) != "{broken latest state") throw new InvalidDataException("State validation must not write recovery data.");
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
