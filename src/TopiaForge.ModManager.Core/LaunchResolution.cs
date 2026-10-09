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

        /// <summary>The longest owning namespace has duplicate selected package identities or duplicate declarations.</summary>
        DeclarationIdAmbiguous = 18,

        /// <summary>A target is otherwise valid but every world it admits is unavailable.</summary>
        /// <remarks>
        /// Surfaced with a reason rather than omitted. A bound gamemode that silently vanishes from a
        /// menu is indistinguishable from one that was never installed.
        /// </remarks>
        NoAvailableTarget = 19,

        /// <summary>Revalidation before preparing found a loaded package set the plan was not built from.</summary>
        PlanPackageSetMismatch = 20,
        /// <summary>The selected world provider has no successful current binding.</summary>
        WorldUnbound = 21,
        /// <summary>A matching producer observation reports unavailable world content.</summary>
        WorldUnavailable = 22,
        /// <summary>An explicit consent reference lacks a required dependency on its owner.</summary>
        WorldConsentRefNotADependency = 23,
        /// <summary>The same exact package selection now resolves to a different declaration tuple.</summary>
        PlanResolutionMismatch = 24,
        /// <summary>The target package does not support the current installation.</summary>
        TargetPlatformUnsupported = 25,
        /// <summary>The gamemode package does not support the current installation.</summary>
        GamemodePlatformUnsupported = 26
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
            if (!Enum.IsDefined(typeof(LaunchBlockCode), code)) throw new ArgumentOutOfRangeException(nameof(code));
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

            var byCode = string.CompareOrdinal(Code.ToString(), other.Code.ToString());
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

    /// <summary>An authoritative plan or all independently determinable blocking reasons.</summary>
    public sealed class LaunchResolution
    {
        private LaunchResolution(LaunchPlan? plan, IReadOnlyList<LaunchBlock> blocks)
        {
            Plan = plan;
            Blocks = blocks;
        }

        public LaunchPlan? Plan { get; }
        public IReadOnlyList<LaunchBlock> Blocks { get; }
        public bool Resolved => Plan != null;
        internal static LaunchResolution Success(LaunchPlan plan) => new LaunchResolution(plan, Array.Empty<LaunchBlock>());
        public static LaunchResolution Blocked(IEnumerable<LaunchBlock> blocks) =>
            new LaunchResolution(null, LaunchBlockCollection.Copy(blocks));
    }
}
