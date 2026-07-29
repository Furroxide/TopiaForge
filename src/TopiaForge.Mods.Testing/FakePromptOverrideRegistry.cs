using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic, owner-bound prompt override registry for module tests.</summary>
    public sealed class FakePromptOverrideRegistry : IPromptOverrideRegistry
    {
        private readonly string ownerId;
        private readonly IModLifetime lifetime;
        private readonly List<Registration> registrations = new List<Registration>();

        /// <summary>Creates a prompt registry bound to the supplied mod context.</summary>
        /// <param name="context">The fake or custom context whose identity and lifetime own registrations.</param>
        public FakePromptOverrideRegistry(IModContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            ownerId = context.Identity.Id;
            lifetime = context.Lifetime;
        }

        /// <summary>Gets the number of registrations that remain active.</summary>
        public int ActiveRegistrationCount => registrations.Count;

        /// <inheritdoc />
        public IReadOnlyList<PromptOverride> Overrides
        {
            get
            {
                var values = new List<PromptOverride>(registrations.Count);
                foreach (var registration in registrations)
                {
                    values.Add(registration.Override);
                }

                values.Sort(Compare);
                return values.AsReadOnly();
            }
        }

        /// <inheritdoc />
        public OperationResult<IPromptOverrideHandle> Register(PromptOverrideRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.PromptId) || string.IsNullOrWhiteSpace(request.ReplacementText))
            {
                return OperationResult<IPromptOverrideHandle>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A prompt id and replacement text are required.");
            }

            var value = new PromptOverride(
                ownerId,
                request.PromptId,
                request.ReplacementText,
                request.Priority,
                request.Description);
            var registration = new Registration(value, released => registrations.Remove(released));
            registrations.Add(registration);
            try
            {
                lifetime.Track(registration);
                return OperationResult<IPromptOverrideHandle>.Success(registration);
            }
            catch (ObjectDisposedException)
            {
                registration.Dispose();
                return OperationResult<IPromptOverrideHandle>.Failure(
                    ModErrorCode.Cancelled,
                    "The fake mod stopped before its prompt override could be registered.");
            }
        }

        /// <inheritdoc />
        public bool TryGetEffectiveOverride(string promptId, out PromptOverride? promptOverride)
        {
            promptOverride = null;
            if (string.IsNullOrWhiteSpace(promptId))
            {
                return false;
            }

            foreach (var candidate in Overrides)
            {
                if (string.Equals(candidate.PromptId, promptId, StringComparison.OrdinalIgnoreCase))
                {
                    promptOverride = candidate;
                    return true;
                }
            }

            return false;
        }

        /// <inheritdoc />
        public IReadOnlyList<PromptConflict> GetConflicts()
        {
            var grouped = new Dictionary<string, List<PromptOverride>>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in Overrides)
            {
                if (!grouped.TryGetValue(value.PromptId, out var group))
                {
                    group = new List<PromptOverride>();
                    grouped.Add(value.PromptId, group);
                }

                group.Add(value);
            }

            var conflicts = new List<PromptConflict>();
            foreach (var pair in grouped)
            {
                if (pair.Value.Count > 1)
                {
                    conflicts.Add(new PromptConflict(pair.Key, pair.Value.AsReadOnly(), pair.Value[0]));
                }
            }

            conflicts.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.PromptId, right.PromptId));
            return conflicts.AsReadOnly();
        }

        private static int Compare(PromptOverride left, PromptOverride right)
        {
            var prompt = StringComparer.OrdinalIgnoreCase.Compare(left.PromptId, right.PromptId);
            if (prompt != 0)
            {
                return prompt;
            }

            var priority = right.Priority.CompareTo(left.Priority);
            return priority != 0
                ? priority
                : StringComparer.Ordinal.Compare(left.SourceId, right.SourceId);
        }

        private sealed class Registration : IPromptOverrideHandle
        {
            private Action<Registration>? release;

            public Registration(PromptOverride value, Action<Registration> release)
            {
                Override = value;
                this.release = release;
            }

            public PromptOverride Override { get; }
            public bool IsDisposed => release == null;

            public void Dispose()
            {
                var callback = release;
                release = null;
                callback?.Invoke(this);
            }
        }
    }
}
