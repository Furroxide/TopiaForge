using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Identifies one deterministic multiplayer admission failure.</summary>
    public enum MultiplayerAdmissionMismatchCode
    {
        InvalidProfile = 0,
        GameBuildMismatch = 1,
        TopiaForgeProtocolMismatch = 2,
        StandaloneOnlyMod = 3,
        MissingRequiredMod = 4,
        ModProtocolMismatch = 5,
        SynchronizedContentMismatch = 6,
        ServerOnlyModOnClient = 7,
        ExactProfileMismatch = 8,
        ClientLocalModOnServer = 9,
        OptionalSessionModUnavailable = 10
    }

    /// <summary>Controls whether admission uses wire compatibility or exact curated profile equality.</summary>
    public enum MultiplayerAdmissionPolicy
    {
        Compatible = 0,
        ExactProfile = 1
    }

    /// <summary>One enabled packaged mod offered by a peer during admission.</summary>
    public sealed class MultiplayerAdmissionMod
    {
        public MultiplayerAdmissionMod(ModManifest manifest, string packageSha256 = "")
        {
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            PackageSha256 = packageSha256 ?? string.Empty;
        }

        public ModManifest Manifest { get; }
        public string PackageSha256 { get; }
    }

    /// <summary>Immutable handshake input for one logical server or interactive client.</summary>
    public sealed class MultiplayerAdmissionProfile
    {
        public MultiplayerAdmissionProfile(
            string peerId,
            string gameBuild,
            string topiaForgeProtocolVersion,
            string topiaForgePeerVersionRange,
            IReadOnlyList<MultiplayerAdmissionMod> mods)
        {
            PeerId = Require(peerId, nameof(peerId));
            GameBuild = Require(gameBuild, nameof(gameBuild));
            TopiaForgeProtocolVersion = Require(topiaForgeProtocolVersion, nameof(topiaForgeProtocolVersion));
            TopiaForgePeerVersionRangeIsPresent = !string.IsNullOrWhiteSpace(topiaForgePeerVersionRange);
            TopiaForgePeerVersionRange = TopiaForgePeerVersionRangeIsPresent
                ? topiaForgePeerVersionRange.Trim()
                : TopiaForgeProtocolVersion;
            Mods = (mods ?? throw new ArgumentNullException(nameof(mods))).ToArray();
        }

        public string PeerId { get; }
        public string GameBuild { get; }
        public string TopiaForgeProtocolVersion { get; }
        /// <summary>Gets whether the peer advertised a range instead of requesting exact protocol equality.</summary>
        public bool TopiaForgePeerVersionRangeIsPresent { get; }
        public string TopiaForgePeerVersionRange { get; }
        public IReadOnlyList<MultiplayerAdmissionMod> Mods { get; }

        private static string Require(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A non-empty value is required.", parameterName);
            return value;
        }
    }

    /// <summary>One structured, actionable mismatch between server and client.</summary>
    public sealed class MultiplayerAdmissionMismatch
    {
        public MultiplayerAdmissionMismatch(
            MultiplayerAdmissionMismatchCode code,
            string message,
            string modId = "",
            string serverValue = "",
            string clientValue = "")
        {
            Code = code;
            Message = message ?? string.Empty;
            ModId = modId ?? string.Empty;
            ServerValue = serverValue ?? string.Empty;
            ClientValue = clientValue ?? string.Empty;
        }

        public MultiplayerAdmissionMismatchCode Code { get; }
        public string Message { get; }
        public string ModId { get; }
        public string ServerValue { get; }
        public string ClientValue { get; }
    }

    /// <summary>Deterministic admission result produced before world loading.</summary>
    public sealed class MultiplayerAdmissionReport
    {
        internal MultiplayerAdmissionReport(
            IReadOnlyList<string> activeSessionMods,
            IReadOnlyList<MultiplayerAdmissionMismatch> mismatches,
            IReadOnlyList<MultiplayerAdmissionMismatch> inactiveSessionReasons)
        {
            ActiveSessionMods = activeSessionMods;
            Mismatches = mismatches;
            InactiveSessionReasons = inactiveSessionReasons;
        }

        public bool IsAdmitted => Mismatches.Count == 0;
        public IReadOnlyList<string> ActiveSessionMods { get; }
        public IReadOnlyList<MultiplayerAdmissionMismatch> Mismatches { get; }
        /// <summary>Gets non-fatal reasons why optional session mods did not activate.</summary>
        public IReadOnlyList<MultiplayerAdmissionMismatch> InactiveSessionReasons { get; }
    }

    /// <summary>Applies server-canonical V5 presence, protocol, and content admission rules.</summary>
    public static class MultiplayerAdmissionPlanner
    {
        private static readonly Regex Sha256Regex = new Regex("^[A-Fa-f0-9]{64}$", RegexOptions.Compiled);

        public static MultiplayerAdmissionReport Evaluate(
            MultiplayerAdmissionProfile server,
            MultiplayerAdmissionProfile client,
            MultiplayerAdmissionPolicy policy = MultiplayerAdmissionPolicy.Compatible)
        {
            if (server == null) throw new ArgumentNullException(nameof(server));
            if (client == null) throw new ArgumentNullException(nameof(client));
            if (!Enum.IsDefined(typeof(MultiplayerAdmissionPolicy), policy)) throw new ArgumentOutOfRangeException(nameof(policy));

            var mismatches = new List<MultiplayerAdmissionMismatch>();
            var inactiveSessionReasons = new List<MultiplayerAdmissionMismatch>();
            if (!string.Equals(server.GameBuild, client.GameBuild, StringComparison.Ordinal))
            {
                mismatches.Add(new MultiplayerAdmissionMismatch(
                    MultiplayerAdmissionMismatchCode.GameBuildMismatch,
                    "Robotopia builds must match exactly.",
                    serverValue: server.GameBuild,
                    clientValue: client.GameBuild));
            }

            var serverProtocolIsValid = ValidateProfileProtocol(server, "server", mismatches);
            var clientProtocolIsValid = ValidateProfileProtocol(client, "client", mismatches);
            if (serverProtocolIsValid && clientProtocolIsValid &&
                !AreMutuallyCompatible(
                    server.TopiaForgeProtocolVersion,
                    server.TopiaForgePeerVersionRange,
                    server.TopiaForgePeerVersionRangeIsPresent,
                    client.TopiaForgeProtocolVersion,
                    client.TopiaForgePeerVersionRange,
                    client.TopiaForgePeerVersionRangeIsPresent))
            {
                mismatches.Add(new MultiplayerAdmissionMismatch(
                    MultiplayerAdmissionMismatchCode.TopiaForgeProtocolMismatch,
                    "The TopiaForge multiplayer protocol ranges are not mutually compatible.",
                    serverValue: ProtocolDisplay(
                        server.TopiaForgeProtocolVersion,
                        server.TopiaForgePeerVersionRange,
                        server.TopiaForgePeerVersionRangeIsPresent),
                    clientValue: ProtocolDisplay(
                        client.TopiaForgeProtocolVersion,
                        client.TopiaForgePeerVersionRange,
                        client.TopiaForgePeerVersionRangeIsPresent)));
            }

            var serverMods = Index(server, "server", mismatches);
            var clientMods = Index(client, "client", mismatches);
            RejectStandaloneOnly(serverMods, "server", mismatches);
            RejectStandaloneOnly(clientMods, "client", mismatches);
            RejectWrongSideMods(serverMods, clientMods, mismatches);

            var active = new SortedSet<string>(StringComparer.Ordinal);
            var allIds = new SortedSet<string>(serverMods.Keys, StringComparer.Ordinal);
            allIds.UnionWith(clientMods.Keys);
            foreach (var id in allIds)
            {
                serverMods.TryGetValue(id, out var serverMod);
                clientMods.TryGetValue(id, out var clientMod);
                var serverMetadata = serverMod?.Manifest.Multiplayer;
                var clientMetadata = clientMod?.Manifest.Multiplayer;
                var serverSession = IsSession(serverMetadata);
                var clientSession = IsSession(clientMetadata);
                var requiredByServer = serverSession && IsRequired(serverMetadata!);
                var requiredByClient = clientSession && IsRequired(clientMetadata!);
                var missingRequiredCopy = false;

                if (requiredByServer && !clientSession)
                {
                    AddMissing(
                        id,
                        "The server requires a session-compatible copy of this mod on every client.",
                        "session/required",
                        ModeDisplay(clientMod),
                        mismatches);
                    missingRequiredCopy = true;
                }

                if (requiredByClient && !serverSession)
                {
                    AddMissing(
                        id,
                        "The client requires a session-compatible copy of this mod on the server.",
                        ModeDisplay(serverMod),
                        "session/required",
                        mismatches);
                    missingRequiredCopy = true;
                }

                if (missingRequiredCopy) continue;

                if (!serverSession || !clientSession)
                {
                    if (policy == MultiplayerAdmissionPolicy.ExactProfile && (serverSession || clientSession))
                    {
                        mismatches.Add(new MultiplayerAdmissionMismatch(
                            MultiplayerAdmissionMismatchCode.ExactProfileMismatch,
                            "The exact-profile policy requires the same session-mod inventory on both peers.",
                            id,
                            ModeDisplay(serverMod),
                            ModeDisplay(clientMod)));
                    }
                    else if (serverSession || clientSession)
                    {
                        inactiveSessionReasons.Add(new MultiplayerAdmissionMismatch(
                            MultiplayerAdmissionMismatchCode.OptionalSessionModUnavailable,
                            "The optional session mod is present on only one peer and remains inactive.",
                            id,
                            ModeDisplay(serverMod),
                            ModeDisplay(clientMod)));
                    }
                    continue;
                }

                var pairMismatches = new List<MultiplayerAdmissionMismatch>();
                CompareProtocol(id, serverMetadata!.Protocol!, clientMetadata!.Protocol!, pairMismatches);
                CompareSynchronizedContent(id, serverMod!.Manifest, clientMod!.Manifest, pairMismatches);
                if (policy == MultiplayerAdmissionPolicy.ExactProfile)
                {
                    CompareExactProfile(id, serverMod, clientMod, pairMismatches);
                }

                if (pairMismatches.Count == 0)
                {
                    active.Add(id);
                }
                else if (requiredByServer || requiredByClient || policy == MultiplayerAdmissionPolicy.ExactProfile)
                {
                    mismatches.AddRange(pairMismatches);
                }
                else
                {
                    inactiveSessionReasons.AddRange(pairMismatches);
                }
            }

            return new MultiplayerAdmissionReport(
                active.ToArray(),
                mismatches
                    .OrderBy(item => item.Code)
                    .ThenBy(item => item.ModId, StringComparer.Ordinal)
                    .ThenBy(item => item.Message, StringComparer.Ordinal)
                    .ThenBy(item => item.ServerValue, StringComparer.Ordinal)
                    .ThenBy(item => item.ClientValue, StringComparer.Ordinal)
                    .ToArray(),
                inactiveSessionReasons
                    .OrderBy(item => item.Code)
                    .ThenBy(item => item.ModId, StringComparer.Ordinal)
                    .ThenBy(item => item.Message, StringComparer.Ordinal)
                    .ThenBy(item => item.ServerValue, StringComparer.Ordinal)
                    .ThenBy(item => item.ClientValue, StringComparer.Ordinal)
                    .ToArray());
        }

        private static bool ValidateProfileProtocol(
            MultiplayerAdmissionProfile profile,
            string side,
            ICollection<MultiplayerAdmissionMismatch> mismatches)
        {
            var versionIsValid = VersionUtil.TryParse(profile.TopiaForgeProtocolVersion, out _);
            var rangeIsValid = !profile.TopiaForgePeerVersionRangeIsPresent ||
                               VersionUtil.TryParseRange(profile.TopiaForgePeerVersionRange);
            if (versionIsValid && rangeIsValid) return true;

            AddInvalid(
                side,
                string.Empty,
                "The " + side + " profile has invalid TopiaForge multiplayer protocol metadata.",
                ProtocolDisplay(
                    profile.TopiaForgeProtocolVersion,
                    profile.TopiaForgePeerVersionRange,
                    profile.TopiaForgePeerVersionRangeIsPresent),
                mismatches);
            return false;
        }

        private static Dictionary<string, MultiplayerAdmissionMod> Index(
            MultiplayerAdmissionProfile profile,
            string side,
            ICollection<MultiplayerAdmissionMismatch> mismatches)
        {
            var result = new Dictionary<string, MultiplayerAdmissionMod>(StringComparer.Ordinal);
            var candidates = new Dictionary<string, List<MultiplayerAdmissionMod>>(StringComparer.Ordinal);
            foreach (var item in profile.Mods)
            {
                if (item == null)
                {
                    AddInvalid(side, string.Empty, "The " + side + " profile contains a null mod entry.", string.Empty, mismatches);
                    continue;
                }

                var rawId = item.Manifest.Id ?? string.Empty;
                var id = rawId.Trim().ToLowerInvariant();
                if (!ManifestValidator.IsValidId(rawId))
                {
                    AddInvalid(
                        side,
                        id,
                        "The " + side + " profile contains an invalid mod id.",
                        rawId,
                        mismatches);
                    continue;
                }

                if (!candidates.TryGetValue(id, out var entries))
                {
                    entries = new List<MultiplayerAdmissionMod>();
                    candidates.Add(id, entries);
                }
                entries.Add(item);
            }

            foreach (var candidate in candidates.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                if (candidate.Value.Count != 1)
                {
                    AddInvalid(
                        side,
                        candidate.Key,
                        "The " + side + " profile contains duplicate normalized mod ids.",
                        candidate.Key,
                        mismatches);
                    continue;
                }

                var item = candidate.Value[0];
                if (ValidateManifestForAdmission(item.Manifest, candidate.Key, side, mismatches))
                {
                    result.Add(candidate.Key, item);
                }
            }
            return result;
        }

        private static bool ValidateManifestForAdmission(
            ModManifest manifest,
            string id,
            string side,
            ICollection<MultiplayerAdmissionMismatch> mismatches)
        {
            var errors = new List<string>();
            if (!ModManifest.IsSupportedSchemaVersion(manifest.SchemaVersion))
            {
                errors.Add("schemaVersion must be 5 or 6");
            }
            else if (!VersionUtil.TryParse(manifest.Version, out _))
            {
                errors.Add("package version must be an exact semantic version");
            }

            var multiplayer = manifest.Multiplayer;
            if (errors.Count == 0 && multiplayer != null)
            {
                ValidateMultiplayerMetadata(manifest, multiplayer, errors);
            }

            foreach (var error in errors.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal))
            {
                AddInvalid(
                    side,
                    id,
                    "The " + side + " manifest is invalid for multiplayer admission: " + error + ".",
                    error,
                    mismatches);
            }
            return errors.Count == 0;
        }

        private static void ValidateMultiplayerMetadata(
            ModManifest manifest,
            ModMultiplayerMetadata multiplayer,
            ICollection<string> errors)
        {
            var isClientLocal = string.Equals(multiplayer.Mode, ModMultiplayerMetadata.ClientLocalMode, StringComparison.Ordinal);
            var isServerOnly = string.Equals(multiplayer.Mode, ModMultiplayerMetadata.ServerOnlyMode, StringComparison.Ordinal);
            var isSession = string.Equals(multiplayer.Mode, ModMultiplayerMetadata.SessionMode, StringComparison.Ordinal);
            if (!isClientLocal && !isServerOnly && !isSession)
            {
                errors.Add("multiplayer.mode must be client-local, server-only, or session");
                return;
            }

            if (!isSession)
            {
                if (multiplayer.PresenceWasPresent || !string.IsNullOrEmpty(multiplayer.Presence) ||
                    multiplayer.Protocol != null || multiplayer.SynchronizedFilesWasPresent ||
                    (multiplayer.SynchronizedFiles?.Count ?? 0) != 0)
                {
                    errors.Add("non-session modes cannot declare presence, protocol, or synchronized files");
                }
                return;
            }

            if (!string.Equals(multiplayer.Presence, ModMultiplayerMetadata.RequiredPresence, StringComparison.Ordinal) &&
                !string.Equals(multiplayer.Presence, ModMultiplayerMetadata.OptionalPresence, StringComparison.Ordinal))
            {
                errors.Add("session presence must be required or optional");
            }

            var protocol = multiplayer.Protocol;
            if (protocol == null)
            {
                errors.Add("session protocol is required");
            }
            else
            {
                if (!VersionUtil.TryParse(protocol.Version, out _))
                {
                    errors.Add("session protocol version must be an exact semantic version");
                }
                var hasRange = HasDeclaredPeerRange(protocol);
                if (hasRange && (string.IsNullOrWhiteSpace(protocol.PeerVersionRange) ||
                                 !VersionUtil.TryParseRange(protocol.PeerVersionRange)))
                {
                    errors.Add("session protocol peer range is invalid");
                }
            }

            var synchronizedFiles = multiplayer.SynchronizedFiles;
            if (synchronizedFiles == null)
            {
                errors.Add("synchronized files cannot be null");
                return;
            }
            if (synchronizedFiles.Count > ModMultiplayerMetadata.MaxSynchronizedFiles)
            {
                errors.Add("synchronized files exceed the bounded entry limit");
            }
            if (!synchronizedFiles.Contains(
                    ModMultiplayerMetadata.ContractLockFileName,
                    StringComparer.Ordinal))
            {
                errors.Add("session synchronized files must include the canonical generated multiplayer contract lock");
            }

            var collisionKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in synchronizedFiles)
            {
                if (!PortablePackagePath.TryValidate(path, out _, out var collisionKey, out _))
                {
                    errors.Add("synchronized file paths must be safe portable relative paths");
                    continue;
                }
                if (!collisionKeys.Add(collisionKey))
                {
                    errors.Add("synchronized file paths must not contain portable collisions");
                }

                if (manifest.Hashes == null || !manifest.Hashes.TryGetValue(path, out var hash) ||
                    string.IsNullOrWhiteSpace(hash) || !Sha256Regex.IsMatch(hash))
                {
                    errors.Add("every synchronized file must have a packed SHA-256 hash");
                }
            }
        }

        private static void RejectStandaloneOnly(
            IReadOnlyDictionary<string, MultiplayerAdmissionMod> mods,
            string side,
            ICollection<MultiplayerAdmissionMismatch> mismatches)
        {
            foreach (var item in mods.Where(item => item.Value.Manifest.IsStandaloneOnly))
            {
                mismatches.Add(new MultiplayerAdmissionMismatch(
                    MultiplayerAdmissionMismatchCode.StandaloneOnlyMod,
                    "The " + side + " enables a standalone-only mod. Disable it in an explicitly confirmed derived profile or add multiplayer metadata.",
                    item.Key));
            }
        }

        private static void RejectWrongSideMods(
            IReadOnlyDictionary<string, MultiplayerAdmissionMod> serverMods,
            IReadOnlyDictionary<string, MultiplayerAdmissionMod> clientMods,
            ICollection<MultiplayerAdmissionMismatch> mismatches)
        {
            foreach (var item in serverMods.Where(item => string.Equals(
                         item.Value.Manifest.Multiplayer?.Mode,
                         ModMultiplayerMetadata.ClientLocalMode,
                         StringComparison.Ordinal)))
            {
                mismatches.Add(new MultiplayerAdmissionMismatch(
                    MultiplayerAdmissionMismatchCode.ClientLocalModOnServer,
                    "A client-local mod is enabled in the logical server profile.",
                    item.Key,
                    ModMultiplayerMetadata.ClientLocalMode,
                    "absent"));
            }

            foreach (var item in clientMods.Where(item => string.Equals(
                         item.Value.Manifest.Multiplayer?.Mode,
                         ModMultiplayerMetadata.ServerOnlyMode,
                         StringComparison.Ordinal)))
            {
                mismatches.Add(new MultiplayerAdmissionMismatch(
                    MultiplayerAdmissionMismatchCode.ServerOnlyModOnClient,
                    "A server-only mod is enabled in the interactive client profile.",
                    item.Key,
                    "absent",
                    ModMultiplayerMetadata.ServerOnlyMode));
            }
        }

        private static bool IsSession(ModMultiplayerMetadata? metadata) =>
            metadata != null && string.Equals(metadata.Mode, ModMultiplayerMetadata.SessionMode, StringComparison.Ordinal);

        private static bool IsRequired(ModMultiplayerMetadata metadata) =>
            string.Equals(metadata.Presence, ModMultiplayerMetadata.RequiredPresence, StringComparison.Ordinal);

        private static void AddMissing(
            string id,
            string message,
            string serverValue,
            string clientValue,
            ICollection<MultiplayerAdmissionMismatch> mismatches) =>
            mismatches.Add(new MultiplayerAdmissionMismatch(
                MultiplayerAdmissionMismatchCode.MissingRequiredMod,
                message,
                id,
                serverValue,
                clientValue));

        private static void CompareProtocol(
            string id,
            ModMultiplayerProtocol server,
            ModMultiplayerProtocol client,
            ICollection<MultiplayerAdmissionMismatch> mismatches)
        {
            var serverHasRange = HasDeclaredPeerRange(server);
            var clientHasRange = HasDeclaredPeerRange(client);
            if (AreMutuallyCompatible(
                server.Version,
                server.EffectivePeerVersionRange,
                serverHasRange,
                client.Version,
                client.EffectivePeerVersionRange,
                clientHasRange)) return;
            mismatches.Add(new MultiplayerAdmissionMismatch(
                MultiplayerAdmissionMismatchCode.ModProtocolMismatch,
                "The mod protocol ranges are not mutually compatible.",
                id,
                ProtocolDisplay(server.Version, server.EffectivePeerVersionRange, serverHasRange),
                ProtocolDisplay(client.Version, client.EffectivePeerVersionRange, clientHasRange)));
        }

        private static void CompareSynchronizedContent(
            string id,
            ModManifest server,
            ModManifest client,
            ICollection<MultiplayerAdmissionMismatch> mismatches)
        {
            var serverPaths = new SortedSet<string>(server.Multiplayer!.SynchronizedFiles, StringComparer.Ordinal);
            var clientPaths = new SortedSet<string>(client.Multiplayer!.SynchronizedFiles, StringComparer.Ordinal);
            if (!serverPaths.SetEquals(clientPaths))
            {
                AddContentMismatch(id, "The synchronized-file inventories differ.", string.Join(",", serverPaths), string.Join(",", clientPaths), mismatches);
                return;
            }

            foreach (var path in serverPaths)
            {
                server.Hashes.TryGetValue(path, out var serverHash);
                client.Hashes.TryGetValue(path, out var clientHash);
                if (!string.Equals(serverHash, clientHash, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(serverHash))
                {
                    AddContentMismatch(id, "Synchronized content differs at '" + path + "'.", serverHash ?? string.Empty, clientHash ?? string.Empty, mismatches);
                }
            }
        }

        private static void AddContentMismatch(
            string id,
            string message,
            string serverValue,
            string clientValue,
            ICollection<MultiplayerAdmissionMismatch> mismatches) =>
            mismatches.Add(new MultiplayerAdmissionMismatch(
                MultiplayerAdmissionMismatchCode.SynchronizedContentMismatch,
                message,
                id,
                serverValue,
                clientValue));

        private static void CompareExactProfile(
            string id,
            MultiplayerAdmissionMod server,
            MultiplayerAdmissionMod client,
            ICollection<MultiplayerAdmissionMismatch> mismatches)
        {
            if (string.Equals(server.Manifest.Version, client.Manifest.Version, StringComparison.Ordinal) &&
                Sha256Regex.IsMatch(server.PackageSha256) &&
                Sha256Regex.IsMatch(client.PackageSha256) &&
                string.Equals(server.PackageSha256, client.PackageSha256, StringComparison.OrdinalIgnoreCase)) return;
            mismatches.Add(new MultiplayerAdmissionMismatch(
                MultiplayerAdmissionMismatchCode.ExactProfileMismatch,
                "The exact-profile policy requires equal package versions and archive hashes.",
                id,
                server.Manifest.Version + "@" + server.PackageSha256,
                client.Manifest.Version + "@" + client.PackageSha256));
        }

        private static bool AreMutuallyCompatible(
            string leftVersion,
            string leftRange,
            bool leftHasRange,
            string rightVersion,
            string rightRange,
            bool rightHasRange) =>
            Accepts(leftVersion, leftRange, leftHasRange, rightVersion) &&
            Accepts(rightVersion, rightRange, rightHasRange, leftVersion);

        private static bool Accepts(string localVersion, string localRange, bool hasRange, string remoteVersion) =>
            hasRange
                ? VersionUtil.AllowsRange(remoteVersion, localRange)
                : string.Equals(localVersion, remoteVersion, StringComparison.Ordinal);

        private static bool HasDeclaredPeerRange(ModMultiplayerProtocol protocol) =>
            protocol.PeerVersionRangeWasPresent || !string.IsNullOrEmpty(protocol.PeerVersionRange);

        private static string ModeDisplay(MultiplayerAdmissionMod? item) =>
            item == null
                ? "absent"
                : item.Manifest.Multiplayer == null
                    ? "standalone-only"
                    : item.Manifest.Multiplayer.Mode;

        private static void AddInvalid(
            string side,
            string id,
            string message,
            string value,
            ICollection<MultiplayerAdmissionMismatch> mismatches) =>
            mismatches.Add(new MultiplayerAdmissionMismatch(
                MultiplayerAdmissionMismatchCode.InvalidProfile,
                message,
                id,
                string.Equals(side, "server", StringComparison.Ordinal) ? value : string.Empty,
                string.Equals(side, "client", StringComparison.Ordinal) ? value : string.Empty));

        private static string ProtocolDisplay(string version, string range, bool hasRange) =>
            version + " accepts " + (hasRange ? range : "exactly " + version);
    }
}
