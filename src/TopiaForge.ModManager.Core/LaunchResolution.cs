using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Why a launch cannot proceed. A closed set, shared with the Dart launcher.</summary>
    /// <remarks>
    /// Closed on purpose. An open-ended string reason is one a caller cannot switch on, cannot
    /// translate, and cannot present as anything better than the sentence a developer happened to
    /// write. Every one of these is something a player or an author can act on.
    /// </remarks>
    public enum LaunchBlockCode
    {
        /// <summary>No launch target with that id exists in the effective profile.</summary>
        /// <remarks>Also where a selection saved before a target was renamed lands.</remarks>
        TargetNotDeclared = 1,

        /// <summary>The declaring package is installed but not enabled in this profile.</summary>
        TargetPackageDisabled = 2,

        /// <summary>A pinned version sits outside a range on the reference path.</summary>
        TargetPackageVersionUnsatisfied = 3,

        /// <summary>The target names a gamemode no package in the profile declares.</summary>
        GamemodeNotDeclared = 4,

        /// <summary>The gamemode's package is installed but not enabled.</summary>
        GamemodePackageDisabled = 5,

        /// <summary>A cross-package gamemode reference no required dependency owns.</summary>
        GamemodeRefNotADependency = 6,

        /// <summary>Declared and loaded, but the implementation did not bind.</summary>
        /// <remarks>Runtime observation only. Static resolution cannot see binding and does not guess.</remarks>
        GamemodeUnbound = 7,

        /// <summary>The resolved world id names no declaration.</summary>
        WorldNotDeclared = 8,

        /// <summary>The world's package is installed but not enabled.</summary>
        WorldPackageDisabled = 9,

        /// <summary>A cross-package world reference no required dependency owns.</summary>
        WorldRefNotADependency = 10,

        /// <summary>A fixed or list policy, and the requested world is outside the declared set.</summary>
        WorldNotAdmittedByPolicy = 11,

        /// <summary>An open policy, and the world consents to neither this gamemode nor any.</summary>
        WorldConsentMissing = 12,

        /// <summary>A policy names a discovered family, or an instance under one.</summary>
        /// <remarks>
        /// Members of a discovered family exist only once the game has run and reported them, so a
        /// policy naming one is naming content that may never appear on this installation.
        /// </remarks>
        WorldNotStaticallyDeclared = 13,

        /// <summary>The world and the gamemode share no transition.</summary>
        TransitionUnsatisfiable = 14,

        /// <summary>An explicit transition outside the intersection, or a player override the target does not offer.</summary>
        TransitionNotOffered = 15,

        /// <summary>The gamemode requires an authored spawn marker; the world provides a default.</summary>
        SpawnRequirementUnsatisfied = 16,

        /// <summary>The world's package does not support this platform, architecture, content target, or game version.</summary>
        WorldPlatformUnsupported = 17,

        /// <summary>Two resolved packages both claim to own an id, and the longer-prefix owner does not declare it.</summary>
        DeclarationIdAmbiguous = 18,

        /// <summary>A target is otherwise valid but every world it admits is unavailable.</summary>
        /// <remarks>
        /// Surfaced with a reason rather than omitted. A bound gamemode that silently vanishes from a
        /// menu is indistinguishable from one that was never installed.
        /// </remarks>
        NoAvailableTarget = 19,

        /// <summary>Revalidation before preparing found a loaded package set the plan was not built from.</summary>
        PlanPackageSetMismatch = 20
    }

    /// <summary>One reason a launch is blocked, and what it is about.</summary>
    public sealed class LaunchBlock : IComparable<LaunchBlock>
    {
        /// <summary>Creates a blocking reason.</summary>
        /// <param name="code">Why the launch is blocked.</param>
        /// <param name="subject">The id the reason is about.</param>
        /// <param name="subjectVersion">The pinned version, where the reason is about one.</param>
        public LaunchBlock(LaunchBlockCode code, string subject, string subjectVersion = "")
        {
            Code = code;
            Subject = subject ?? string.Empty;
            SubjectVersion = subjectVersion ?? string.Empty;
        }

        /// <summary>Gets the reason.</summary>
        public LaunchBlockCode Code { get; }

        /// <summary>Gets the id the reason is about.</summary>
        public string Subject { get; }

        /// <summary>Gets the pinned version, or empty.</summary>
        public string SubjectVersion { get; }

        /// <summary>
        /// Orders reasons by code, then subject, then version.
        /// </summary>
        /// <remarks>
        /// Mandatory, not cosmetic. The resolver reports every applicable reason rather than the first,
        /// so without a total order the same profile produces the same reasons in a different sequence
        /// and every resolution fixture is flaky.
        /// </remarks>
        public int CompareTo(LaunchBlock? other)
        {
            if (other == null)
            {
                return 1;
            }

            var byCode = ((int)Code).CompareTo((int)other.Code);
            if (byCode != 0)
            {
                return byCode;
            }

            var bySubject = string.CompareOrdinal(Subject, other.Subject);
            return bySubject != 0
                ? bySubject
                : string.CompareOrdinal(SubjectVersion, other.SubjectVersion);
        }

        /// <inheritdoc />
        public override string ToString() =>
            SubjectVersion.Length == 0
                ? Code + "(" + Subject + ")"
                : Code + "(" + Subject + "@" + SubjectVersion + ")";
    }

    /// <summary>One enabled package at its pinned version, with the manifest that was read from it.</summary>
    public sealed class ResolvedPackage
    {
        /// <summary>Creates a resolved package.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="manifest"/> is null.</exception>
        public ResolvedPackage(string id, string version, ModManifest manifest)
        {
            Id = id ?? string.Empty;
            Version = version ?? string.Empty;
            Manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        }

        /// <summary>Gets the package id.</summary>
        public string Id { get; }

        /// <summary>Gets the exact pinned version.</summary>
        public string Version { get; }

        /// <summary>Gets the manifest read from that package.</summary>
        public ModManifest Manifest { get; }
    }

    /// <summary>What this installation is, for the package-level compatibility rule.</summary>
    /// <remarks>
    /// Passed in rather than read, so resolution stays pure data. Reading the install from disk here
    /// is what would make GM-02 untestable again: the whole point is that a resolution can be
    /// reproduced from its inputs.
    /// </remarks>
    public sealed class InstallFacts
    {
        /// <summary>Creates install facts. Any empty value means "do not check that dimension".</summary>
        public InstallFacts(
            string platform = "",
            string architecture = "",
            string contentTarget = "",
            string gameVersion = "")
        {
            Platform = platform ?? string.Empty;
            Architecture = architecture ?? string.Empty;
            ContentTarget = contentTarget ?? string.Empty;
            GameVersion = gameVersion ?? string.Empty;
        }

        /// <summary>Gets the platform, or empty to skip the check.</summary>
        public string Platform { get; }

        /// <summary>Gets the architecture, or empty to skip the check.</summary>
        public string Architecture { get; }

        /// <summary>Gets the content target, or empty to skip the check.</summary>
        public string ContentTarget { get; }

        /// <summary>Gets the installed game version, or empty to skip the check.</summary>
        public string GameVersion { get; }
    }

    /// <summary>The enabled package set at pinned versions, and nothing else.</summary>
    public sealed class EffectiveProfile
    {
        /// <summary>Creates an effective profile.</summary>
        public EffectiveProfile(
            string profileId,
            int revision,
            IReadOnlyList<ResolvedPackage> packages,
            InstallFacts? install = null,
            IReadOnlyList<ResolvedPackage>? disabledPackages = null)
        {
            ProfileId = profileId ?? string.Empty;
            Revision = revision;
            Packages = packages ?? Array.Empty<ResolvedPackage>();
            Install = install ?? new InstallFacts();
            DisabledPackages = disabledPackages ?? Array.Empty<ResolvedPackage>();
        }

        /// <summary>Gets the profile this set belongs to.</summary>
        public string ProfileId { get; }

        /// <summary>Gets the profile revision the set was taken at.</summary>
        public int Revision { get; }

        /// <summary>Gets the enabled packages at their pinned versions.</summary>
        public IReadOnlyList<ResolvedPackage> Packages { get; }

        /// <summary>Gets what this installation is.</summary>
        public InstallFacts Install { get; }

        /// <summary>Gets packages that are installed but not enabled in this profile.</summary>
        /// <remarks>
        /// Carried so the resolver can tell "there is no such thing" from "it is right there, switched
        /// off". Those are the same dead end to a player who is only told the target does not exist,
        /// and the second one is a single click away from working.
        /// </remarks>
        public IReadOnlyList<ResolvedPackage> DisabledPackages { get; }
    }

    /// <summary>What the game reported after running. It can only ever narrow a plan.</summary>
    /// <remarks>
    /// An observation may mark a declared target unavailable and may name the members of a discovered
    /// family. It may never add a launch target, and never re-enable a package the profile disabled:
    /// letting a previous run's report resurrect content is how a launcher ends up offering something
    /// the current profile does not contain.
    /// </remarks>
    public sealed class RuntimeObservation
    {
        /// <summary>Creates a runtime observation.</summary>
        public RuntimeObservation(
            IReadOnlyList<string>? unavailableWorldIds = null,
            IReadOnlyList<string>? discoveredWorldIds = null,
            IReadOnlyList<string>? unboundGamemodeIds = null)
        {
            UnavailableWorldIds = unavailableWorldIds ?? Array.Empty<string>();
            DiscoveredWorldIds = discoveredWorldIds ?? Array.Empty<string>();
            UnboundGamemodeIds = unboundGamemodeIds ?? Array.Empty<string>();
        }

        /// <summary>An observation that reports nothing.</summary>
        public static readonly RuntimeObservation None = new RuntimeObservation();

        /// <summary>Gets worlds the game could not make available this run.</summary>
        public IReadOnlyList<string> UnavailableWorldIds { get; }

        /// <summary>Gets observed members of discovered families, as full instance ids.</summary>
        public IReadOnlyList<string> DiscoveredWorldIds { get; }

        /// <summary>Gets gamemodes whose declared implementation did not bind.</summary>
        public IReadOnlyList<string> UnboundGamemodeIds { get; }
    }

    /// <summary>A request to launch one target.</summary>
    public sealed class LaunchRequest
    {
        /// <summary>Creates a launch request.</summary>
        /// <param name="targetId">The launch target the player picked.</param>
        /// <param name="worldOverride">A world the player chose, when the target admits a choice.</param>
        /// <param name="transitionOverride">A transition the player chose, when the target offers one.</param>
        public LaunchRequest(
            string targetId,
            string worldOverride = "",
            string transitionOverride = "")
        {
            TargetId = targetId ?? string.Empty;
            WorldOverride = worldOverride ?? string.Empty;
            TransitionOverride = transitionOverride ?? string.Empty;
        }

        /// <summary>Gets the requested launch target.</summary>
        public string TargetId { get; }

        /// <summary>Gets the requested world, or empty for the target's default.</summary>
        public string WorldOverride { get; }

        /// <summary>Gets the requested transition, or empty for the target's own choice.</summary>
        public string TransitionOverride { get; }
    }

    /// <summary>One launch, fully decided.</summary>
    public sealed class LaunchPlan
    {
        /// <summary>Creates a launch plan.</summary>
        public LaunchPlan(
            string launchTargetId,
            string gamemodeId,
            string worldId,
            string transition,
            IReadOnlyList<ResolvedPackage> resolvedPackages,
            string worldInstanceId = "")
        {
            LaunchTargetId = launchTargetId;
            GamemodeId = gamemodeId;
            WorldId = worldId;
            Transition = transition;
            WorldInstanceId = worldInstanceId ?? string.Empty;
            ResolvedPackages = resolvedPackages;
            Digest = PackageSetDigest.Of(resolvedPackages);
        }

        /// <summary>Gets the target that was launched.</summary>
        public string LaunchTargetId { get; }

        /// <summary>Gets the gamemode that will run.</summary>
        public string GamemodeId { get; }

        /// <summary>Gets the world it will run in.</summary>
        public string WorldId { get; }

        /// <summary>Gets the observed instance under a discovered family, or empty.</summary>
        public string WorldInstanceId { get; }

        /// <summary>Gets the chosen transition.</summary>
        public string Transition { get; }

        /// <summary>Gets the packages this plan was resolved against, ordered by id.</summary>
        public IReadOnlyList<ResolvedPackage> ResolvedPackages { get; }

        /// <summary>Gets the digest of <see cref="ResolvedPackages"/>.</summary>
        /// <remarks>
        /// Revalidated against the actually-loaded set before any scene work begins. That check is the
        /// whole reason the plan carries its package set: a preflight that agreed with a set nobody
        /// compared afterwards is a promise, not an invariant.
        /// </remarks>
        public string Digest { get; }
    }

    /// <summary>A plan, or the reasons there is none.</summary>
    public sealed class LaunchResolution
    {
        private LaunchResolution(LaunchPlan? plan, IReadOnlyList<LaunchBlock> blocks)
        {
            Plan = plan;
            Blocks = blocks;
        }

        /// <summary>Gets the plan, or null when the launch is blocked.</summary>
        public LaunchPlan? Plan { get; }

        /// <summary>Gets every applicable reason, ordered. Empty when a plan was produced.</summary>
        public IReadOnlyList<LaunchBlock> Blocks { get; }

        /// <summary>Gets whether a plan was produced.</summary>
        public bool Resolved => Plan != null;

        /// <summary>Creates a resolved result.</summary>
        public static LaunchResolution Success(LaunchPlan plan) =>
            new LaunchResolution(plan, Array.Empty<LaunchBlock>());

        /// <summary>Creates a blocked result, ordering the reasons.</summary>
        public static LaunchResolution Blocked(List<LaunchBlock> blocks)
        {
            blocks.Sort();
            return new LaunchResolution(null, blocks);
        }
    }

    /// <summary>Reduces a package set to one comparable value.</summary>
    /// <remarks>
    /// FNV-1a over the canonical <c>id@version</c> form, written out here and mirrored exactly in Dart
    /// so both sides agree without either taking a dependency for it.
    /// <para>
    /// Not a security boundary and not an integrity check. It answers one question -- is the loaded set
    /// the set this plan was built from -- about two views of the same local state. Today nothing asks
    /// that question at all, so a cheap exact-equality signal is the whole improvement.
    /// </para>
    /// </remarks>
    public static class PackageSetDigest
    {
        private const ulong OffsetBasis = 14695981039346656037UL;
        private const ulong Prime = 1099511628211UL;

        /// <summary>Computes the digest of a package set, in whatever order it arrives.</summary>
        public static string Of(IReadOnlyList<ResolvedPackage> packages)
        {
            var canonical = new List<string>(packages.Count);
            foreach (var package in packages)
            {
                canonical.Add(package.Id + "@" + package.Version);
            }

            canonical.Sort(StringComparer.Ordinal);
            return Of(canonical);
        }

        /// <summary>Computes the digest of already-canonical <c>id@version</c> entries.</summary>
        public static string Of(IReadOnlyList<string> canonicalEntries)
        {
            var hash = OffsetBasis;
            for (var index = 0; index < canonicalEntries.Count; index++)
            {
                if (index > 0)
                {
                    hash = Mix(hash, (byte)'\n');
                }

                foreach (var character in canonicalEntries[index])
                {
                    // UTF-16 code units, low byte first, so both languages hash the same bytes for
                    // any id -- including one outside the Basic Latin range.
                    hash = Mix(hash, (byte)(character & 0xFF));
                    hash = Mix(hash, (byte)((character >> 8) & 0xFF));
                }
            }

            return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
        }

        private static ulong Mix(ulong hash, byte value) => (hash ^ value) * Prime;
    }
}
