using System;
using System.Collections.Generic;
using System.Linq;

namespace TopiaForge.ModManager.Core
{
    /// <summary>An authoritative host-compatibility decision reused by validation, loading, and diagnostics.</summary>
    public sealed class ManifestRuntimeCompatibility
    {
        public const string NotEvaluatedStatus = "not-evaluated";
        public const string PortableStatus = "portable";
        public const string MatchedStatus = "matched";
        public const string RejectedStatus = "rejected";

        private ManifestRuntimeCompatibility(string status, IReadOnlyList<string> errors)
        {
            Status = status;
            Errors = errors;
        }

        public string Status { get; }
        public IReadOnlyList<string> Errors { get; }
        public bool IsCompatible => !string.Equals(Status, RejectedStatus, StringComparison.Ordinal);
        public bool WasEvaluated => !string.Equals(Status, NotEvaluatedStatus, StringComparison.Ordinal);

        /// <summary>Evaluates a manifest against one normalized validation/runtime context.</summary>
        public static ManifestRuntimeCompatibility Evaluate(
            ModManifest manifest,
            ManifestValidationContext context)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!context.EnforceRuntimeCompatibility)
            {
                return new ManifestRuntimeCompatibility(NotEvaluatedStatus, Array.Empty<string>());
            }

            var platforms = manifest.Platforms ?? new List<string>();
            var architectures = manifest.Architectures ?? new List<string>();
            var contentTargets = manifest.ContentTargets ?? new List<string>();
            var portable = platforms.Count == 0 && architectures.Count == 0 && contentTargets.Count == 0;
            if (portable)
            {
                return new ManifestRuntimeCompatibility(PortableStatus, Array.Empty<string>());
            }

            var errors = new List<string>();
            MatchSingleConstraint(platforms, "platforms", "platform", context.Platform, errors);
            MatchSingleConstraint(architectures, "architectures", "architecture", context.Architecture, errors);
            MatchContentTargets(contentTargets, context.ContentTargets, errors);
            return new ManifestRuntimeCompatibility(
                errors.Count == 0 ? MatchedStatus : RejectedStatus,
                errors.ToArray());
        }

        private static void MatchSingleConstraint(
            IReadOnlyCollection<string> constraints,
            string fieldName,
            string hostName,
            string actual,
            ICollection<string> errors)
        {
            if (constraints.Count == 0)
            {
                return;
            }

            if (string.IsNullOrEmpty(actual))
            {
                errors.Add(fieldName + " cannot be checked because the host " + hostName + " is unknown.");
                return;
            }

            if (!constraints.Contains(actual, StringComparer.Ordinal))
            {
                errors.Add(fieldName + " does not include host " + hostName + " " + actual + ".");
            }
        }

        private static void MatchContentTargets(
            IReadOnlyCollection<string> constraints,
            IReadOnlyList<string> supported,
            ICollection<string> errors)
        {
            if (constraints.Count == 0)
            {
                return;
            }

            if (supported.Count == 0)
            {
                errors.Add("contentTargets cannot be checked because the host content targets are unknown.");
                return;
            }

            if (!constraints.Any(target => supported.Contains(target, StringComparer.Ordinal)))
            {
                errors.Add(
                    "contentTargets does not include a host-supported target (" +
                    string.Join(", ", supported) + ").");
            }
        }
    }
}
