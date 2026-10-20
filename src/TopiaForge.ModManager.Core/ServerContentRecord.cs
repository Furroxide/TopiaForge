using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    /// <summary>How a record of server content read.</summary>
    internal enum ServerContentStatus
    {
        /// <summary>There is no record: nothing was installed for servers.</summary>
        Missing,

        /// <summary>A version 1 record, every entry as the contract says.</summary>
        Valid,

        /// <summary>A newer launcher wrote it; this reader must not guess at it.</summary>
        Newer,

        /// <summary>Anything else that is there: bytes that are not the contract, a link or a folder.</summary>
        Damaged,

        /// <summary>It is there but could not be read just now.</summary>
        Unreadable,
    }

    /// <summary>One archive a join installed.</summary>
    internal sealed class ServerContentVersion
    {
        internal ServerContentVersion(string id, string version, string sha256, string receipt, bool hidden, long installedAt)
        {
            Id = id;
            Version = version;
            Sha256 = sha256;
            Receipt = receipt;
            Hidden = hidden;
            InstalledAt = installedAt;
        }

        public string Id { get; }
        public string Version { get; }
        public string Sha256 { get; }

        /// <summary>The SHA-256 of the install receipt the join wrote.</summary>
        public string Receipt { get; }

        /// <summary>Whether the player's own play never sees it: the mod was already the player's.</summary>
        public bool Hidden { get; }

        public long InstalledAt { get; }
    }

    /// <summary>
    /// The record of server content, <c>BepInEx/TopiaForge/server-content.json</c>: what joins installed, which the
    /// launcher writes and the launcher and this loader read. A version it hides or withholds is for exact pins
    /// alone, which is how server profiles load, never for the player's own play.
    /// </summary>
    /// <remarks>
    /// The contract is shared with the launcher's <c>ServerContentRecord</c> and held to it by
    /// <c>tests/fixtures/multiplayer/server-content</c>: exactly the members <c>schemaVersion</c> (1),
    /// <c>versions</c>, <c>withheld</c> and <c>addedRecords</c>, at most <see cref="MaximumBytes"/> bytes of UTF-8 and
    /// <see cref="MaximumEntries"/> entries in each list, no member twice anywhere. A record that is not exactly that
    /// is <see cref="ServerContentStatus.Damaged"/>, never partly read: a reader that dropped a damaged entry would
    /// expose the version it withheld.
    /// </remarks>
    internal sealed class ServerContentRecord
    {
        public const string FileName = "server-content.json";
        public const int SchemaVersion = 1;
        public const int MaximumBytes = 1024 * 1024;
        public const int MaximumEntries = 1024;
        public const long MaximumSafeInteger = 9007199254740991L;

        private const int MaximumNesting = 128;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Regex ModId = new Regex(@"\A[A-Za-z0-9][A-Za-z0-9_.-]{1,63}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Sha256 = new Regex(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
        private static readonly Regex Integer = new Regex(@"\A-?(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant);

        // SemVer 2.0.0, written as the launcher's descriptor reader writes it so that neither backtracks.
        private static readonly Regex SemanticVersion = new Regex(
            @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)" +
            @"(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?" +
            @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
            RegexOptions.CultureInvariant);

        private static readonly string[] TopMembers = { "schemaVersion", "versions", "withheld", "addedRecords" };
        private static readonly string[] VersionMembers = { "id", "version", "sha256", "receipt", "hidden", "installedAt" };
        private static readonly string[] WithheldMembers = { "id", "version", "receipt" };

        private readonly Dictionary<string, string[]> withholding;

        private ServerContentRecord(
            ServerContentStatus status,
            string problem,
            IReadOnlyList<ServerContentVersion> versions,
            IReadOnlyList<(string Id, string Version, string Receipt)> withheld,
            IReadOnlyCollection<string> addedRecords)
        {
            Status = status;
            Problem = problem;
            Versions = versions;
            Withheld = withheld;
            AddedRecords = addedRecords;
            withholding = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var item in versions.Where(item => item.Hidden)) Withhold(item.Id, item.Version, item.Receipt);
            foreach (var item in withheld) Withhold(item.Id, item.Version, item.Receipt);
        }

        public ServerContentStatus Status { get; }

        /// <summary>Why a record is not valid, for the manager log; empty when it is.</summary>
        public string Problem { get; }

        public IReadOnlyList<ServerContentVersion> Versions { get; }

        /// <summary>Versions withheld from the player's own play without being known server content.</summary>
        public IReadOnlyList<(string Id, string Version, string Receipt)> Withheld { get; }

        /// <summary>The lowercase ids of mods whose manager record a join added, disabled.</summary>
        public IReadOnlyCollection<string> AddedRecords { get; }

        public static ServerContentRecord Missing() => Empty(ServerContentStatus.Missing, string.Empty);

        public static ServerContentRecord Unusable(ServerContentStatus status, string problem)
        {
            if (status == ServerContentStatus.Valid || status == ServerContentStatus.Missing)
                throw new ArgumentOutOfRangeException(nameof(status));
            return Empty(status, problem);
        }

        /// <summary>
        /// Whether the record keeps <paramref name="id"/> (in any case) at <paramref name="version"/> out of the
        /// player's own play while its install receipt has the SHA-256 <paramref name="receiptSha256"/>. Null for
        /// a receipt that could not be read: a version the record names is then withheld, failing closed.
        /// </summary>
        public bool Withholds(string id, string version, string? receiptSha256)
        {
            if (Status != ServerContentStatus.Valid || !withholding.TryGetValue(Key(id, version), out var receipts))
                return false;
            return receiptSha256 == null || receipts.Contains(receiptSha256, StringComparer.Ordinal);
        }

        /// <summary>Whether the record names a version of <paramref name="id"/> at <paramref name="version"/>.</summary>
        public bool Names(string id, string version) =>
            Status == ServerContentStatus.Valid && withholding.ContainsKey(Key(id, version));

        public bool AddedRecordOf(string id) =>
            Status == ServerContentStatus.Valid && AddedRecords.Contains(id.ToLowerInvariant());

        /// <summary>Reads a record's bytes as the contract says, or says why they are not one.</summary>
        public static ServerContentRecord Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length > MaximumBytes)
                return Empty(ServerContentStatus.Damaged, "The record is larger than " + MaximumBytes + " bytes.");
            string text;
            try { text = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { return Empty(ServerContentStatus.Damaged, "The record is not UTF-8."); }
            if (text.Length > 0 && text[0] == (char)0xFEFF)
                return Empty(ServerContentStatus.Damaged, "The record starts with a byte order mark.");
            try
            {
                RejectDuplicateMembers(text);
                StrictJsonValueValidator.Validate(text);
                var top = Members(text, null);
                if (!top.TryGetValue("schemaVersion", out var rawVersion) || !TryInteger(rawVersion, out var schema))
                    throw Invalid("The record has no integer schemaVersion.");
                if (schema > SchemaVersion)
                    return Empty(ServerContentStatus.Newer, "A newer launcher wrote this record (schemaVersion " + schema + ").");
                if (schema != SchemaVersion) throw Invalid("The record's schemaVersion is not 1.");
                RequireExactly(top, TopMembers, "The record");
                var versions = Entries(top["versions"], "versions").Select(ReadVersion).ToList();
                var withheld = Entries(top["withheld"], "withheld").Select(ReadWithheld).ToList();
                var added = Entries(top["addedRecords"], "addedRecords").Select(ReadAddedRecord).ToList();
                RequireUnique(versions.Select(item => Key(item.Id, item.Version)), "versions");
                RequireUnique(withheld.Select(item => Key(item.Id, item.Version)), "withheld");
                RequireUnique(added, "addedRecords");
                return new ServerContentRecord(ServerContentStatus.Valid, string.Empty, versions, withheld, added);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                // Whatever the readers below refuse, the record is damaged: a scan never fails on it, and never reads
                // part of it.
                return Empty(ServerContentStatus.Damaged, error.Message);
            }
        }

        private static ServerContentRecord Empty(ServerContentStatus status, string problem) =>
            new ServerContentRecord(status, problem, Array.Empty<ServerContentVersion>(),
                Array.Empty<(string, string, string)>(), Array.Empty<string>());

        private void Withhold(string id, string version, string receipt)
        {
            var key = Key(id, version);
            withholding[key] = withholding.TryGetValue(key, out var known) ? known.Concat(new[] { receipt }).ToArray() : new[] { receipt };
        }

        private static string Key(string id, string version) => id.ToLowerInvariant() + "@" + version;

        private static ServerContentVersion ReadVersion(string raw)
        {
            var fields = Members(raw, VersionMembers);
            RequireExactly(fields, VersionMembers, "A version entry");
            if (!TryInteger(fields["installedAt"], out var installedAt) || installedAt < 0 || installedAt > MaximumSafeInteger)
                throw Invalid("A version entry's installedAt is not a whole number of seconds from 0 to 2^53 - 1.");
            return new ServerContentVersion(Id(fields["id"]), SemVer(fields["version"]), Digest(fields["sha256"]),
                Digest(fields["receipt"]), Flag(fields["hidden"]), installedAt);
        }

        private static (string Id, string Version, string Receipt) ReadWithheld(string raw)
        {
            var fields = Members(raw, WithheldMembers);
            RequireExactly(fields, WithheldMembers, "A withheld entry");
            return (Id(fields["id"]), SemVer(fields["version"]), Digest(fields["receipt"]));
        }

        private static string ReadAddedRecord(string raw)
        {
            var id = Id(raw);
            if (!string.Equals(id, id.ToLowerInvariant(), StringComparison.Ordinal))
                throw Invalid("An added record is not a lowercase mod id.");
            return id;
        }

        private static Dictionary<string, string> Members(string json, string[]? expected)
        {
            var members = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in JsonObjectMerge.ReadProperties(json))
            {
                if (expected != null && !expected.Contains(property.Name, StringComparer.Ordinal))
                    throw Invalid("An entry has a member the contract does not.");
                members.Add(property.Name, property.RawValue.Trim());
            }

            return members;
        }

        private static void RequireExactly(Dictionary<string, string> members, string[] expected, string what)
        {
            if (members.Count != expected.Length || expected.Any(name => !members.ContainsKey(name)))
                throw Invalid(what + " does not have exactly the members the contract names.");
        }

        private static IReadOnlyList<string> Entries(string raw, string name)
        {
            if (raw.Length == 0 || raw[0] != '[') throw Invalid("The record's " + name + " is not a list.");
            var items = JsonObjectMerge.ReadArrayValues(raw);
            if (items.Count > MaximumEntries)
                throw Invalid("The record's " + name + " has more than " + MaximumEntries + " entries.");
            return items;
        }

        private static void RequireUnique(IEnumerable<string> keys, string name)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (keys.Any(key => !seen.Add(key))) throw Invalid("The record's " + name + " names one version twice.");
        }

        private static string Id(string raw)
        {
            var value = Text(raw);
            if (!ModId.IsMatch(value)) throw Invalid("An entry's id is not a mod id.");
            return value;
        }

        private static string SemVer(string raw)
        {
            var value = Text(raw);
            if (value.Length > 64 || !SemanticVersion.IsMatch(value)) throw Invalid("An entry's version is not a semantic version.");
            return value;
        }

        private static string Digest(string raw)
        {
            var value = Text(raw);
            if (!Sha256.IsMatch(value)) throw Invalid("An entry's digest is not 64 lowercase hexadecimal digits.");
            return value;
        }

        private static bool Flag(string raw) =>
            raw == "true" ? true : raw == "false" ? false : throw Invalid("An entry's hidden is not a Boolean.");

        private static string Text(string raw)
        {
            if (raw.Length == 0 || raw[0] != '"') throw Invalid("An entry's value is not a string.");
            return JsonUtil.Deserialize<string>(raw);
        }

        // An integer to both JSON readers: no fraction or exponent, and within 64 bits, as the launcher's decoder
        // reads one.
        private static bool TryInteger(string raw, out long value)
        {
            value = 0;
            return Integer.IsMatch(raw) && long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// One pass over the text, as the launcher's reader makes it, refusing a member named twice in any object
        /// (two spellings of one name included) and nesting past <see cref="MaximumNesting"/> levels.
        /// </summary>
        private static void RejectDuplicateMembers(string text)
        {
            var scopes = new List<HashSet<string>?>();
            for (var offset = 0; offset < text.Length; offset++)
            {
                var code = text[offset];
                if (code == '{' || code == '[')
                {
                    if (scopes.Count >= MaximumNesting) throw Invalid("The record nests past " + MaximumNesting + " levels.");
                    scopes.Add(code == '{' ? new HashSet<string>(StringComparer.Ordinal) : null);
                    continue;
                }

                if (code == '}' || code == ']')
                {
                    if (scopes.Count > 0) scopes.RemoveAt(scopes.Count - 1);
                    continue;
                }

                if (code != '"') continue;
                var start = offset;
                var escaped = false;
                var closed = false;
                while (++offset < text.Length)
                {
                    var next = text[offset];
                    if (escaped) { escaped = false; continue; }
                    if (next == '\\') { escaped = true; continue; }
                    if (next == '"') { closed = true; break; }
                }

                if (!closed) throw Invalid("The record has an unterminated string.");
                var after = offset + 1;
                while (after < text.Length && (text[after] == ' ' || text[after] == '\t' || text[after] == '\n' || text[after] == '\r'))
                    after++;
                if (after >= text.Length || text[after] != ':') continue;
                var name = JsonUtil.Deserialize<string>(text.Substring(start, offset - start + 1));
                var scope = scopes.Count == 0 ? null : scopes[scopes.Count - 1];
                if (scope != null && !scope.Add(name)) throw Invalid("The record names a member twice.");
            }
        }

        private static InvalidDataException Invalid(string message) => new InvalidDataException(message);
    }
}
