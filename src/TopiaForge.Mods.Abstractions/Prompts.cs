using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    public interface IPromptOverrideRegistry
    {
        IReadOnlyList<PromptOverride> Overrides { get; }
        IPromptOverrideHandle Register(PromptOverrideRequest request);
        bool TryGetEffectiveOverride(string promptId, out PromptOverride? promptOverride);
        IReadOnlyList<PromptConflict> GetConflicts();
        void UnregisterOwner(string ownerModId);
    }

    public interface IPromptOverrideHandle : IDisposable
    {
        PromptOverride Override { get; }
        bool IsDisposed { get; }
    }

    public sealed class PromptOverrideRequest
    {
        public PromptOverrideRequest(string ownerModId, string promptId, string replacementText, int priority = 0, string description = "")
        {
            OwnerModId = ownerModId ?? string.Empty;
            PromptId = promptId ?? string.Empty;
            ReplacementText = replacementText ?? string.Empty;
            Priority = priority;
            Description = description ?? string.Empty;
        }

        public string OwnerModId { get; }
        public string PromptId { get; }
        public string ReplacementText { get; }
        public int Priority { get; }
        public string Description { get; }
    }

    public sealed class PromptOverride
    {
        public PromptOverride(string modId, string promptId, string replacementText, int priority = 0, string description = "")
        {
            ModId = modId ?? string.Empty;
            PromptId = promptId ?? string.Empty;
            ReplacementText = replacementText ?? string.Empty;
            Priority = priority;
            Description = description ?? string.Empty;
        }

        public string ModId { get; }
        public string PromptId { get; }
        public string ReplacementText { get; }
        public int Priority { get; }
        public string Description { get; }
    }

    public sealed class PromptConflict
    {
        public PromptConflict(string promptId, IReadOnlyList<PromptOverride> overrides, PromptOverride? effectiveOverride)
        {
            PromptId = promptId ?? string.Empty;
            Overrides = overrides ?? Array.Empty<PromptOverride>();
            EffectiveOverride = effectiveOverride;
        }

        public string PromptId { get; }
        public IReadOnlyList<PromptOverride> Overrides { get; }
        public PromptOverride? EffectiveOverride { get; }
    }
}
