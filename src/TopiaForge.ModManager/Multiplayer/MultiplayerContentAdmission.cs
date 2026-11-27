using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Multiplayer content decisions. A listen host admits a player through <see cref="MultiplayerAdmissionPlanner"/>,
    /// with this process as the server and the player's offer as the client. A joining player checks the host's
    /// descriptor first, as far as it can without the host's manifests: implicit packages exactly, by archive hash, since
    /// every player needs the identical package; for a required session mod only that it is here, since its version,
    /// protocol and synchronized files are the host's planner's to judge. Both answer with lines a player can act on,
    /// naming mods and versions, never an address.
    /// </summary>
    internal static class MultiplayerContentAdmission
    {
        private const string Absent = "absent";
        private const int MaximumLineLength = 256;

        /// <summary>Decides, on a listen host, whether the player presenting <paramref name="offer"/> may join.</summary>
        internal static InternalContentVerdict Evaluate(string gameBuild, IReadOnlyList<MultiplayerAdmissionMod> local, InternalContentOffer offer)
        {
            if (local == null) throw new ArgumentNullException(nameof(local));
            if (offer == null) throw new ArgumentNullException(nameof(offer));
            var server = new MultiplayerAdmissionProfile("host", gameBuild, MultiplayerModule.ProtocolVersion, MultiplayerModule.PeerVersionRange, local);
            var remote = new List<MultiplayerAdmissionMod>(offer.Mods.Count);
            foreach (var mod in offer.Mods) remote.Add(new MultiplayerAdmissionMod(ToManifest(mod), mod.PackageSha256));
            var client = new MultiplayerAdmissionProfile("player", offer.GameBuild, offer.ProtocolVersion, offer.PeerVersionRange, remote);
            var report = MultiplayerAdmissionPlanner.Evaluate(server, client, MultiplayerAdmissionPolicy.Compatible, MultiplayerAdmissionServerRole.ListenHost);
            if (report.IsAdmitted) return InternalContentVerdict.Admitted;

            var hostMods = Index(local);
            var playerMods = new Dictionary<string, InternalContentDescriptor>(StringComparer.Ordinal);
            foreach (var mod in offer.Mods) playerMods[Normalize(mod.Id)] = mod;
            var problems = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var mismatch in report.Mismatches)
            {
                var line = Bound(Describe(mismatch, gameBuild, offer.GameBuild, hostMods, playerMods));
                if (seen.Add(line)) problems.Add(line);
            }

            return InternalContentVerdict.Refused(problems);
        }

        /// <summary>
        /// Checks, on a joining player, the host's packages against this game's: each implicit package must be installed
        /// as the identical archive, each required session mod must be installed, and nothing enabled here may keep this
        /// player out. A required session mod at another version is the host's to judge: its planner admits a compatible
        /// protocol. Optional session mods never block; client-local mods are this player's own business.
        /// </summary>
        internal static InternalContentVerdict CheckHost(IReadOnlyList<InternalContentDescriptor> local, IReadOnlyList<InternalHostPackage> hostPackages)
        {
            if (local == null) throw new ArgumentNullException(nameof(local));
            if (hostPackages == null) throw new ArgumentNullException(nameof(hostPackages));
            var mine = new Dictionary<string, InternalContentDescriptor>(StringComparer.Ordinal);
            foreach (var descriptor in local) mine[Normalize(descriptor.Id)] = descriptor;
            var hosted = new HashSet<string>(StringComparer.Ordinal);
            var problems = new List<string>();
            foreach (var package in hostPackages)
            {
                var id = Normalize(package.Id);
                hosted.Add(id);
                if (package.Role == InternalHostPackageRole.SessionOptional) continue;
                if (!mine.TryGetValue(id, out var installed) || installed.Mode == InternalContentMode.ClientLocal)
                {
                    problems.Add(Bound(package.Id + " " + package.Version + " is missing."));
                }
                else if (package.Role == InternalHostPackageRole.Implicit && !string.Equals(installed.PackageSha256, package.Sha256, StringComparison.Ordinal))
                {
                    problems.Add(Bound(string.Equals(installed.Version, package.Version, StringComparison.Ordinal)
                        ? installed.DisplayName + " " + package.Version + " differs from the host's copy."
                        : installed.DisplayName + ": the host runs " + package.Version + "; this game has " + installed.Version + "."));
                }
            }

            foreach (var descriptor in local)
            {
                if (hosted.Contains(Normalize(descriptor.Id))) continue;
                switch (descriptor.Mode)
                {
                    case InternalContentMode.ImplicitShared:
                        problems.Add(Bound("The host does not run " + descriptor.DisplayName + "; disable it to join."));
                        break;
                    case InternalContentMode.Session when descriptor.Presence == InternalContentPresence.Required:
                        problems.Add(Bound("The host does not run " + descriptor.DisplayName + ", which every player needs; disable it to join."));
                        break;
                    case InternalContentMode.ServerOnly:
                        problems.Add(Bound(descriptor.DisplayName + " runs only on a host; disable it to join."));
                        break;
                    case InternalContentMode.Standalone:
                        problems.Add(Bound(descriptor.DisplayName + " refuses multiplayer; disable it to join."));
                        break;
                }
            }

            return problems.Count == 0 ? InternalContentVerdict.Admitted : InternalContentVerdict.Refused(problems);
        }

        /// <summary>
        /// Builds the manifest the planner reads for a mod a player presented. It carries exactly what the offer says: the
        /// identity, the mode and, for a session mod, its presence, protocol and synchronized files with their hashes.
        /// </summary>
        internal static ModManifest ToManifest(InternalContentDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            var manifest = new ModManifest
            {
                SchemaVersion = ModManifest.ManifestV6SchemaVersion,
                Id = descriptor.Id,
                Name = descriptor.DisplayName,
                Version = descriptor.Version
            };

            switch (descriptor.Mode)
            {
                case InternalContentMode.ClientLocal:
                    manifest.Multiplayer = new ModMultiplayerMetadata { Mode = ModMultiplayerMetadata.ClientLocalMode };
                    break;
                case InternalContentMode.ServerOnly:
                    manifest.Multiplayer = new ModMultiplayerMetadata { Mode = ModMultiplayerMetadata.ServerOnlyMode };
                    break;
                case InternalContentMode.Standalone:
                    manifest.Multiplayer = new ModMultiplayerMetadata { Mode = ModMultiplayerMetadata.StandaloneMode };
                    break;
                case InternalContentMode.Session:
                    var files = new List<string>(descriptor.SynchronizedFiles.Count);
                    foreach (var file in descriptor.SynchronizedFiles)
                    {
                        files.Add(file.Path);
                        manifest.Hashes[file.Path] = file.Sha256;
                    }

                    manifest.Multiplayer = new ModMultiplayerMetadata
                    {
                        Mode = ModMultiplayerMetadata.SessionMode,
                        Presence = descriptor.Presence == InternalContentPresence.Optional ? ModMultiplayerMetadata.OptionalPresence : ModMultiplayerMetadata.RequiredPresence,
                        Protocol = new ModMultiplayerProtocol { Version = descriptor.ProtocolVersion!, PeerVersionRange = descriptor.PeerVersionRange ?? string.Empty },
                        SynchronizedFiles = files
                    };
                    break;
            }

            return manifest;
        }

        private static string Describe(
            MultiplayerAdmissionMismatch mismatch,
            string hostBuild,
            string playerBuild,
            IReadOnlyDictionary<string, MultiplayerAdmissionMod> host,
            IReadOnlyDictionary<string, InternalContentDescriptor> player)
        {
            var id = mismatch.ModId;
            host.TryGetValue(id, out var hostMod);
            player.TryGetValue(id, out var playerMod);
            var name = hostMod != null ? DisplayName(hostMod.Manifest) : playerMod?.DisplayName ?? id;
            switch (mismatch.Code)
            {
                case MultiplayerAdmissionMismatchCode.GameBuildMismatch:
                    return "The host runs Robotopia build " + hostBuild + "; this game runs " + playerBuild + ".";
                case MultiplayerAdmissionMismatchCode.TopiaForgeProtocolMismatch:
                    return "The host's multiplayer protocol cannot play with this game's.";
                case MultiplayerAdmissionMismatchCode.StandaloneOnlyMod:
                    return playerMod != null ? name + " refuses multiplayer; disable it to join." : "The host runs " + name + ", which refuses multiplayer.";
                case MultiplayerAdmissionMismatchCode.MissingRequiredMod:
                    if (string.Equals(mismatch.ClientValue, Absent, StringComparison.Ordinal) && hostMod != null)
                    {
                        return name + " " + hostMod.Manifest.Version + " is missing.";
                    }

                    if (string.Equals(mismatch.ServerValue, Absent, StringComparison.Ordinal))
                    {
                        return "The host does not run " + name + "; disable it to join.";
                    }

                    return name + " is installed here as " + mismatch.ClientValue + "; the host needs it as " + mismatch.ServerValue + ".";
                case MultiplayerAdmissionMismatchCode.ModProtocolMismatch:
                    return name + ": the host's " + (hostMod?.Manifest.Version ?? "copy") + " cannot play with this game's " + (playerMod?.Version ?? "copy") + ".";
                case MultiplayerAdmissionMismatchCode.SynchronizedContentMismatch:
                    return name + ": its synchronized files differ from the host's.";
                case MultiplayerAdmissionMismatchCode.ServerOnlyModOnClient:
                    return name + " runs only on a host; disable it to join.";
                case MultiplayerAdmissionMismatchCode.ImplicitSharedContentMismatch:
                    var hostVersion = hostMod?.Manifest.Version ?? string.Empty;
                    var playerVersion = playerMod?.Version ?? string.Empty;
                    return string.Equals(hostVersion, playerVersion, StringComparison.Ordinal)
                        ? name + " " + hostVersion + " differs from the host's copy."
                        : name + ": the host runs " + hostVersion + "; this game has " + playerVersion + ".";
                case MultiplayerAdmissionMismatchCode.InvalidProfile:
                    if (id.Length == 0) return "This game's multiplayer details could not be read.";
                    return mismatch.ClientValue.Length > 0
                        ? "This game's copy of " + name + " cannot be used in multiplayer."
                        : "The host's copy of " + name + " cannot be used in multiplayer.";
                default:
                    return id.Length == 0 ? "The host refused this game's mods." : name + " does not match the host's copy.";
            }
        }

        private static Dictionary<string, MultiplayerAdmissionMod> Index(IReadOnlyList<MultiplayerAdmissionMod> mods)
        {
            var index = new Dictionary<string, MultiplayerAdmissionMod>(StringComparer.Ordinal);
            foreach (var mod in mods) index[Normalize(mod.Manifest.Id)] = mod;
            return index;
        }

        /// <summary>Mod ids compare as the planner compares them: trimmed and in lower case.</summary>
        private static string Normalize(string id) => (id ?? string.Empty).Trim().ToLowerInvariant();

        private static string DisplayName(ModManifest manifest) =>
            string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name.Trim();

        /// <summary>Keeps a line within the admission report's 256-character display bound.</summary>
        private static string Bound(string line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length <= MaximumLineLength) return trimmed;
            var cut = char.IsHighSurrogate(trimmed[MaximumLineLength - 2]) ? MaximumLineLength - 2 : MaximumLineLength - 1;
            return trimmed.Substring(0, cut).TrimEnd() + "…";
        }
    }
}
