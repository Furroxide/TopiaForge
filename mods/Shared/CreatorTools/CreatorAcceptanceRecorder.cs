using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    /// <summary>
    /// Records challenge-bound Creator acceptance results from real observed
    /// workbench transitions.
    /// </summary>
    /// <remarks>
    /// The recorder is inert unless the release harness provisioned a 64-hex
    /// one-run challenge into the CreatorTools config. That makes evidence
    /// impossible to produce from a run the harness did not set up, and makes
    /// ordinary player sessions emit nothing at all.
    ///
    /// Every case is gated on observations reported by the workbench as state
    /// actually changed. The recorder never inspects a script's assertion and
    /// never marks a case from intent — only from a transition that happened.
    /// </remarks>
    internal sealed class CreatorAcceptanceRecorder
    {
        private const string Prefix = "TF-CREATOR";

        private readonly IModLogger logger;
        private readonly string challenge;
        private readonly HashSet<string> emitted =
            new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<CreatorObservation> observations =
            new HashSet<CreatorObservation>();
        private int completedCycles;
        private int reportedCycles;

        private CreatorAcceptanceRecorder(IModLogger logger, string challenge)
        {
            this.logger = logger;
            this.challenge = challenge;
        }

        /// <summary>
        /// Creates a recorder, or null when no valid challenge was provisioned.
        /// </summary>
        public static CreatorAcceptanceRecorder? TryCreate(
            IModLogger? logger,
            string? challenge)
        {
            if (logger == null || !IsLowerHexChallenge(challenge)) return null;
            return new CreatorAcceptanceRecorder(logger, challenge!);
        }

        /// <summary>Gets the number of completed open/edit/end cycles.</summary>
        public int CompletedCycles => completedCycles;

        /// <summary>Records one observed workbench transition.</summary>
        public void Observe(CreatorObservation observation)
        {
            if (!observations.Add(observation)) return;
            EvaluateCases();
        }

        /// <summary>
        /// Records a completed open/spawn/edit/hide/reopen/end cycle that left
        /// no retained roster, lease, runner, or surface behind.
        /// </summary>
        public void ObserveCompletedCycle(bool clean)
        {
            if (!clean) return;
            completedCycles++;
            if (completedCycles > reportedCycles)
            {
                reportedCycles = completedCycles;
                logger.Info(
                    Prefix + "|CYCLE|" + challenge + "|"
                    + completedCycles.ToString(
                        System.Globalization.CultureInfo.InvariantCulture));
            }
            EvaluateCases();
        }

        /// <summary>Reports a case that was observed to fail.</summary>
        public void Fail(string caseId, string detail)
        {
            if (string.IsNullOrEmpty(caseId) || !emitted.Add("fail:" + caseId))
            {
                return;
            }
            logger.Error(
                Prefix + "|FAIL|" + challenge + "|" + caseId + "|"
                + Sanitize(string.IsNullOrEmpty(detail)
                    ? "observed failure"
                    : detail));
        }

        private void EvaluateCases()
        {
            foreach (var definition in CreatorAcceptanceCases.All)
            {
                if (emitted.Contains(definition.Id)) continue;
                if (!definition.IsSatisfied(observations, completedCycles))
                {
                    continue;
                }
                emitted.Add(definition.Id);
                logger.Info(
                    Prefix + "|PASS|" + challenge + "|" + definition.Id + "|"
                    + Sanitize(definition.Describe(completedCycles)));
            }
        }

        /// <summary>Keeps marker fields single-line and delimiter-safe.</summary>
        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var buffer = new char[Math.Min(value.Length, 160)];
            var length = 0;
            foreach (var character in value)
            {
                if (length == buffer.Length) break;
                buffer[length++] =
                    character == '|' || character == '\r' || character == '\n'
                        ? ' '
                        : character;
            }
            return new string(buffer, 0, length);
        }

        private static bool IsLowerHexChallenge(string? value)
        {
            if (value == null || value.Length != 64) return false;
            foreach (var character in value)
            {
                var isHex = (character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f');
                if (!isHex) return false;
            }
            return true;
        }
    }
}
