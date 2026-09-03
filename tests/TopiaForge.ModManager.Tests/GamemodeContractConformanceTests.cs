using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Executes the cross-language gamemode-contract fixtures from the C# side.
    /// <para>
    /// The manifest contract is read by two hand-written readers, and this one never sees a JSON
    /// Schema: nothing under src/ or tests/ mentions topiaforge.mod.schema.json. The schema constrains
    /// Dart alone. So the fixtures are the only artefact that holds both readers to one contract, and
    /// a fixture that no runner executes is worse than no fixture -- it reads as coverage and asserts
    /// nothing.
    /// </para>
    /// <para>
    /// The older tests/fixtures/manifests corpus shows both failure modes this harness exists to close.
    /// It compares only the accept/reject verdict, so two readers can disagree about <em>why</em> and
    /// still both pass; and neither runner enumerates the directory, so a fixture added without a
    /// corpus.txt line is silently dead. Here the index is generated and checked for closure over the
    /// tree, error codes are compared as a set, and every runner obliged by a channel must execute
    /// every case on it.
    /// </para>
    /// </summary>
    internal static class GamemodeContractConformanceTests
    {
        private const string RunnerName = "csharp";

        private static readonly HashSet<string> CaseFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "id", "channel", "kind", "summary", "selection", "intent", "expect", "divergenceReason"
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
            foreach (var channel in channels)
            {
                var channelRoot = Path.Combine(fixtureRoot, channel);
                if (!Directory.Exists(channelRoot))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(channelRoot, "*.json", SearchOption.AllDirectories))
                {
                    onDisk.Add(
                        Path.GetRelativePath(fixtureRoot, file).Replace(Path.DirectorySeparatorChar, '/'));
                }
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

            AssertDivergenceIsExplained(indexed, body, obligedRunners);

            var expectation = ReadExpectation(indexed, body);
            switch (indexed.Kind)
            {
                case "launch-intent-round-trip":
                case "launch-intent-hostile":
                    ExecuteLaunchIntent(indexed, body, expectation);
                    break;
                default:
                    // Deliberately fatal. A kind this runner does not know must fail the build rather
                    // than fall through as a pass, which is how a fixture becomes decorative.
                    throw new InvalidOperationException(
                        "Fixture " + indexed.Id + " has kind '" + indexed.Kind +
                        "', which the C# conformance runner does not implement.");
            }
        }

        /// <summary>
        /// Two runners are allowed to disagree, but never quietly. A divergence is one side accepting
        /// what the other rejects; differing error codes for the same verdict are ordinary, because a
        /// kind may give each runner a different operation to perform. Both runners enforce this, so an
        /// unexplained divergence cannot land from either side.
        /// </summary>
        private static void AssertDivergenceIsExplained(
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

            var rendered = obligedRunners
                .Select(runner => expect.GetProperty(runner).GetProperty("outcome").GetString())
                .Distinct(StringComparer.Ordinal)
                .Count();
            var explained = body.TryGetProperty("divergenceReason", out var reason)
                && !string.IsNullOrWhiteSpace(reason.GetString());
            Assert(
                rendered == 1 || explained,
                indexed.Path + " expects different outcomes per runner without a divergenceReason. " +
                "A divergence between the two readers is a finding, not a detail.");
            Assert(
                rendered > 1 || !explained,
                indexed.Path + " carries a divergenceReason but every runner reaches the same verdict; " +
                "delete the reason so a real divergence is visible when one appears.");
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
        /// Every launch-intent complaint opens with the field it is about, so the leading token is a
        /// stable code without inventing a second vocabulary the messages could drift from.
        /// </summary>
        private static string FieldNamedBy(string error)
        {
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
