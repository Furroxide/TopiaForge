using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>How a hosted session can be found.</summary>
    internal enum ServerVisibility
    {
        /// <summary>Listed in the Internet browser and announced on the LAN.</summary>
        Public = 0,

        /// <summary>Reachable only through an invite code or link.</summary>
        Invite = 1,

        /// <summary>Announced on the local network only.</summary>
        Lan = 2
    }

    /// <summary>What a joining player must do about one package the host runs.</summary>
    internal enum DescriptorPackageRole
    {
        /// <summary>A <c>session</c> mod with <c>required</c> presence: the exact package must be installed.</summary>
        SessionRequired = 0,

        /// <summary>A <c>session</c> mod with <c>optional</c> presence: used when installed at the exact package.</summary>
        SessionOptional = 1,

        /// <summary>A mod with no <c>multiplayer</c> block, shared implicitly: required at the exact package.</summary>
        Implicit = 2
    }

    /// <summary>One package the host runs, pinned to its exact archive.</summary>
    internal sealed class DescriptorPackage
    {
        // Every pattern ends in \z: .NET's $ also matches before a final newline, so "a.mod\n" would pass.
        private static readonly Regex ModId = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}\z", RegexOptions.CultureInvariant);

        internal DescriptorPackage(string id, string version, string sha256, DescriptorPackageRole role)
        {
            if (!IsModId(id)) throw new ArgumentException("A package id is a manifest mod id.", nameof(id));
            if (!ServerDescriptor.IsSemanticVersion(version)) throw new ArgumentException("A package version is a semantic version.", nameof(version));
            if (!WireText.IsHex(sha256, 32)) throw new ArgumentException("A package digest is 64 lowercase hexadecimal characters.", nameof(sha256));
            if (!Enum.IsDefined(typeof(DescriptorPackageRole), role)) throw new ArgumentOutOfRangeException(nameof(role));
            Id = id;
            Version = version;
            Sha256 = sha256;
            Role = role;
        }

        internal string Id { get; }
        internal string Version { get; }

        /// <summary>Gets the lowercase hexadecimal SHA-256 of the package archive.</summary>
        internal string Sha256 { get; }

        internal DescriptorPackageRole Role { get; }

        internal static bool IsModId(string? id) => id != null && ModId.IsMatch(id);
    }

    /// <summary>
    /// The public description of a hosted session that a joining player checks before connecting: who the host is,
    /// the transport key the handshake must present, and the exact packages to install. The host signs it with its
    /// Ed25519 key (see <see cref="SignedServerDescriptor"/>), and its server id is the SHA-256 of that key, so a
    /// link, listing or LAN beacon that names a server id pins everything here. It never holds a network address.
    /// </summary>
    internal sealed class ServerDescriptor
    {
        internal const string Schema = "topiaforge.server-descriptor/1";
        internal const int MaximumNameLength = 48;
        internal const int MaximumTargetNameLength = 48;
        internal const int MaximumGameBuildLength = 16;
        internal const int MaximumVersionLength = 64;
        internal const int MaximumPackages = 512;
        internal const int MinimumPlayers = 2;
        internal const int MaximumPlayers = 16;

        // A pre-release identifier is digits, then a letter or hyphen, then anything: one way to match each string, so the
        // pattern runs in linear time. An identifier pattern that let any part take letters backtracked exponentially.
        private static readonly Regex SemanticVersion = new Regex(
            @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)" +
            @"(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?" +
            @"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
            RegexOptions.CultureInvariant);

        private static readonly Regex GameBuildText = new Regex(@"^[0-9A-Za-z._-]{1,16}\z", RegexOptions.CultureInvariant);

        private readonly byte[] signingKey;
        private readonly byte[] transportKey;

        internal ServerDescriptor(
            byte[] signingKey,
            byte[] transportKey,
            long issuedAt,
            string name,
            string gameBuild,
            string runtimeVersion,
            string protocolVersion,
            int maxPlayers,
            bool passwordRequired,
            bool voice,
            ServerVisibility visibility,
            bool hostServesPackages,
            string? gamemode,
            string? world,
            IReadOnlyList<DescriptorPackage> packages)
        {
            RequireKey(signingKey, nameof(signingKey));
            RequireKey(transportKey, nameof(transportKey));
            if (issuedAt < 0 || issuedAt > CanonicalJson.MaximumSafeInteger) throw new ArgumentOutOfRangeException(nameof(issuedAt));
            if (!WireText.IsDisplayText(name, MaximumNameLength)) throw new ArgumentException("A server name is 1 to 48 printable characters.", nameof(name));
            if (!IsGameBuild(gameBuild)) throw new ArgumentException("A game build is 1 to 16 letters, digits, dots, dashes or underscores.", nameof(gameBuild));
            if (!IsSemanticVersion(runtimeVersion)) throw new ArgumentException("The runtime version is a semantic version.", nameof(runtimeVersion));
            if (!IsSemanticVersion(protocolVersion)) throw new ArgumentException("The protocol version is a semantic version.", nameof(protocolVersion));
            if (maxPlayers < MinimumPlayers || maxPlayers > MaximumPlayers) throw new ArgumentOutOfRangeException(nameof(maxPlayers));
            if (!Enum.IsDefined(typeof(ServerVisibility), visibility)) throw new ArgumentOutOfRangeException(nameof(visibility));
            if (gamemode != null && !WireText.IsDisplayText(gamemode, MaximumTargetNameLength)) throw new ArgumentException("A gamemode name is 1 to 48 printable characters.", nameof(gamemode));
            if (world != null && !WireText.IsDisplayText(world, MaximumTargetNameLength)) throw new ArgumentException("A world name is 1 to 48 printable characters.", nameof(world));
            if (packages == null) throw new ArgumentNullException(nameof(packages));
            if (packages.Count > MaximumPackages) throw new ArgumentException("A descriptor lists at most 512 packages.", nameof(packages));

            // The list is copied before it is checked, so the caller cannot change it after the digest is taken.
            var copy = new DescriptorPackage[packages.Count];
            for (var index = 0; index < copy.Length; index++) copy[index] = packages[index];
            for (var index = 0; index < copy.Length; index++)
            {
                if (copy[index] == null) throw new ArgumentException("A package entry is missing.", nameof(packages));
                if (index > 0 && string.CompareOrdinal(copy[index - 1].Id, copy[index].Id) >= 0)
                {
                    throw new ArgumentException("Packages are listed once each, in ordinal order of their ids.", nameof(packages));
                }
            }

            var snapshot = Array.AsReadOnly(copy);

            this.signingKey = (byte[])signingKey.Clone();
            this.transportKey = (byte[])transportKey.Clone();
            ServerId = ServerIdOf(this.signingKey);
            IssuedAt = issuedAt;
            Name = name;
            GameBuild = gameBuild;
            RuntimeVersion = runtimeVersion;
            ProtocolVersion = protocolVersion;
            MaxPlayers = maxPlayers;
            PasswordRequired = passwordRequired;
            Voice = voice;
            Visibility = visibility;
            HostServesPackages = hostServesPackages;
            Gamemode = gamemode;
            World = world;
            Packages = snapshot;
            PackageDigest = WireText.ToHex(WireText.Sha256(CanonicalJson.Serialize(PackagesNode(snapshot))));
        }

        /// <summary>Gets a copy of the host's Ed25519 public key.</summary>
        internal byte[] SigningKey => (byte[])signingKey.Clone();

        /// <summary>Gets a copy of the host's X25519 static key, which the transport handshake must present.</summary>
        internal byte[] TransportKey => (byte[])transportKey.Clone();

        /// <summary>Gets the lowercase hexadecimal SHA-256 of <see cref="SigningKey"/>.</summary>
        internal string ServerId { get; }

        /// <summary>Gets the signing time in Unix seconds.</summary>
        internal long IssuedAt { get; }

        internal string Name { get; }
        internal string GameBuild { get; }
        internal string RuntimeVersion { get; }
        internal string ProtocolVersion { get; }
        internal int MaxPlayers { get; }
        internal bool PasswordRequired { get; }
        internal bool Voice { get; }
        internal ServerVisibility Visibility { get; }

        /// <summary>Gets whether the host, in developer mode, serves its own package archives to joining players.</summary>
        internal bool HostServesPackages { get; }

        internal string? Gamemode { get; }
        internal string? World { get; }
        internal IReadOnlyList<DescriptorPackage> Packages { get; }

        /// <summary>Gets the lowercase hexadecimal SHA-256 of the canonical package list, which a join plan pins.</summary>
        internal string PackageDigest { get; }

        internal static string ServerIdOf(byte[] signingKey)
        {
            RequireKey(signingKey, nameof(signingKey));
            return WireText.ToHex(WireText.Sha256(signingKey));
        }

        internal static bool IsGameBuild(string? value) => value != null && GameBuildText.IsMatch(value);

        internal static bool IsSemanticVersion(string? value) =>
            value != null && value.Length <= MaximumVersionLength && SemanticVersion.IsMatch(value);

        internal JsonObjectNode ToJson() =>
            new JsonObjectNode()
                .Add("schema", Schema)
                .Add("serverId", ServerId)
                .Add("signingKey", WireText.ToBase64Url(SigningKey))
                .Add("transportKey", WireText.ToBase64Url(TransportKey))
                .Add("issuedAt", IssuedAt)
                .Add("name", Name)
                .Add("gameBuild", GameBuild)
                .Add("runtimeVersion", RuntimeVersion)
                .Add("protocolVersion", ProtocolVersion)
                .Add("maxPlayers", MaxPlayers)
                .Add("passwordRequired", PasswordRequired)
                .Add("voice", Voice)
                .Add("visibility", VisibilityText(Visibility))
                .Add("hostServesPackages", HostServesPackages)
                .Add("gamemode", Gamemode)
                .Add("world", World)
                .Add("packages", PackagesNode(Packages))
                .Add("packageDigest", PackageDigest);

        /// <summary>Reads a descriptor object, refusing any unknown, missing or inconsistent member.</summary>
        internal static ServerDescriptor FromJson(JsonNode node)
        {
            if (!(node is JsonObjectNode value) || !value.HasExactly(
                    "schema", "serverId", "signingKey", "transportKey", "issuedAt", "name", "gameBuild", "runtimeVersion",
                    "protocolVersion", "maxPlayers", "passwordRequired", "voice", "visibility", "hostServesPackages",
                    "gamemode", "world", "packages", "packageDigest"))
            {
                throw new FormatException("A server descriptor has exactly the version 1 members.");
            }

            if (JsonRead.String(value, "schema") != Schema) throw new FormatException("The descriptor schema is not " + Schema + ".");
            if (!WireText.TryParseBase64Url(JsonRead.String(value, "signingKey"), 32, out var signingKey)) throw new FormatException("The signing key is malformed.");
            if (!WireText.TryParseBase64Url(JsonRead.String(value, "transportKey"), 32, out var transportKey)) throw new FormatException("The transport key is malformed.");
            var packages = ReadPackages(value);
            ServerDescriptor descriptor;
            try
            {
                descriptor = new ServerDescriptor(
                    signingKey,
                    transportKey,
                    JsonRead.Integer(value, "issuedAt"),
                    JsonRead.String(value, "name"),
                    JsonRead.String(value, "gameBuild"),
                    JsonRead.String(value, "runtimeVersion"),
                    JsonRead.String(value, "protocolVersion"),
                    checked((int)JsonRead.Integer(value, "maxPlayers")),
                    JsonRead.Boolean(value, "passwordRequired"),
                    JsonRead.Boolean(value, "voice"),
                    ParseVisibility(JsonRead.String(value, "visibility")),
                    JsonRead.Boolean(value, "hostServesPackages"),
                    JsonRead.OptionalString(value, "gamemode"),
                    JsonRead.OptionalString(value, "world"),
                    packages);
            }
            catch (Exception exception) when (exception is ArgumentException || exception is OverflowException)
            {
                throw new FormatException("The descriptor is invalid: " + exception.Message);
            }

            if (JsonRead.String(value, "serverId") != descriptor.ServerId) throw new FormatException("The server id does not match the signing key.");
            if (JsonRead.String(value, "packageDigest") != descriptor.PackageDigest) throw new FormatException("The package digest does not match the packages.");
            return descriptor;
        }

        internal static string VisibilityText(ServerVisibility visibility) =>
            visibility == ServerVisibility.Public ? "public" : visibility == ServerVisibility.Invite ? "invite" : "lan";

        internal static string RoleText(DescriptorPackageRole role) =>
            role == DescriptorPackageRole.SessionRequired ? "session-required"
            : role == DescriptorPackageRole.SessionOptional ? "session-optional"
            : "implicit";

        private static ServerVisibility ParseVisibility(string text)
        {
            switch (text)
            {
                case "public": return ServerVisibility.Public;
                case "invite": return ServerVisibility.Invite;
                case "lan": return ServerVisibility.Lan;
                // The host's text is never repeated back: a refusal names the rule, not what was sent.
                default: throw new FormatException("The visibility is not public, invite or lan.");
            }
        }

        private static DescriptorPackageRole ParseRole(string text)
        {
            switch (text)
            {
                case "session-required": return DescriptorPackageRole.SessionRequired;
                case "session-optional": return DescriptorPackageRole.SessionOptional;
                case "implicit": return DescriptorPackageRole.Implicit;
                default: throw new FormatException("A package role is not session-required, session-optional or implicit.");
            }
        }

        private static JsonArrayNode PackagesNode(IReadOnlyList<DescriptorPackage> packages)
        {
            var items = new List<JsonNode>(packages.Count);
            foreach (var package in packages)
            {
                items.Add(new JsonObjectNode()
                    .Add("id", package.Id)
                    .Add("version", package.Version)
                    .Add("sha256", package.Sha256)
                    .Add("role", RoleText(package.Role)));
            }

            return new JsonArrayNode(items);
        }

        private static List<DescriptorPackage> ReadPackages(JsonObjectNode value)
        {
            if (!value.TryGet("packages", out var node) || !(node is JsonArrayNode array)) throw new FormatException("The packages member is a list.");
            if (array.Items.Count > MaximumPackages) throw new FormatException("A descriptor lists at most 512 packages.");
            var packages = new List<DescriptorPackage>(array.Items.Count);
            foreach (var item in array.Items)
            {
                if (!(item is JsonObjectNode entry) || !entry.HasExactly("id", "version", "sha256", "role"))
                {
                    throw new FormatException("A package entry has exactly an id, version, sha256 and role.");
                }

                try
                {
                    packages.Add(new DescriptorPackage(
                        JsonRead.String(entry, "id"),
                        JsonRead.String(entry, "version"),
                        JsonRead.String(entry, "sha256"),
                        ParseRole(JsonRead.String(entry, "role"))));
                }
                catch (ArgumentException exception)
                {
                    throw new FormatException("A package entry is invalid: " + exception.Message);
                }
            }

            return packages;
        }

        private static void RequireKey(byte[] key, string name)
        {
            if (key == null) throw new ArgumentNullException(name);
            if (key.Length != 32) throw new ArgumentException("Keys are 32 bytes.", name);
        }
    }

    /// <summary>Typed reads of canonical JSON members that fail with a format error.</summary>
    internal static class JsonRead
    {
        internal static string String(JsonObjectNode value, string key) =>
            value.TryGet(key, out var node) && node is JsonStringNode text ? text.Value : throw new FormatException("'" + key + "' must be a string.");

        internal static string? OptionalString(JsonObjectNode value, string key)
        {
            if (!value.TryGet(key, out var node)) throw new FormatException("'" + key + "' is missing.");
            if (node is JsonNullNode) return null;
            return node is JsonStringNode text ? text.Value : throw new FormatException("'" + key + "' must be a string or null.");
        }

        internal static long Integer(JsonObjectNode value, string key) =>
            value.TryGet(key, out var node) && node is JsonIntegerNode number ? number.Value : throw new FormatException("'" + key + "' must be an integer.");

        internal static bool Boolean(JsonObjectNode value, string key) =>
            value.TryGet(key, out var node) && node is JsonBooleanNode flag ? flag.Value : throw new FormatException("'" + key + "' must be true or false.");
    }
}
