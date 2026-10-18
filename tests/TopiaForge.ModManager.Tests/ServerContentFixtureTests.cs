using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Replays tests/fixtures/multiplayer/server-content/cases.json, which the launcher's Dart tests replay too, so the
    /// game's loader and the launcher read the record of server content alike: the same documents are records, the same
    /// versions are withheld from the player's own play, and every other document fails closed.
    /// </summary>
    internal static class ServerContentFixtureTests
    {
        internal static void Run(string repositoryRoot)
        {
            var path = Path.Combine(repositoryRoot, "tests", "fixtures", "multiplayer", "server-content", "cases.json");
            using var fixture = JsonDocument.Parse(File.ReadAllText(path));
            var root = fixture.RootElement;
            var failures = new List<Exception>();
            var runs = 0;
            void Check(string name, Action test)
            {
                runs++;
                try { test(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(name + ": " + error.Message, error)); }
            }

            Check("limits", () => Limits(root.GetProperty("limits")));
            foreach (var item in root.GetProperty("valid").EnumerateArray())
            {
                var id = "valid " + item.GetProperty("id").GetString();
                Check(id + " reads", () => Valid(item, item.GetProperty("bytes").GetString()!));
                Check(id + " reads in any spelling", () => Valid(item, item.GetProperty("reordered").GetString()!));
            }

            foreach (var item in root.GetProperty("newer").EnumerateArray())
            {
                Check("newer " + item.GetProperty("id").GetString(), () =>
                    Unusable(ServerContentRecord.Read(Encoding.UTF8.GetBytes(item.GetProperty("text").GetString()!)),
                        ServerContentStatus.Newer, item));
            }

            foreach (var item in root.GetProperty("invalid").EnumerateArray())
            {
                Check("invalid " + item.GetProperty("id").GetString(), () =>
                    Unusable(ServerContentRecord.Read(Bytes(item)), ServerContentStatus.Damaged, item));
            }

            foreach (var item in root.GetProperty("generated").EnumerateArray())
            {
                Check("generated " + item.GetProperty("id").GetString(), () => Generated(item));
            }

            if (failures.Count > 0)
            {
                throw new AggregateException(
                    "Server content fixtures: " + failures.Count + " of " + runs + " checks failed.", failures);
            }

            Console.WriteLine("Server content fixtures: " + runs + " checks passed.");
        }

        private static void Limits(JsonElement limits)
        {
            Assert(limits.GetProperty("schemaVersion").GetInt32() == ServerContentRecord.SchemaVersion
                && limits.GetProperty("maximumBytes").GetInt32() == ServerContentRecord.MaximumBytes
                && limits.GetProperty("maximumEntries").GetInt32() == ServerContentRecord.MaximumEntries
                && limits.GetProperty("maximumSafeInteger").GetInt64() == ServerContentRecord.MaximumSafeInteger,
                "The reader's limits are the fixture's.");
        }

        private static void Valid(JsonElement item, string text)
        {
            var record = ServerContentRecord.Read(Encoding.UTF8.GetBytes(text));
            Assert(record.Status == ServerContentStatus.Valid && record.Problem.Length == 0,
                "A valid record reads as valid, but read as " + record.Status + ": " + record.Problem);
            var selection = ServerContentSelection.For(record);
            Assert(!selection.Frozen, "A valid record never freezes selection.");
            foreach (var probe in item.GetProperty("hides").EnumerateArray())
            {
                var id = probe.GetProperty("id").GetString()!;
                var version = probe.GetProperty("version").GetString()!;
                var hidden = probe.GetProperty("hidden").GetBoolean();
                Assert(record.Withholds(id, version, probe.GetProperty("receipt").GetString()) == hidden,
                    id + "@" + version + " with its receipt must " + (hidden ? string.Empty : "not ") + "be withheld.");
            }

            var expected = item.GetProperty("addedRecords").EnumerateArray().Select(value => value.GetString()!).ToList();
            Assert(record.AddedRecords.OrderBy(id => id, StringComparer.Ordinal).SequenceEqual(expected),
                "The added records are the fixture's.");
            foreach (var id in expected)
            {
                Assert(selection.StartsDisabled(id) && selection.StartsDisabled(id.ToUpperInvariant()),
                    id + " was added by a join, so it starts disabled in any case.");
            }
        }

        private static void Unusable(ServerContentRecord record, ServerContentStatus expected, JsonElement item)
        {
            Assert(record.Status == expected,
                "Read as " + record.Status + " instead of " + expected + ": " + item.GetProperty("about").GetString());
            Assert(record.Problem.Length > 0, "An unusable record says why.");
            Assert(record.Versions.Count == 0 && record.Withheld.Count == 0 && record.AddedRecords.Count == 0,
                "Nothing of an unusable record is read.");
            Assert(!record.Withholds("io.example.lights", "2.0.0", null) && !record.AddedRecordOf("io.example.arena"),
                "An unusable record answers nothing on its own.");
            var selection = ServerContentSelection.For(record);
            Assert(selection.Frozen && selection.Status == expected,
                "An unusable record freezes unpinned selection instead of exposing what it may withhold.");
        }

        private static void Generated(JsonElement item)
        {
            var data = Build(item);
            Assert(data.Length == item.GetProperty("length").GetInt32(), "The recipe builds " + data.Length + " bytes.");
            Assert(Sha256(data) == item.GetProperty("sha256").GetString(), "The recipe builds the fixture's bytes.");
            var record = ServerContentRecord.Read(data);
            if (item.GetProperty("valid").GetBoolean())
            {
                Assert(record.Status == ServerContentStatus.Valid, "Read as " + record.Status + ": " + record.Problem);
                if (item.TryGetProperty("entries", out var entries) && item.GetProperty("recipe").GetString() == "maximal")
                {
                    var count = entries.GetInt32();
                    Assert(record.Versions.Count == count && record.Withheld.Count == count && record.AddedRecords.Count == count,
                        "Every entry of the largest record is read.");
                }
            }
            else
            {
                Assert(record.Status == ServerContentStatus.Damaged, "Read as " + record.Status + ".");
            }
        }

        private static byte[] Build(JsonElement item)
        {
            var recipe = item.GetProperty("recipe").GetString();
            if (recipe == "maximal") return Maximal(item.GetProperty("entries").GetInt32());
            if (recipe == "too-many")
                return TooMany(item.GetProperty("entries").GetInt32(), item.GetProperty("list").GetString()!);
            if (recipe == "padded") return Padded(item.GetProperty("length").GetInt32());
            throw new InvalidOperationException("Unknown recipe.");
        }

        private static byte[] Maximal(int count)
        {
            string Id(int index) => "m" + index.ToString("D4") + new string('x', 59);
            return Encoding.UTF8.GetBytes(Canonical(
                Enumerable.Range(0, count).Select(index => VersionEntry(Id(index), "1.0.0-" + new string('a', 58),
                    Hex64(index), Hex64(index + count))),
                Enumerable.Range(0, count).Select(index => WithheldEntry(Id(index), "2.0.0-" + new string('b', 58),
                    Hex64(index + 2 * count))),
                Enumerable.Range(0, count).Select(Id)));
        }

        private static byte[] TooMany(int count, string list)
        {
            string Id(int index) => "m" + index.ToString("D5");
            var none = Enumerable.Empty<string>();
            return Encoding.UTF8.GetBytes(Canonical(
                list == "versions"
                    ? Enumerable.Range(0, count).Select(index => VersionEntry(Id(index), "1.0.0", Hex64(index), Hex64(index + count)))
                    : none,
                list == "withheld"
                    ? Enumerable.Range(0, count).Select(index => WithheldEntry(Id(index), "2.0.0", Hex64(index + 2 * count)))
                    : none,
                list == "addedRecords" ? Enumerable.Range(0, count).Select(Id) : none));
        }

        private static byte[] Padded(int length)
        {
            var empty = Canonical(Enumerable.Empty<string>(), Enumerable.Empty<string>(), Enumerable.Empty<string>());
            return Encoding.UTF8.GetBytes(empty + new string(' ', length - empty.Length));
        }

        // Canonical spelling: members in ordinal order, no white space; every id and version here is ASCII and needs no
        // escape, and the recipes list entries in sorted order already.
        private static string Canonical(IEnumerable<string> versions, IEnumerable<string> withheld, IEnumerable<string> added) =>
            "{\"addedRecords\":[" + string.Join(",", added.Select(id => "\"" + id + "\"")) + "],\"schemaVersion\":"
            + ServerContentRecord.SchemaVersion + ",\"versions\":[" + string.Join(",", versions) + "],\"withheld\":["
            + string.Join(",", withheld) + "]}";

        private static string VersionEntry(string id, string version, string sha256, string receipt) =>
            "{\"hidden\":true,\"id\":\"" + id + "\",\"installedAt\":" + ServerContentRecord.MaximumSafeInteger + ",\"receipt\":\""
            + receipt + "\",\"sha256\":\"" + sha256 + "\",\"version\":\"" + version + "\"}";

        private static string WithheldEntry(string id, string version, string receipt) =>
            "{\"id\":\"" + id + "\",\"receipt\":\"" + receipt + "\",\"version\":\"" + version + "\"}";

        private static string Hex64(int value) => value.ToString("x").PadLeft(64, '0');

        private static byte[] Bytes(JsonElement item) =>
            item.TryGetProperty("hex", out var hex)
                ? Enumerable.Range(0, hex.GetString()!.Length / 2)
                    .Select(index => Convert.ToByte(hex.GetString()!.Substring(index * 2, 2), 16)).ToArray()
                : Encoding.UTF8.GetBytes(item.GetProperty("text").GetString()!);

        private static string Sha256(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(data).Select(value => value.ToString("x2")));
            }
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
