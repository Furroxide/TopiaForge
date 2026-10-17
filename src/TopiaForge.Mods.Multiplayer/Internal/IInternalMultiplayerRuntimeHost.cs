using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// What the multiplayer provider needs from TopiaForge's runtime: who this player is, which packages run, the
    /// admission decision over them, and a few process controls. ModManager implements it and hands it to the provider
    /// through <see cref="IInternalMultiplayerControl.AttachRuntime"/>. Main thread only.
    /// </summary>
    internal interface IInternalMultiplayerRuntimeHost
    {
        /// <summary>Gets the Robotopia build this process runs: 1 to 16 letters, digits, dots, dashes or underscores.</summary>
        string GameBuild { get; }

        /// <summary>Gets the TopiaForge runtime version, a semantic version.</summary>
        string RuntimeVersion { get; }

        /// <summary>Gets the name this player hosts and joins as: 1 to 24 printable characters.</summary>
        string DisplayName { get; }

        /// <summary>
        /// Gets the packages this process runs, as multiplayer admission sees them, sorted by id. The same list comes back
        /// until the packages change, so a caller may keep what it derives from the list for as long as it gets that list.
        /// Installed packages are identified by their install receipts, one small file each, at the first read. Packages
        /// without one are identified by hashing their files off the main thread (<see cref="ContentReadiness"/>), never on
        /// the calling thread: until that finishes, a read throws <see cref="InternalContentNotReadyException"/>. When a
        /// package could not be read, the read after that work throws an <see cref="System.IO.IOException"/> that names it;
        /// the provider then treats this player's content as unreadable, so it neither hosts nor joins with it, and the next
        /// read starts identifying again.
        /// </summary>
        IReadOnlyList<InternalContentDescriptor> LocalContent { get; }

        /// <summary>
        /// Gets how far the runtime is in identifying this game's packages for multiplayer. Reading it starts that work when
        /// nothing has started it yet: the first read after the packages change reads their receipts, and the hashing runs
        /// off the main thread. It never hashes or throws, and a settled read allocates nothing, so a frame may read it.
        /// <see cref="InternalContentState.Unreadable"/> stays until <see cref="LocalContent"/> is read again.
        /// </summary>
        InternalContentReadiness ContentReadiness { get; }

        /// <summary>
        /// Decides, on a listen host, whether a player presenting <paramref name="offer"/> may join. The runtime runs its
        /// admission planner with this process as the server; a refusal lists the problems for the player.
        /// </summary>
        InternalContentVerdict EvaluateAdmission(InternalContentOffer offer);

        /// <summary>
        /// Checks, on a joining player, whether the host's packages are installed exactly and nothing enabled here keeps
        /// this player out. It runs before anything connects.
        /// </summary>
        InternalContentVerdict CheckHostContent(IReadOnlyList<InternalHostPackage> hostPackages);

        /// <summary>Keeps the game running while its window is in the background: on while online, off in single player.</summary>
        void SetRunInBackground(bool enabled);

        /// <summary>
        /// Takes a provider diagnostic. The runtime redacts anything shaped like an address before it reaches a log, the
        /// diagnostics bundle or a crash report, so this is safe for any text the provider holds.
        /// </summary>
        void ReportDiagnostic(InternalMultiplayerDiagnostic diagnostic);
    }

    /// <summary>Whether this game's packages are identified for multiplayer.</summary>
    internal enum InternalContentState
    {
        /// <summary>Identifying, off the main thread; hosting and joining wait for it.</summary>
        Pending = 0,

        /// <summary>Every package has its hash; <see cref="IInternalMultiplayerRuntimeHost.LocalContent"/> answers.</summary>
        Ready = 1,

        /// <summary>A package could not be read, as when another program holds one of its files open.</summary>
        Unreadable = 2
    }

    /// <summary>
    /// How far the runtime is in identifying this game's packages: its state and the progress of the work, in packages and
    /// in bytes of files hashed. The counts only grow while one identification runs.
    /// </summary>
    internal readonly struct InternalContentReadiness
    {
        internal InternalContentReadiness(InternalContentState state, int packagesDone, int packagesTotal, long bytesDone, long bytesTotal)
        {
            // A range check rather than Enum.IsDefined, which boxes: readiness is read every frame and must not allocate.
            if (state < InternalContentState.Pending || state > InternalContentState.Unreadable) throw new ArgumentOutOfRangeException(nameof(state));
            if (packagesTotal < 0 || packagesDone < 0 || packagesDone > packagesTotal) throw new ArgumentOutOfRangeException(nameof(packagesDone));
            if (bytesTotal < 0 || bytesDone < 0) throw new ArgumentOutOfRangeException(nameof(bytesDone));
            State = state;
            PackagesDone = packagesDone;
            PackagesTotal = packagesTotal;
            BytesDone = bytesDone > bytesTotal ? bytesTotal : bytesDone;
            BytesTotal = bytesTotal;
        }

        /// <summary>Gets readiness with nothing left to identify.</summary>
        internal static InternalContentReadiness Ready => new InternalContentReadiness(InternalContentState.Ready, 0, 0, 0, 0);

        internal InternalContentState State { get; }

        /// <summary>Gets how many packages this identification has finished, read from a receipt or hashed.</summary>
        internal int PackagesDone { get; }

        /// <summary>Gets how many packages this identification covers.</summary>
        internal int PackagesTotal { get; }

        /// <summary>Gets how many bytes of files this identification has hashed.</summary>
        internal long BytesDone { get; }

        /// <summary>Gets how many bytes of files it hashes in all, once it has listed them; zero before that.</summary>
        internal long BytesTotal { get; }
    }

    /// <summary>
    /// Thrown by <see cref="IInternalMultiplayerRuntimeHost.LocalContent"/> while the runtime is still identifying this game's
    /// packages off the main thread. Nothing failed: wait for <see cref="IInternalMultiplayerRuntimeHost.ContentReadiness"/>.
    /// </summary>
    internal sealed class InternalContentNotReadyException : InvalidOperationException
    {
        internal const string Text = "This game is still identifying its mods for multiplayer.";

        internal InternalContentNotReadyException()
            : base(Text)
        {
        }
    }

    /// <summary>How a package takes part in sessions, from its manifest's <c>multiplayer</c> block.</summary>
    internal enum InternalContentMode
    {
        /// <summary>No multiplayer block: every player needs the identical package.</summary>
        ImplicitShared = 0,

        /// <summary>A declared session mod, negotiated by presence, protocol and synchronized files.</summary>
        Session = 1,

        /// <summary>Runs only for the player who has it; never required of others.</summary>
        ClientLocal = 2,

        /// <summary>Runs only where the session's server runs.</summary>
        ServerOnly = 3,

        /// <summary>Refuses every multiplayer session.</summary>
        Standalone = 4
    }

    /// <summary>Whether a session mod must be on every peer.</summary>
    internal enum InternalContentPresence
    {
        /// <summary>Not a session mod.</summary>
        None = 0,

        Required = 1,

        Optional = 2
    }

    /// <summary>One synchronized file of a session mod and its packed SHA-256.</summary>
    internal sealed class InternalSynchronizedFile
    {
        internal InternalSynchronizedFile(string path, string sha256)
        {
            if (string.IsNullOrEmpty(path) || path.Length > InternalContentLimits.MaximumPathLength) throw new ArgumentException("A synchronized file path is 1 to 260 characters.", nameof(path));
            if (!InternalText.IsSha256(sha256)) throw new ArgumentException("A file hash is 64 lowercase hexadecimal characters.", nameof(sha256));
            Path = path;
            Sha256 = sha256;
        }

        internal string Path { get; }

        internal string Sha256 { get; }
    }

    /// <summary>Bounds on content descriptors, shared by the runtime and the provider's join content.</summary>
    internal static class InternalContentLimits
    {
        internal const int MaximumPackages = 512;
        internal const int MaximumIdLength = 64;
        internal const int MaximumVersionLength = 64;
        internal const int MaximumRangeLength = 128;
        internal const int MaximumPathLength = 260;
        internal const int MaximumSynchronizedFiles = 256;
    }

    /// <summary>One package as multiplayer admission sees it: its identity, exact archive and manifest semantics.</summary>
    internal sealed class InternalContentDescriptor
    {
        internal InternalContentDescriptor(
            string id,
            string version,
            string packageSha256,
            InternalContentMode mode,
            InternalContentPresence presence = InternalContentPresence.None,
            string? protocolVersion = null,
            string? peerVersionRange = null,
            IReadOnlyList<InternalSynchronizedFile>? synchronizedFiles = null,
            string? displayName = null)
        {
            if (string.IsNullOrEmpty(id) || id.Length > InternalContentLimits.MaximumIdLength) throw new ArgumentException("A package id is 1 to 64 characters.", nameof(id));
            if (string.IsNullOrEmpty(version) || version.Length > InternalContentLimits.MaximumVersionLength) throw new ArgumentException("A package version is 1 to 64 characters.", nameof(version));
            if (!InternalText.IsSha256(packageSha256)) throw new ArgumentException("A package hash is 64 lowercase hexadecimal characters.", nameof(packageSha256));
            if (!Enum.IsDefined(typeof(InternalContentMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            if (!Enum.IsDefined(typeof(InternalContentPresence), presence)) throw new ArgumentOutOfRangeException(nameof(presence));
            var files = synchronizedFiles ?? Array.Empty<InternalSynchronizedFile>();
            if (mode == InternalContentMode.Session)
            {
                if (presence == InternalContentPresence.None) throw new ArgumentException("A session mod is required or optional.", nameof(presence));
                if (string.IsNullOrEmpty(protocolVersion) || protocolVersion!.Length > InternalContentLimits.MaximumVersionLength)
                {
                    throw new ArgumentException("A session mod declares a protocol version.", nameof(protocolVersion));
                }

                if (peerVersionRange != null && (peerVersionRange.Length == 0 || peerVersionRange.Length > InternalContentLimits.MaximumRangeLength))
                {
                    throw new ArgumentException("A peer range is 1 to 128 characters.", nameof(peerVersionRange));
                }

                if (files.Count > InternalContentLimits.MaximumSynchronizedFiles) throw new ArgumentException("A session mod synchronizes at most 256 files.", nameof(synchronizedFiles));
            }
            else if (presence != InternalContentPresence.None || protocolVersion != null || peerVersionRange != null || files.Count != 0)
            {
                throw new ArgumentException("Only a session mod declares presence, a protocol or synchronized files.", nameof(mode));
            }

            var copy = new InternalSynchronizedFile[files.Count];
            for (var index = 0; index < copy.Length; index++) copy[index] = files[index] ?? throw new ArgumentException("A synchronized file is missing.", nameof(synchronizedFiles));
            Id = id;
            Version = version;
            PackageSha256 = packageSha256;
            Mode = mode;
            Presence = presence;
            ProtocolVersion = protocolVersion;
            PeerVersionRange = peerVersionRange;
            SynchronizedFiles = Array.AsReadOnly(copy);
            DisplayName = InternalText.IsDisplayText(displayName, 64) ? displayName! : id;
        }

        internal string Id { get; }

        /// <summary>Gets the name players see in problems; the id when the manifest gives none.</summary>
        internal string DisplayName { get; }

        internal string Version { get; }

        /// <summary>Gets the SHA-256 of the package archive this install came from, or of its files when none is known.</summary>
        internal string PackageSha256 { get; }

        internal InternalContentMode Mode { get; }

        internal InternalContentPresence Presence { get; }

        internal string? ProtocolVersion { get; }

        /// <summary>Gets the declared peer range, or null when the mod accepts exactly its own protocol version.</summary>
        internal string? PeerVersionRange { get; }

        internal IReadOnlyList<InternalSynchronizedFile> SynchronizedFiles { get; }
    }

    /// <summary>What a joining player presents for admission: its game build, protocol and content.</summary>
    internal sealed class InternalContentOffer
    {
        internal InternalContentOffer(string gameBuild, string protocolVersion, string peerVersionRange, IReadOnlyList<InternalContentDescriptor> mods)
        {
            GameBuild = gameBuild ?? throw new ArgumentNullException(nameof(gameBuild));
            ProtocolVersion = protocolVersion ?? throw new ArgumentNullException(nameof(protocolVersion));
            PeerVersionRange = peerVersionRange ?? throw new ArgumentNullException(nameof(peerVersionRange));
            if (mods == null) throw new ArgumentNullException(nameof(mods));
            if (mods.Count > InternalContentLimits.MaximumPackages) throw new ArgumentException("An offer lists at most 512 packages.", nameof(mods));
            Mods = mods;
        }

        internal string GameBuild { get; }

        /// <summary>Gets the joining player's TopiaForge multiplayer protocol version.</summary>
        internal string ProtocolVersion { get; }

        /// <summary>Gets the protocol range the joining player accepts.</summary>
        internal string PeerVersionRange { get; }

        /// <summary>Gets the packages the player runs, sorted by id. Client-local packages are left out: they are nobody else's business.</summary>
        internal IReadOnlyList<InternalContentDescriptor> Mods { get; }
    }

    /// <summary>What a joining player must do about one package the host runs.</summary>
    internal enum InternalHostPackageRole
    {
        /// <summary>A required session mod: the exact package must be installed.</summary>
        SessionRequired = 0,

        /// <summary>An optional session mod: used when installed at the exact package.</summary>
        SessionOptional = 1,

        /// <summary>A mod with no multiplayer block: required at the exact package.</summary>
        Implicit = 2
    }

    /// <summary>One package a host lists in its descriptor.</summary>
    internal sealed class InternalHostPackage
    {
        internal InternalHostPackage(string id, string version, string sha256, InternalHostPackageRole role)
        {
            if (string.IsNullOrEmpty(id) || id.Length > InternalContentLimits.MaximumIdLength) throw new ArgumentException("A package id is 1 to 64 characters.", nameof(id));
            if (string.IsNullOrEmpty(version) || version.Length > InternalContentLimits.MaximumVersionLength) throw new ArgumentException("A package version is 1 to 64 characters.", nameof(version));
            if (!InternalText.IsSha256(sha256)) throw new ArgumentException("A package hash is 64 lowercase hexadecimal characters.", nameof(sha256));
            if (!Enum.IsDefined(typeof(InternalHostPackageRole), role)) throw new ArgumentOutOfRangeException(nameof(role));
            Id = id;
            Version = version;
            Sha256 = sha256;
            Role = role;
        }

        internal string Id { get; }

        internal string Version { get; }

        internal string Sha256 { get; }

        internal InternalHostPackageRole Role { get; }
    }

    /// <summary>A content decision: admitted, or the problems a player can act on.</summary>
    internal sealed class InternalContentVerdict
    {
        internal static readonly InternalContentVerdict Admitted = new InternalContentVerdict(Array.Empty<string>());

        private InternalContentVerdict(IReadOnlyList<string> problems)
        {
            Problems = problems;
        }

        internal bool IsAdmitted => Problems.Count == 0;

        /// <summary>Gets one display line per problem, such as "Zombies 0.2.0 is missing.". Never an address.</summary>
        internal IReadOnlyList<string> Problems { get; }

        /// <summary>Creates a refusal. A refusal names at least one problem.</summary>
        internal static InternalContentVerdict Refused(IReadOnlyList<string> problems)
        {
            if (problems == null) throw new ArgumentNullException(nameof(problems));
            if (problems.Count == 0) throw new ArgumentException("A refusal names at least one problem.", nameof(problems));
            var copy = new string[problems.Count];
            for (var index = 0; index < copy.Length; index++) copy[index] = problems[index] ?? throw new ArgumentException("A problem is missing.", nameof(problems));
            return new InternalContentVerdict(Array.AsReadOnly(copy));
        }
    }

    internal enum InternalDiagnosticSeverity
    {
        Info = 0,
        Warning = 1,
        Error = 2
    }

    /// <summary>
    /// One provider diagnostic for the runtime's log and diagnostics bundle. It names participants, codes and reasons;
    /// the runtime still redacts its text before writing it anywhere.
    /// </summary>
    internal sealed class InternalMultiplayerDiagnostic
    {
        internal InternalMultiplayerDiagnostic(InternalDiagnosticSeverity severity, string code, string message, ParticipantId? participant = null, Exception? exception = null)
        {
            if (!Enum.IsDefined(typeof(InternalDiagnosticSeverity), severity)) throw new ArgumentOutOfRangeException(nameof(severity));
            if (string.IsNullOrEmpty(code)) throw new ArgumentException("A diagnostic code is required.", nameof(code));
            Severity = severity;
            Code = code;
            Message = message ?? string.Empty;
            Participant = participant;
            Exception = exception;
        }

        internal InternalDiagnosticSeverity Severity { get; }

        /// <summary>Gets a stable dotted code such as <c>engine.protocol-violation</c> or <c>live.host-failed</c>.</summary>
        internal string Code { get; }

        internal string Message { get; }

        internal ParticipantId? Participant { get; }

        /// <summary>Gets the failure behind the diagnostic, when one threw. Its text is redacted like the message.</summary>
        internal Exception? Exception { get; }
    }
}
