using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Executes every indexed C# contract operation and rejects unindexed or misplaced files.
    /// The Dart runner independently validates each manifest payload against the V6 JSON Schema;
    /// both language operations share exact verdicts, codes, and structured normalized results.
    /// </summary>
    internal static class GamemodeContractConformanceTests
    {
        private const string RunnerName = "csharp";

        private static readonly HashSet<string> CaseFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "channel", "kind", "summary", "selection", "intent", "manifest", "expect",
            "divergenceReason", "schemaOutcome", "operations", "modelMutation"
        };
        private static readonly HashSet<string> OutcomeFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "outcome", "errorCodes", "normalized"
        };

        public static void Run()
        {
            var fixtureRoot = Path.Combine(Program.FindRepoRoot(), "tests", "fixtures", "gamemode-v6");
            using var index = LoadIndex(fixtureRoot);
            var root = index.RootElement;

            var channelRunners = ReadChannelRunners(root);
            var cases = ReadCases(root);
            AssertIndexIsClosedOverTheTree(fixtureRoot, channelRunners.Keys, cases);

            var executed = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var testCase in cases)
            {
                if (!channelRunners[testCase.Channel].Contains(RunnerName))
                {
                    continue;
                }

                Execute(fixtureRoot, testCase, channelRunners[testCase.Channel]);
                executed.TryGetValue(testCase.Channel, out var count);
                executed[testCase.Channel] = count + 1;
            }

            AssertRunnerMetItsObligations(channelRunners, cases, executed);
            Console.WriteLine(
                "All gamemode contract conformance fixtures passed (" +
                executed.Values.Sum() + " executed by " + RunnerName + ").");
        }

        private static JsonDocument LoadIndex(string fixtureRoot)
        {
            var path = Path.Combine(fixtureRoot, "index.json");
            Assert(
                File.Exists(path),
                "tests/fixtures/gamemode-v6/index.json is missing; run " +
                "'python3 .github/scripts/check_fixture_index.py --write'.");
            return JsonDocument.Parse(File.ReadAllText(path));
        }

        private static Dictionary<string, HashSet<string>> ReadChannelRunners(JsonElement root)
        {
            var runners = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var channel in root.GetProperty("channelRunners").EnumerateObject())
            {
                runners[channel.Name] = new HashSet<string>(
                    channel.Value.EnumerateArray().Select(item => item.GetString() ?? string.Empty),
                    StringComparer.Ordinal);
            }

            Assert(runners.Count > 0, "the fixture index declares no channels");
            return runners;
        }

        private static List<IndexedCase> ReadCases(JsonElement root)
        {
            var cases = root.GetProperty("cases").EnumerateArray()
                .Select(item => new IndexedCase(
                    item.GetProperty("id").GetString() ?? string.Empty,
                    item.GetProperty("channel").GetString() ?? string.Empty,
                    item.GetProperty("kind").GetString() ?? string.Empty,
                    item.GetProperty("path").GetString() ?? string.Empty))
                .ToList();
            Assert(cases.Count > 0, "the fixture index lists no cases; an empty index asserts nothing");
            return cases;
        }

        /// <summary>
        /// The check the existing corpus mechanism lacks. Without it a fixture can sit on disk
        /// forever, executed by nobody, while the suite reports success.
        /// </summary>
        private static void AssertIndexIsClosedOverTheTree(
            string fixtureRoot,
            IEnumerable<string> channels,
            IReadOnlyList<IndexedCase> cases)
        {
            var onDisk = new HashSet<string>(StringComparer.Ordinal);
            var knownChannels = new HashSet<string>(channels, StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(fixtureRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(fixtureRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                if (relative == "index.json" || relative == "fixture.schema.json") continue;
                Assert(relative.EndsWith(".json", StringComparison.Ordinal),
                    relative + " is an unexpected non-JSON fixture file.");
                Assert(relative.Contains('/') && knownChannels.Contains(relative.Split('/')[0]),
                    relative + " is outside a known channel directory.");
                onDisk.Add(relative);
            }

            var indexed = new HashSet<string>(cases.Select(item => item.Path), StringComparer.Ordinal);
            var orphaned = onDisk.Except(indexed).OrderBy(item => item, StringComparer.Ordinal).ToList();
            Assert(
                orphaned.Count == 0,
                "fixtures exist on disk that no runner executes: " + string.Join(", ", orphaned) +
                ". Run 'python3 .github/scripts/check_fixture_index.py --write'.");

            var missing = indexed.Except(onDisk).OrderBy(item => item, StringComparer.Ordinal).ToList();
            Assert(
                missing.Count == 0,
                "the fixture index lists files that are not on disk: " + string.Join(", ", missing));
        }

        private static void Execute(
            string fixtureRoot,
            IndexedCase indexed,
            IReadOnlyCollection<string> obligedRunners)
        {
            var path = Path.Combine(fixtureRoot, indexed.Path.Replace('/', Path.DirectorySeparatorChar));
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var body = document.RootElement;

            AssertClosedObject(indexed.Id, body, CaseFields);
            Assert(
                (body.GetProperty("id").GetString() ?? string.Empty) == indexed.Id,
                indexed.Path + " declares an id the index disagrees with");
            Assert(
                (body.GetProperty("kind").GetString() ?? string.Empty) == indexed.Kind,
                indexed.Path + " declares a kind the index disagrees with");

            Assert(body.GetProperty("channel").GetString() == indexed.Channel,
                indexed.Path + " declares a channel the index disagrees with.");
            var directory = indexed.Kind.StartsWith("manifest-", StringComparison.Ordinal) ? "manifest" : "launch-intent";
            Assert(indexed.Path.StartsWith(indexed.Channel + "/" + directory + "/", StringComparison.Ordinal),
                indexed.Path + " is misplaced for its kind.");
            AssertEquivalentOperations(indexed, body, obligedRunners);

            Assert(indexed.Kind == "manifest-model-rejects" || !body.TryGetProperty("modelMutation", out _),
                indexed.Path + " cannot change a model in a reader operation.");
            var expectation = ReadExpectation(indexed, body);
            switch (indexed.Kind)
            {
                case "launch-intent-round-trip":
                case "launch-intent-hostile":
                    ExecuteLaunchIntent(indexed, body, expectation);
                    break;
                case "manifest-accepts":
                case "manifest-rejects":
                case "manifest-model-rejects":
                    ExecuteManifest(indexed, body, expectation);
                    break;
                default:
                    // Deliberately fatal. A kind this runner does not know must fail the build rather
                    // than fall through as a pass, which is how a fixture becomes decorative.
                    throw new InvalidOperationException(
                        "Fixture " + indexed.Id + " has kind '" + indexed.Kind +
                        "', which the C# conformance runner does not implement.");
            }
        }

        /// <summary>Manifest operations share complete expectations; wire operations name producer and consumer.</summary>
        private static void AssertEquivalentOperations(
            IndexedCase indexed,
            JsonElement body,
            IReadOnlyCollection<string> obligedRunners)
        {
            var expect = body.GetProperty("expect");
            foreach (var runner in obligedRunners)
            {
                Assert(
                    expect.TryGetProperty(runner, out _),
                    indexed.Path + " is missing the expectation for obliged runner '" + runner + "'.");
            }

            if (indexed.Kind.StartsWith("manifest-", StringComparison.Ordinal))
            {
                Assert(body.TryGetProperty("schemaOutcome", out var schemaOutcome)
                    && (schemaOutcome.GetString() == "accept" || schemaOutcome.GetString() == "reject"),
                    indexed.Path + " requires schemaOutcome for its manifest payload.");
                Assert(DeclarationDigest.Equal(expect.GetProperty("csharp"), expect.GetProperty("dart")),
                    indexed.Path + " has divergent same-operation expectations.");
                Assert(!body.TryGetProperty("divergenceReason", out _),
                    indexed.Path + " cannot exempt manifest-reader parity.");
            }
            else
            {
                var operations = body.GetProperty("operations");
                Assert(operations.GetProperty("csharp").GetString() == "read-intent"
                    && operations.GetProperty("dart").GetString() == "write-intent",
                    indexed.Path + " requires explicit wire operations.");
            }
        }

        private static Expectation ReadExpectation(IndexedCase indexed, JsonElement body)
        {
            var mine = body.GetProperty("expect").GetProperty(RunnerName);
            AssertClosedObject(indexed.Id, mine, OutcomeFields);
            var outcome = mine.GetProperty("outcome").GetString() ?? string.Empty;
            Assert(
                outcome == "accept" || outcome == "reject",
                indexed.Path + " declares an unknown outcome '" + outcome + "'.");

            var codes = new SortedSet<string>(StringComparer.Ordinal);
            if (mine.TryGetProperty("errorCodes", out var declared))
            {
                foreach (var code in declared.EnumerateArray())
                {
                    codes.Add(code.GetString() ?? string.Empty);
                }
            }

            return new Expectation(outcome == "accept", codes);
        }

        /// <summary>
        /// Reads the one-shot worldLaunch intent exactly as the manager does and compares both the
        /// verdict and which field each complaint names.
        /// </summary>
        private static void ExecuteLaunchIntent(
            IndexedCase indexed,
            JsonElement body,
            Expectation expected)
        {
            var intent = body.GetProperty("intent").GetRawText();
            var accepted = false;
            var codes = new SortedSet<string>(StringComparer.Ordinal);
            try
            {
                var errors = JsonUtil.Deserialize<WorldLaunchIntent>(intent).Validate();
                accepted = errors.Count == 0;
                foreach (var error in errors)
                {
                    codes.Add(FieldNamedBy(error));
                }
            }
            catch (InvalidDataException)
            {
                codes.Add("unreadable");
            }
            catch (FormatException)
            {
                codes.Add("unreadable");
            }

            Assert(
                accepted == expected.Accepted,
                indexed.Path + ": the launch intent reader " + (accepted ? "accepted" : "rejected") +
                " a fixture expecting " + (expected.Accepted ? "accept" : "reject") + ".");
            Assert(
                codes.SetEquals(expected.ErrorCodes),
                indexed.Path + ": expected error codes [" + string.Join(", ", expected.ErrorCodes) +
                "] but the reader named [" + string.Join(", ", codes) + "].");
        }

        /// <summary>
        /// Reads a whole manifest exactly as the installer does, and compares both the verdict and a
        /// digest of what was parsed. The digest is the point: the older corpus mechanism compares only
        /// accept/reject, so both readers can agree a manifest is fine while disagreeing about what it
        /// said -- which for an absent flag against an explicit false is a silent behaviour change.
        /// </summary>
        private static void ExecuteManifest(
            IndexedCase indexed,
            JsonElement body,
            Expectation expected)
        {
            var accepted = false;
            var codes = new SortedSet<string>(StringComparer.Ordinal);
            ModManifest? manifest = null;
            try
            {
                manifest = ModManifestJson.Deserialize(body.GetProperty("manifest").GetRawText());
                if (body.TryGetProperty("modelMutation", out var mutation))
                    MutateModel(manifest, mutation.GetString()!);
                var errors = ManifestValidator.Validate(manifest);
                accepted = errors.Count == 0;
                foreach (var error in errors)
                {
                    codes.Add(FieldNamedBy(error));
                }
            }
            catch (InvalidDataException exception)
            {
                codes.Add(FieldNamedBy(exception.Message));
            }
            catch (FormatException exception)
            {
                codes.Add(FieldNamedBy(exception.Message));
            }

            Assert(
                accepted == expected.Accepted,
                indexed.Path + ": the manifest reader " + (accepted ? "accepted" : "rejected") +
                " a fixture expecting " + (expected.Accepted ? "accept" : "reject") +
                (codes.Count == 0 ? "." : "; it named [" + string.Join(", ", codes) + "]."));
            Assert(
                codes.SetEquals(expected.ErrorCodes),
                indexed.Path + ": expected error codes [" + string.Join(", ", expected.ErrorCodes) +
                "] but the reader named [" + string.Join(", ", codes) + "].");

            if (!accepted)
            {
                return;
            }

            var actual = DeclarationDigest.Of(manifest!);
            var declared = body.GetProperty("expect").GetProperty(RunnerName).GetProperty("normalized");
            Assert(DeclarationDigest.Equal(actual, declared),
                indexed.Path + ": parsed contribution fields " + actual.GetRawText()
                + " differ from expected " + declared.GetRawText());

            // Round-trip the contribution DTOs through the production reader without unrelated
            // historical common-manifest serialization defaults obscuring this contract.
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                body.GetProperty("manifest").GetRawText())!;
            if (manifest!.Contributions != null)
            {
                using var serialized = JsonDocument.Parse(JsonUtil.Serialize(manifest.Contributions));
                fields["contributions"] = serialized.RootElement.Clone();
            }
            var restored = ModManifestJson.Deserialize(JsonSerializer.Serialize(fields));
            Assert(ManifestValidator.Validate(restored).Count == 0,
                indexed.Path + ": serialized contribution DTOs no longer validate.");
            Assert(DeclarationDigest.Equal(DeclarationDigest.Of(restored), declared),
                indexed.Path + ": contribution serialization loses fields or presence.");
        }

        private static void MutateModel(ModManifest manifest, string mutation)
        {
            var contributions = manifest.Contributions!;
            switch (mutation)
            {
                case "empty-contributions": manifest.Contributions = new ModContributions(); break;
                case "missing-content": contributions.Worlds[0].Content = null; break;
                case "missing-spawn": contributions.Worlds[0].Spawn = null; break;
                case "missing-implementation": contributions.Gamemodes[0].Implementation = null; break;
                case "missing-world": contributions.LaunchTargets[0].World = null; break;
                case "empty-requirements": contributions.Gamemodes[0].WorldRequirements = new ModWorldRequirements(); break;
                default: throw new InvalidOperationException("Unknown fixture model mutation " + mutation);
            }
        }

        /// <summary>
        /// Every launch-intent complaint opens with the field it is about, so the leading token is a
        /// stable code without inventing a second vocabulary the messages could drift from.
        /// </summary>
        private static string FieldNamedBy(string error)
        {
            // Reader messages name the field in quotes ("Manifest field 'x' ..."); validator messages
            // open with it ("schemaVersion must be ..."). Both give the same answer -- the field the
            // complaint is about -- without a second vocabulary to keep in step with the prose.
            var open = error.StartsWith("Manifest field ", StringComparison.Ordinal)
                ? error.IndexOf('\'')
                : -1;
            if (open >= 0)
            {
                var close = error.IndexOf('\'', open + 1);
                if (close > open + 1)
                {
                    return error.Substring(open + 1, close - open - 1);
                }
            }

            var space = error.IndexOf(' ');
            return space > 0 ? error.Substring(0, space) : error;
        }

        /// <summary>
        /// The Dart runner validates each case against fixture.schema.json. This side has no JSON
        /// Schema validator, so the closed key set is checked by hand rather than trusted.
        /// </summary>
        private static void AssertClosedObject(string id, JsonElement element, ISet<string> allowed)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert(
                    allowed.Contains(property.Name),
                    "fixture " + id + " contains unknown field '" + property.Name + "'.");
            }
        }

        private static void AssertRunnerMetItsObligations(
            IReadOnlyDictionary<string, HashSet<string>> channelRunners,
            IReadOnlyList<IndexedCase> cases,
            IReadOnlyDictionary<string, int> executed)
        {
            var obliged = channelRunners
                .Where(channel => channel.Value.Contains(RunnerName))
                .Select(channel => channel.Key)
                .ToList();
            Assert(
                obliged.Count > 0,
                "no channel obliges the " + RunnerName + " runner, so this harness asserts nothing.");

            foreach (var channel in channelRunners.Keys)
            {
                var available = cases.Count(item => item.Channel == channel);
                executed.TryGetValue(channel, out var ran);
                var expected = obliged.Contains(channel) ? available : 0;
                Assert(
                    ran == expected,
                    "channel '" + channel + "' expected " + RunnerName + " to execute " + expected +
                    " cases but it executed " + ran + ".");
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Gamemode contract conformance: " + message);
            }
        }

        private sealed class IndexedCase
        {
            public IndexedCase(string id, string channel, string kind, string path)
            {
                Id = id;
                Channel = channel;
                Kind = kind;
                Path = path;
            }

            public string Id { get; }

            public string Channel { get; }

            public string Kind { get; }

            public string Path { get; }
        }

        private sealed class Expectation
        {
            public Expectation(bool accepted, SortedSet<string> errorCodes)
            {
                Accepted = accepted;
                ErrorCodes = errorCodes;
            }

            public bool Accepted { get; }

            public SortedSet<string> ErrorCodes { get; }
        }
    }
}
