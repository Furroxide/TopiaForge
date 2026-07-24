using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace TopiaForge.Mods
{
    /// <summary>Stable ids for prompt slots consumed by first-party runtime bridges and cooperating providers.</summary>
    public static class WellKnownPromptIds
    {
        /// <summary>
        /// Gets the optional directive that augments robot personality prompts. The native autonomous planning
        /// bridge and cooperating providers such as RobotKit may consume this slot; they append the directive without
        /// replacing their existing personality, authoritative facts, action schema, or output contract.
        /// </summary>
        public const string GlobalRobotDirective = "topiaforge.robot.global-directive";
    }

    /// <summary>Registers owner-bound prompt replacements and reports deterministic conflicts.</summary>
    public interface IPromptOverrideRegistry
    {
        /// <summary>Gets a deterministic snapshot of active overrides visible to the caller.</summary>
        IReadOnlyList<PromptOverride> Overrides { get; }

        /// <summary>Registers an override owned by the current mod lifetime.</summary>
        OperationResult<IPromptOverrideHandle> Register(PromptOverrideRequest request);

        /// <summary>Tries to resolve the effective replacement for a prompt id.</summary>
        bool TryGetEffectiveOverride(string promptId, out PromptOverride? promptOverride);

        /// <summary>Gets prompt ids for which more than one provider registered a replacement.</summary>
        IReadOnlyList<PromptConflict> GetConflicts();
    }

    /// <summary>A disposable prompt registration lease.</summary>
    public interface IPromptOverrideHandle : IDisposable
    {
        /// <summary>Gets the registered immutable override.</summary>
        PromptOverride Override { get; }

        /// <summary>Gets whether the registration has been released.</summary>
        bool IsDisposed { get; }
    }

    /// <summary>Describes a prompt replacement without exposing or accepting an owner identity.</summary>
    public sealed class PromptOverrideRequest
    {
        /// <summary>Creates a prompt replacement request.</summary>
        public PromptOverrideRequest(
            string promptId,
            string replacementText,
            int priority = 0,
            string description = "")
        {
            PromptId = promptId ?? string.Empty;
            ReplacementText = replacementText ?? string.Empty;
            Priority = priority;
            Description = description ?? string.Empty;
        }

        /// <summary>Gets the stable game or provider prompt id.</summary>
        public string PromptId { get; }

        /// <summary>Gets the replacement prompt text.</summary>
        public string ReplacementText { get; }

        /// <summary>Gets the priority; greater values win.</summary>
        public int Priority { get; }

        /// <summary>Gets optional diagnostic context.</summary>
        public string Description { get; }
    }

    /// <summary>One active immutable prompt replacement.</summary>
    public sealed class PromptOverride
    {
        /// <summary>Creates an override. Runtime providers fill the source identity from the owner-bound facade.</summary>
        public PromptOverride(
            string sourceId,
            string promptId,
            string replacementText,
            int priority = 0,
            string description = "")
        {
            SourceId = sourceId ?? string.Empty;
            PromptId = promptId ?? string.Empty;
            ReplacementText = replacementText ?? string.Empty;
            Priority = priority;
            Description = description ?? string.Empty;
        }

        /// <summary>Gets the runtime-authenticated provider identity.</summary>
        public string SourceId { get; }

        /// <summary>Gets the prompt id.</summary>
        public string PromptId { get; }

        /// <summary>Gets the replacement text.</summary>
        public string ReplacementText { get; }

        /// <summary>Gets the deterministic priority.</summary>
        public int Priority { get; }

        /// <summary>Gets optional diagnostic context.</summary>
        public string Description { get; }
    }

    /// <summary>Describes competing replacements for one prompt.</summary>
    public sealed class PromptConflict
    {
        /// <summary>Creates conflict details.</summary>
        public PromptConflict(
            string promptId,
            IReadOnlyList<PromptOverride> overrides,
            PromptOverride? effectiveOverride)
        {
            PromptId = promptId ?? string.Empty;
            Overrides = overrides == null
                ? Array.Empty<PromptOverride>()
                : new ReadOnlyCollection<PromptOverride>(new List<PromptOverride>(overrides));
            EffectiveOverride = effectiveOverride;
        }

        /// <summary>Gets the conflicting prompt id.</summary>
        public string PromptId { get; }

        /// <summary>Gets competing overrides in winner-first order.</summary>
        public IReadOnlyList<PromptOverride> Overrides { get; }

        /// <summary>Gets the deterministic winner, if one exists.</summary>
        public PromptOverride? EffectiveOverride { get; }
    }
}
