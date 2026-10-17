using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>
    /// Live sessions' content, from the runtime attached through the internal control: the packages a host announces, the
    /// offer a joining player presents, the host's admission through the runtime's planner, and the joining player's own
    /// check. With no runtime attached nothing is announced and nobody is admitted, so a host can never let anyone in
    /// without the runtime's decision. The same holds while the runtime cannot read this game's packages, as when another
    /// program holds a file of one without an install receipt while it is hashed: the failure is reported, hosting does not
    /// start and a running host refuses every player, and a joining player stops before anything connects. Main thread only.
    /// </summary>
    internal sealed class RuntimeContent : ILiveSessionContent
    {
        internal const string HostNotReady = "The host is not ready to admit players yet.";
        internal const string ClientNotReady = "This game is not ready to join sessions yet.";
        internal const string OfferUnreadable = "The host could not read this game's list of mods.";
        internal const string ContentUnreadable = "This game's mods could not be read: a file of one of them is in use or cannot be opened.";
        internal const string HostContentUnreadable = "The host could not read its own mods.";

        private readonly string fallbackGameBuild;
        private readonly string fallbackRuntimeVersion;
        private IInternalMultiplayerRuntimeHost? runtime;
        private IReadOnlyList<InternalContentDescriptor>? offeredContent;
        private byte[]? encodedOffer;

        /// <param name="fallbackGameBuild">The build to describe before a runtime attaches.</param>
        /// <param name="fallbackRuntimeVersion">The runtime version to describe before a runtime attaches.</param>
        internal RuntimeContent(string fallbackGameBuild, string fallbackRuntimeVersion)
        {
            this.fallbackGameBuild = ServerDescriptor.IsGameBuild(fallbackGameBuild) ? fallbackGameBuild : "unknown";
            this.fallbackRuntimeVersion = ServerDescriptor.IsSemanticVersion(fallbackRuntimeVersion) ? fallbackRuntimeVersion : "0.0.0";
        }

        /// <summary>Gets or sets the attached runtime. Changing it forgets the cached offer.</summary>
        internal IInternalMultiplayerRuntimeHost? Runtime
        {
            get => runtime;
            set
            {
                runtime = value;
                offeredContent = null;
                encodedOffer = null;
            }
        }

        /// <summary>Gets or sets where a failing runtime call is reported.</summary>
        internal Action<InternalMultiplayerDiagnostic>? Report { get; set; }

        public string GameBuild
        {
            get
            {
                var build = runtime?.GameBuild;
                return ServerDescriptor.IsGameBuild(build) ? build! : fallbackGameBuild;
            }
        }

        public string RuntimeVersion
        {
            get
            {
                var version = runtime?.RuntimeVersion;
                return ServerDescriptor.IsSemanticVersion(version) ? version! : fallbackRuntimeVersion;
            }
        }

        /// <summary>
        /// Gets the shared packages for the descriptor: implicit and session mods, never client-local or server-only ones.
        /// Null when this game's packages cannot be read, so the host does not start: a descriptor listing none would tell
        /// every joining player that the host runs none.
        /// </summary>
        public IReadOnlyList<DescriptorPackage>? HostPackages()
        {
            var current = runtime;
            if (current == null) return Array.Empty<DescriptorPackage>();
            var local = ReadLocalContent(current);
            if (local == null) return null;
            var packages = new List<DescriptorPackage>();
            foreach (var descriptor in local)
            {
                DescriptorPackageRole role;
                switch (descriptor.Mode)
                {
                    case InternalContentMode.ImplicitShared:
                        role = DescriptorPackageRole.Implicit;
                        break;
                    case InternalContentMode.Session:
                        role = descriptor.Presence == InternalContentPresence.Required ? DescriptorPackageRole.SessionRequired : DescriptorPackageRole.SessionOptional;
                        break;
                    default:
                        continue;
                }

                if (!DescriptorPackage.IsModId(descriptor.Id) || !ServerDescriptor.IsSemanticVersion(descriptor.Version))
                {
                    Warn("content.descriptor-skipped", "A package cannot be listed in the server descriptor: " + descriptor.Id + ".");
                    continue;
                }

                packages.Add(new DescriptorPackage(descriptor.Id, descriptor.Version, descriptor.PackageSha256, role));
            }

            packages.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            if (packages.Count <= ServerDescriptor.MaximumPackages) return packages;

            // A descriptor lists at most 512; players are still admitted against every package by the host's planner.
            Warn("content.descriptor-full", "Only the first " + ServerDescriptor.MaximumPackages + " of " + packages.Count + " shared packages fit the server descriptor.");
            return packages.GetRange(0, ServerDescriptor.MaximumPackages);
        }

        /// <summary>
        /// Gets the encoded offer this player presents; empty before a runtime attaches, while this game's packages cannot be
        /// read, or when they cannot fit. A joining player's check has already stopped the join in each case.
        /// </summary>
        public byte[] ClientContent() => EncodedOffer() ?? Array.Empty<byte>();

        /// <summary>
        /// Decides with the runtime's planner whether a player presenting <paramref name="clientContent"/> may join. The
        /// planner's lines become the problems the host's policy puts in the admission report, which the player sees.
        /// </summary>
        public ContentAdmission Admit(byte[] clientContent)
        {
            var current = runtime;
            if (current == null) return ContentAdmission.Refused(new[] { HostNotReady }, AdmissionCode.NotReady);

            // A host that cannot read its own packages cannot judge anyone else's.
            if (ReadLocalContent(current) == null) return ContentAdmission.Refused(new[] { HostContentUnreadable }, AdmissionCode.NotReady);
            if (!ContentOfferCodec.TryDecode(clientContent, out var offer))
            {
                return ContentAdmission.Refused(new[] { OfferUnreadable }, AdmissionCode.Protocol);
            }

            InternalContentVerdict verdict;
            try
            {
                verdict = current.EvaluateAdmission(offer!);
            }
            catch (Exception exception)
            {
                // A failing runtime must never let a player in, nor take the host's pump down with it.
                Report?.Invoke(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Error, "content.admission-failed", "The runtime's admission check failed.", null, exception));
                return ContentAdmission.Refused(new[] { HostNotReady }, AdmissionCode.NotReady);
            }

            return verdict.IsAdmitted ? ContentAdmission.Admitted : ContentAdmission.Refused(verdict.Problems);
        }

        public ContentCheck Check(ServerDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            var current = runtime;
            if (current == null) return new ContentCheck(new[] { ClientNotReady });

            // This game's own packages are read first, hashed where they have no receipt: one that cannot be read stops the
            // join here, before anything connects.
            var local = ReadLocalContent(current);
            if (local == null) return new ContentCheck(new[] { ContentUnreadable }, unreadable: true);
            var packages = new InternalHostPackage[descriptor.Packages.Count];
            for (var index = 0; index < packages.Length; index++)
            {
                var package = descriptor.Packages[index];
                packages[index] = new InternalHostPackage(package.Id, package.Version, package.Sha256, ToRole(package.Role));
            }

            InternalContentVerdict verdict;
            try
            {
                verdict = current.CheckHostContent(packages);
            }
            catch (Exception exception)
            {
                Report?.Invoke(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Error, "content.check-failed", "The runtime's content check failed.", null, exception));
                return new ContentCheck(new[] { "This game's mods could not be checked." });
            }

            if (!verdict.IsAdmitted) return new ContentCheck(verdict.Problems);
            return EncodedOffer(local) == null
                ? new ContentCheck(new[] { "Too many mods are enabled to join a session; at most " + InternalContentLimits.MaximumPackages + " fit." })
                : ContentCheck.Ready;
        }

        /// <summary>
        /// Reads the attached runtime's packages as every content path here does: null without a runtime, or when the
        /// runtime cannot read them, which is reported.
        /// </summary>
        internal IReadOnlyList<InternalContentDescriptor>? ReadLocalContent()
        {
            var current = runtime;
            return current == null ? null : ReadLocalContent(current);
        }

        /// <summary>
        /// Reads the runtime's packages, or returns null when it cannot, as when another program holds a file of a package
        /// without an install receipt while the runtime hashes it. The failure is reported and the content treated as
        /// unreadable: nothing is announced or offered and nobody is admitted, so the frame goes on, and the next read tries
        /// again.
        /// </summary>
        private IReadOnlyList<InternalContentDescriptor>? ReadLocalContent(IInternalMultiplayerRuntimeHost current)
        {
            try
            {
                return current.LocalContent;
            }
            catch (Exception exception)
            {
                Report?.Invoke(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Error, "content.read-failed", "This game's mods could not be read.", null, exception));
                return null;
            }
        }

        /// <summary>Gets the offer for this player's content, null when it cannot be read or presented.</summary>
        private byte[]? EncodedOffer()
        {
            var current = runtime;
            if (current == null) return null;
            var content = ReadLocalContent(current);
            return content == null ? null : EncodedOffer(content);
        }

        /// <summary>
        /// Gets the offer for <paramref name="content"/>, null when it cannot be presented. It is encoded once for each
        /// content list the runtime returns, and the runtime returns a new list whenever its packages change.
        /// </summary>
        private byte[]? EncodedOffer(IReadOnlyList<InternalContentDescriptor> content)
        {
            if (encodedOffer != null && ReferenceEquals(content, offeredContent)) return encodedOffer;
            offeredContent = content;
            encodedOffer = null;
            var mods = new List<InternalContentDescriptor>();
            foreach (var descriptor in content)
            {
                // Client-local mods run only here; nobody else needs to know about them.
                if (descriptor.Mode != InternalContentMode.ClientLocal) mods.Add(descriptor);
            }

            mods.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
            for (var index = 1; index < mods.Count; index++)
            {
                if (string.Equals(mods[index - 1].Id, mods[index].Id, StringComparison.Ordinal)) return null;
            }

            if (mods.Count > InternalContentLimits.MaximumPackages) return null;
            var offer = new InternalContentOffer(GameBuild, MultiplayerModule.ProtocolVersion, MultiplayerModule.PeerVersionRange, mods.AsReadOnly());
            encodedOffer = ContentOfferCodec.TryEncode(offer);
            return encodedOffer;
        }

        private static InternalHostPackageRole ToRole(DescriptorPackageRole role) =>
            role == DescriptorPackageRole.SessionRequired ? InternalHostPackageRole.SessionRequired
            : role == DescriptorPackageRole.SessionOptional ? InternalHostPackageRole.SessionOptional
            : InternalHostPackageRole.Implicit;

        private void Warn(string code, string message) =>
            Report?.Invoke(new InternalMultiplayerDiagnostic(InternalDiagnosticSeverity.Warning, code, message));
    }
}
