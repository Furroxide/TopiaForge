using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class MultiplayerAdmissionTests
    {
        public static void Run()
        {
            AdmitsRequiredAndMutuallyCompatibleMods();
            RequiredPresenceNeedsASessionCopy();
            OptionalModsNegotiateWithoutBecomingRequired();
            GeneratedContractLockHashesParticipateInAdmission();
            EnforcesLogicalSideModes();
            RequiresMutualProtocolCompatibilityAndExactFallback();
            RejectsStandaloneMissingProtocolAndContentMismatches();
            RejectsInvalidProfilesDeterministically();
            ExactProfileIsOptInAndCuratesSessionInventory();
        }

        private static void AdmitsRequiredAndMutuallyCompatibleMods()
        {
            var server = Profile("server", Session("example.gameplay", "1.2.0", ">=1.0.0 <2.0.0", Hash('a')));
            var client = Profile("client", Session("example.gameplay", "1.4.0", ">=1.0.0 <2.0.0", Hash('a')));
            var report = MultiplayerAdmissionPlanner.Evaluate(server, client);
            Assert(report.IsAdmitted, "mutually compatible required mods should be admitted");
            Assert(report.ActiveSessionMods.SequenceEqual(new[] { "example.gameplay" }), "required mod should activate");
        }

        private static void RequiredPresenceNeedsASessionCopy()
        {
            var clientMasquerade = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Session("example.gameplay", "1.0.0", "1.0.0", Hash('a'))),
                Profile("client", Mode("example.gameplay", ModMultiplayerMetadata.ClientLocalMode)));
            var clientMissing = clientMasquerade.Mismatches.Single(item =>
                item.Code == MultiplayerAdmissionMismatchCode.MissingRequiredMod);
            Assert(clientMissing.ServerValue == "session/required" && clientMissing.ClientValue == "client-local",
                "a same-id client-local mod must not satisfy required session presence");
            Assert(clientMasquerade.ActiveSessionMods.Count == 0, "a masquerading local mod must not activate");

            var serverMasquerade = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Mode("example.gameplay", ModMultiplayerMetadata.ServerOnlyMode)),
                Profile("client", Session("example.gameplay", "1.0.0", "1.0.0", Hash('a'))));
            Assert(serverMasquerade.Mismatches.Any(item =>
                    item.Code == MultiplayerAdmissionMismatchCode.MissingRequiredMod &&
                    item.ServerValue == "server-only" && item.ClientValue == "session/required"),
                "a same-id server-only mod must not satisfy a client's required session declaration");
        }

        private static void OptionalModsNegotiateWithoutBecomingRequired()
        {
            var optional = Session(
                "example.optional",
                "1.0.0",
                "1.0.0",
                Hash('a'),
                ModMultiplayerMetadata.OptionalPresence);
            var absent = MultiplayerAdmissionPlanner.Evaluate(Profile("server", optional), Profile("client"));
            Assert(absent.IsAdmitted && absent.ActiveSessionMods.Count == 0 &&
                   absent.InactiveSessionReasons.Any(item =>
                       item.Code == MultiplayerAdmissionMismatchCode.OptionalSessionModUnavailable &&
                       item.ModId == "example.optional"),
                "an absent optional mod should not block and should explain why it did not activate");

            var shared = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", optional),
                Profile("client", Session(
                    "example.optional",
                    "1.0.0",
                    "1.0.0",
                    Hash('a'),
                    ModMultiplayerMetadata.OptionalPresence)));
            Assert(shared.IsAdmitted && shared.ActiveSessionMods.SequenceEqual(new[] { "example.optional" }),
                "mutually compatible optional copies should activate");

            var incompatible = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", optional),
                Profile("client", Session(
                    "example.optional",
                    "2.0.0",
                    "2.0.0",
                    Hash('b'),
                    ModMultiplayerMetadata.OptionalPresence)));
            Assert(incompatible.IsAdmitted && incompatible.ActiveSessionMods.Count == 0 &&
                   incompatible.InactiveSessionReasons.Any(item =>
                       item.Code == MultiplayerAdmissionMismatchCode.ModProtocolMismatch),
                "mutually incompatible optional copies should remain inactive with a structured reason");

            var requiredByServer = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Session("example.optional", "1.0.0", "1.0.0", Hash('a'))),
                Profile("client", Session(
                    "example.optional",
                    "2.0.0",
                    "2.0.0",
                    Hash('b'),
                    ModMultiplayerMetadata.OptionalPresence)));
            Assert(requiredByServer.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ModProtocolMismatch) &&
                   requiredByServer.ActiveSessionMods.Count == 0,
                "one required declaration must make incompatible negotiation admission-fatal");
        }

        private static void EnforcesLogicalSideModes()
        {
            var allowed = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Mode("example.server", ModMultiplayerMetadata.ServerOnlyMode)),
                Profile("client", Mode("example.client", ModMultiplayerMetadata.ClientLocalMode)));
            Assert(allowed.IsAdmitted, "server-only and client-local mods should be allowed on their logical sides");

            var reversed = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Mode("example.client", ModMultiplayerMetadata.ClientLocalMode)),
                Profile("client", Mode("example.server", ModMultiplayerMetadata.ServerOnlyMode)));
            Assert(reversed.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ClientLocalModOnServer),
                "client-local mods must be rejected from the logical server profile");
            Assert(reversed.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ServerOnlyModOnClient),
                "server-only mods must be rejected from the interactive client profile");
        }

        private static void GeneratedContractLockHashesParticipateInAdmission()
        {
            const string lockPath = "topiaforge.multiplayer.lock.json";
            var required = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Session(
                    "example.contract", "1.0.0", "1.0.0", Hash('a'), synchronizedPath: lockPath)),
                Profile("client", Session(
                    "example.contract", "1.0.0", "1.0.0", Hash('b'), synchronizedPath: lockPath)));
            Assert(!required.IsAdmitted && required.Mismatches.Any(item =>
                    item.Code == MultiplayerAdmissionMismatchCode.SynchronizedContentMismatch &&
                    item.Message.Contains(lockPath, StringComparison.Ordinal)),
                "different generated contract locks must reject a required session pair");

            var optional = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Session(
                    "example.contract", "1.0.0", "1.0.0", Hash('a'),
                    ModMultiplayerMetadata.OptionalPresence, synchronizedPath: lockPath)),
                Profile("client", Session(
                    "example.contract", "1.0.0", "1.0.0", Hash('b'),
                    ModMultiplayerMetadata.OptionalPresence, synchronizedPath: lockPath)));
            Assert(optional.IsAdmitted && optional.ActiveSessionMods.Count == 0 && optional.Mismatches.Count == 0 &&
                   optional.InactiveSessionReasons.Any(item =>
                       item.Code == MultiplayerAdmissionMismatchCode.SynchronizedContentMismatch),
                "different generated contract locks must leave an optional pair inactive with a structured reason");
        }

        private static void RequiresMutualProtocolCompatibilityAndExactFallback()
        {
            var oneWay = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Session("example.gameplay", "1.0.0", ">=1.0.0 <3.0.0", Hash('a'))),
                Profile("client", Session("example.gameplay", "2.0.0", ">=2.0.0 <3.0.0", Hash('a'))));
            Assert(oneWay.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ModProtocolMismatch),
                "both peers must accept the other protocol version");

            var omittedRange = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Session("example.gameplay", "1.0.0", string.Empty, Hash('a'))),
                Profile("client", Session("example.gameplay", "1.1.0", ">=1.0.0 <2.0.0", Hash('a'))));
            Assert(omittedRange.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ModProtocolMismatch),
                "an omitted per-mod peer range must require exact protocol equality");

            var topiaForgeExact = MultiplayerAdmissionPlanner.Evaluate(
                ProfileWithProtocol("server", "1.0.0", string.Empty),
                ProfileWithProtocol("client", "1.1.0", ">=1.0.0 <2.0.0"));
            Assert(topiaForgeExact.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.TopiaForgeProtocolMismatch),
                "an omitted TopiaForge peer range must require exact protocol equality");
        }

        private static void RejectsStandaloneMissingProtocolAndContentMismatches()
        {
            var standalone = Manifest("example.standalone");
            var server = Profile(
                "server",
                new MultiplayerAdmissionMod(standalone),
                Session("example.required", "1.0.0", "1.0.0", Hash('a')),
                Session("example.protocol", "1.0.0", "1.0.0", Hash('a')));
            var client = Profile(
                "client",
                Session("example.protocol", "2.0.0", "2.0.0", Hash('b')));
            var report = MultiplayerAdmissionPlanner.Evaluate(server, client);
            Assert(report.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.StandaloneOnlyMod),
                "undeclared V5 mods should block multiplayer");
            Assert(report.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.MissingRequiredMod),
                "missing required mod should block");
            Assert(report.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ModProtocolMismatch),
                "incompatible protocol should block");
            Assert(report.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.SynchronizedContentMismatch),
                "different synchronized hash should block");
        }

        private static void RejectsInvalidProfilesDeterministically()
        {
            var invalidSession = Manifest("example.invalid");
            invalidSession.Multiplayer = new ModMultiplayerMetadata
            {
                Mode = ModMultiplayerMetadata.SessionMode,
                Presence = ModMultiplayerMetadata.RequiredPresence,
                Protocol = null,
                SynchronizedFiles = new List<string> { "Content/rules.json" }
            };
            invalidSession.Hashes = new Dictionary<string, string> { ["Content/rules.json"] = "not-a-sha" };

            var invalidSchema = Manifest("example.schema");
            invalidSchema.SchemaVersion = 4;
            var report = MultiplayerAdmissionPlanner.Evaluate(
                new MultiplayerAdmissionProfile(
                    "server",
                    "0.0.2309",
                    "not-semver",
                    "bad range",
                    new[]
                    {
                        Session("Example.Duplicate", "1.0.0", "1.0.0", Hash('a')),
                        Session("example.duplicate", "1.0.0", "1.0.0", Hash('a')),
                        new MultiplayerAdmissionMod(invalidSession),
                        new MultiplayerAdmissionMod(invalidSchema)
                    }),
                Profile("client"));
            var invalid = report.Mismatches.Where(item => item.Code == MultiplayerAdmissionMismatchCode.InvalidProfile).ToArray();
            Assert(invalid.Length >= 4, "invalid protocol, duplicate ids, schema, and session metadata should be structured failures");
            Assert(invalid.SequenceEqual(invalid
                    .OrderBy(item => item.ModId, StringComparer.Ordinal)
                    .ThenBy(item => item.Message, StringComparer.Ordinal)
                    .ThenBy(item => item.ServerValue, StringComparer.Ordinal)),
                "invalid-profile output should be deterministic");
            Assert(!report.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.TopiaForgeProtocolMismatch),
                "invalid protocol metadata should not be mislabeled as peer incompatibility");
        }

        private static void ExactProfileIsOptInAndCuratesSessionInventory()
        {
            var server = Profile("server", Session(
                "example.gameplay", "1.0.0", ">=1.0.0 <2.0.0", Hash('a'),
                packageVersion: "2.0.0", archiveHash: Hash('1')));
            var client = Profile("client", Session(
                "example.gameplay", "1.0.0", ">=1.0.0 <2.0.0", Hash('a'),
                packageVersion: "2.1.0", archiveHash: Hash('2')));
            Assert(MultiplayerAdmissionPlanner.Evaluate(server, client).IsAdmitted,
                "package versions and archives are not default wire compatibility");
            var exact = MultiplayerAdmissionPlanner.Evaluate(server, client, MultiplayerAdmissionPolicy.ExactProfile);
            Assert(exact.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ExactProfileMismatch),
                "exact profile should compare package identity");
            Assert(exact.ActiveSessionMods.Count == 0, "a rejected exact-profile pair must not be reported active");

            var optionalInventory = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Session(
                    "example.optional", "1.0.0", "1.0.0", Hash('a'), ModMultiplayerMetadata.OptionalPresence)),
                Profile("client"),
                MultiplayerAdmissionPolicy.ExactProfile);
            Assert(optionalInventory.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ExactProfileMismatch),
                "exact profile should curate optional session-mod presence too");

            var missingArchive = MultiplayerAdmissionPlanner.Evaluate(
                Profile("server", Session("example.hash", "1.0.0", "1.0.0", Hash('a'), archiveHash: string.Empty)),
                Profile("client", Session("example.hash", "1.0.0", "1.0.0", Hash('a'), archiveHash: string.Empty)),
                MultiplayerAdmissionPolicy.ExactProfile);
            Assert(missingArchive.Mismatches.Any(item => item.Code == MultiplayerAdmissionMismatchCode.ExactProfileMismatch),
                "exact profile must require real archive SHA-256 values, not two equal empty strings");
        }

        private static MultiplayerAdmissionProfile Profile(string id, params MultiplayerAdmissionMod[] mods) =>
            new MultiplayerAdmissionProfile(id, "0.0.2309", "1.0.0", ">=1.0.0 <2.0.0", mods);

        private static MultiplayerAdmissionProfile ProfileWithProtocol(string id, string protocol, string range) =>
            new MultiplayerAdmissionProfile(id, "0.0.2309", protocol, range, Array.Empty<MultiplayerAdmissionMod>());

        private static MultiplayerAdmissionMod Session(
            string id,
            string protocol,
            string peerRange,
            string contentHash,
            string presence = ModMultiplayerMetadata.RequiredPresence,
            string packageVersion = "1.0.0",
            string? archiveHash = null,
            string synchronizedPath = "Content/rules.json")
        {
            var manifest = Manifest(id, packageVersion);
            var synchronizedFiles = new List<string>
            {
                ModMultiplayerMetadata.ContractLockFileName
            };
            if (!string.Equals(
                    synchronizedPath,
                    ModMultiplayerMetadata.ContractLockFileName,
                    StringComparison.Ordinal))
            {
                synchronizedFiles.Add(synchronizedPath);
            }
            manifest.Multiplayer = new ModMultiplayerMetadata
            {
                Mode = ModMultiplayerMetadata.SessionMode,
                Presence = presence,
                Protocol = new ModMultiplayerProtocol
                {
                    Version = protocol,
                    PeerVersionRange = peerRange ?? string.Empty
                },
                SynchronizedFiles = synchronizedFiles
            };
            manifest.Hashes = synchronizedFiles.ToDictionary(
                path => path,
                _ => contentHash,
                StringComparer.Ordinal);
            return new MultiplayerAdmissionMod(manifest, archiveHash ?? Hash('f'));
        }

        private static MultiplayerAdmissionMod Mode(string id, string mode)
        {
            var manifest = Manifest(id);
            manifest.Multiplayer = new ModMultiplayerMetadata { Mode = mode };
            return new MultiplayerAdmissionMod(manifest, Hash('f'));
        }

        private static ModManifest Manifest(string id, string version = "1.0.0") => new ModManifest
        {
            SchemaVersion = ModManifest.CurrentSchemaVersion,
            Id = id,
            Name = id,
            Version = version
        };

        private static string Hash(char value) => new string(value, 64);

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException("Multiplayer admission test failed: " + message);
        }
    }
}
