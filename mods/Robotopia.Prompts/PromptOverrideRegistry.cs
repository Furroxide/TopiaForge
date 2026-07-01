using System;
using System.Collections.Generic;
using System.Linq;
using Robotopia.Mods;

namespace Robotopia.Prompts
{
    internal sealed class PromptOverrideRegistry : IPromptOverrideRegistry
    {
        private readonly object gate = new object();
        private readonly List<Entry> entries = new List<Entry>();

        public IReadOnlyList<PromptOverride> Overrides
        {
            get
            {
                lock (gate)
                {
                    return entries
                        .Select(e => e.Override)
                        .OrderBy(o => o.PromptId, StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(o => o.Priority)
                        .ThenBy(o => o.ModId, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
        }

        public IPromptOverrideHandle Register(PromptOverrideRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.OwnerModId))
            {
                throw new ArgumentException("Owner mod id is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.PromptId))
            {
                throw new ArgumentException("Prompt id is required.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.ReplacementText))
            {
                throw new ArgumentException("Replacement text is required.", nameof(request));
            }

            var promptOverride = new PromptOverride(
                request.OwnerModId,
                request.PromptId,
                request.ReplacementText,
                request.Priority,
                request.Description);
            var token = Guid.NewGuid();
            var handle = new PromptOverrideHandle(this, token, promptOverride);
            var entry = new Entry(token, promptOverride, handle);

            lock (gate)
            {
                RemoveMatchingLocked(e =>
                    string.Equals(e.Override.ModId, promptOverride.ModId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Override.PromptId, promptOverride.PromptId, StringComparison.OrdinalIgnoreCase));
                entries.Add(entry);
            }

            return handle;
        }

        public bool TryGetEffectiveOverride(string promptId, out PromptOverride? promptOverride)
        {
            lock (gate)
            {
                promptOverride = EffectiveOverrideLocked(promptId);
                return promptOverride != null;
            }
        }

        public IReadOnlyList<PromptConflict> GetConflicts()
        {
            lock (gate)
            {
                return entries
                    .Select(e => e.Override)
                    .GroupBy(o => o.PromptId, StringComparer.OrdinalIgnoreCase)
                    .Where(g => g.Select(o => o.ModId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var overrides = OrderedOverrides(g).ToList();
                        return new PromptConflict(g.Key, overrides, overrides.FirstOrDefault());
                    })
                    .ToList();
            }
        }

        public void UnregisterOwner(string ownerModId)
        {
            if (string.IsNullOrWhiteSpace(ownerModId))
            {
                return;
            }

            lock (gate)
            {
                RemoveMatchingLocked(e => string.Equals(e.Override.ModId, ownerModId, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void Unregister(Guid token)
        {
            lock (gate)
            {
                RemoveMatchingLocked(e => e.Token == token);
            }
        }

        private PromptOverride? EffectiveOverrideLocked(string promptId)
        {
            if (string.IsNullOrWhiteSpace(promptId))
            {
                return null;
            }

            return OrderedOverrides(entries
                    .Select(e => e.Override)
                    .Where(o => string.Equals(o.PromptId, promptId, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault();
        }

        private static IEnumerable<PromptOverride> OrderedOverrides(IEnumerable<PromptOverride> promptOverrides)
        {
            return promptOverrides
                .OrderByDescending(o => o.Priority)
                .ThenBy(o => o.ModId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.ReplacementText, StringComparer.Ordinal);
        }

        private void RemoveMatchingLocked(Func<Entry, bool> predicate)
        {
            foreach (var entry in entries.Where(predicate).ToList())
            {
                entry.Handle.MarkDisposed();
                entries.Remove(entry);
            }
        }

        private sealed class Entry
        {
            public Entry(Guid token, PromptOverride promptOverride, PromptOverrideHandle handle)
            {
                Token = token;
                Override = promptOverride;
                Handle = handle;
            }

            public Guid Token { get; }
            public PromptOverride Override { get; }
            public PromptOverrideHandle Handle { get; }
        }

        private sealed class PromptOverrideHandle : IPromptOverrideHandle
        {
            private readonly PromptOverrideRegistry registry;
            private readonly Guid token;

            public PromptOverrideHandle(PromptOverrideRegistry registry, Guid token, PromptOverride promptOverride)
            {
                this.registry = registry;
                this.token = token;
                Override = promptOverride;
            }

            public PromptOverride Override { get; }
            public bool IsDisposed { get; private set; }

            public void Dispose()
            {
                if (IsDisposed)
                {
                    return;
                }

                registry.Unregister(token);
                IsDisposed = true;
            }

            public void MarkDisposed()
            {
                IsDisposed = true;
            }
        }
    }
}
