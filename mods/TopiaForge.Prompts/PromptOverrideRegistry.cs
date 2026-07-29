using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Prompts
{
    internal sealed class PromptOverrideRegistry : IPromptOverrideRegistry, IOwnerBoundExtensionFactory, IDisposable
    {
        private readonly string sourceId;
        private readonly object gate = new object();
        private readonly List<Entry> entries = new List<Entry>();
        private bool disposed;

        public PromptOverrideRegistry(string sourceId = "test.provider")
        {
            this.sourceId = sourceId ?? string.Empty;
        }

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
                        .ThenBy(o => o.SourceId, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
            }
        }

        public OperationResult<IPromptOverrideHandle> Register(PromptOverrideRequest request)
        {
            return Register(sourceId, request);
        }

        private OperationResult<IPromptOverrideHandle> Register(string ownerId, PromptOverrideRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.PromptId))
            {
                return OperationResult<IPromptOverrideHandle>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Prompt id is required.");
            }

            if (string.IsNullOrWhiteSpace(request.ReplacementText))
            {
                return OperationResult<IPromptOverrideHandle>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Replacement text is required.");
            }

            var promptOverride = new PromptOverride(
                ownerId,
                request.PromptId,
                request.ReplacementText,
                request.Priority,
                request.Description);
            var token = Guid.NewGuid();
            var handle = new PromptOverrideHandle(this, token, promptOverride);
            var entry = new Entry(token, promptOverride, handle);

            lock (gate)
            {
                if (disposed)
                {
                    return OperationResult<IPromptOverrideHandle>.Failure(
                        ModErrorCode.InvalidState,
                        "Prompt override registry is disposed.");
                }

                RemoveMatchingLocked(e =>
                    string.Equals(e.Override.SourceId, promptOverride.SourceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.Override.PromptId, promptOverride.PromptId, StringComparison.OrdinalIgnoreCase));
                entries.Add(entry);
            }

            return OperationResult<IPromptOverrideHandle>.Success(handle);
        }

        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IPromptOverrideRegistry))
            {
                throw new ArgumentException("Unsupported prompt extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, ownerModId, lifetime);
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
                    .Where(g => g.Select(o => o.SourceId).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var overrides = OrderedOverrides(g).ToList();
                        return new PromptConflict(g.Key, overrides, overrides.FirstOrDefault());
                    })
                    .ToList();
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                RemoveMatchingLocked(_ => true);
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
                .ThenBy(o => o.SourceId, StringComparer.OrdinalIgnoreCase)
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

        private sealed class OwnerFacade : IPromptOverrideRegistry
        {
            private readonly PromptOverrideRegistry registry;
            private readonly string ownerModId;
            private readonly IModLifetime lifetime;

            public OwnerFacade(PromptOverrideRegistry registry, string ownerModId, IModLifetime lifetime)
            {
                this.registry = registry;
                this.ownerModId = ownerModId;
                this.lifetime = lifetime;
            }

            public IReadOnlyList<PromptOverride> Overrides => registry.Overrides;

            public OperationResult<IPromptOverrideHandle> Register(PromptOverrideRequest request)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<IPromptOverrideHandle>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot register prompt overrides.");
                }

                var result = registry.Register(ownerModId, request);
                if (!result.TryGetValue(out var handle))
                {
                    return result;
                }

                try
                {
                    var ownedHandle = new OwnerHandle(handle, lifetime.Track(handle));
                    return OperationResult<IPromptOverrideHandle>.Success(ownedHandle);
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IPromptOverrideHandle>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before its prompt override could be registered.");
                }
            }

            public bool TryGetEffectiveOverride(string promptId, out PromptOverride? promptOverride) =>
                registry.TryGetEffectiveOverride(promptId, out promptOverride);

            public IReadOnlyList<PromptConflict> GetConflicts() => registry.GetConflicts();

            private sealed class OwnerHandle : IPromptOverrideHandle
            {
                private readonly IPromptOverrideHandle handle;
                private IDisposable? lifetimeLease;

                public OwnerHandle(IPromptOverrideHandle handle, IDisposable lifetimeLease)
                {
                    this.handle = handle;
                    this.lifetimeLease = lifetimeLease;
                }

                public PromptOverride Override => handle.Override;
                public bool IsDisposed => lifetimeLease == null || handle.IsDisposed;

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }
    }
}
