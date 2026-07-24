using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorToolHostRouter
    {
        private OperationResult<bool> AddToggleBindingLocked(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return OperationResult<bool>.Success(false);
            }

            InputBinding binding;
            try
            {
                binding = InputBinding.Key(key);
            }
            catch (ArgumentException exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }

            if (hostToggleBindings.TryGetValue(binding.Control, out var existing))
            {
                existing.ReferenceCount++;
                return OperationResult<bool>.Success(false);
            }

            hostToggleBindings.Add(binding.Control, new HostToggleBinding(binding));
            var bindings = BuildToggleBindingsLocked();
            if (bindings.Count > 8)
            {
                hostToggleBindings.Remove(binding.Control);
                return OperationResult<bool>.Failure(
                    ModErrorCode.RateLimited,
                    "The shared creator action reached its binding limit.");
            }

            var action = toggleAction;
            if (action == null) return OperationResult<bool>.Success(true);
            var rebound = action.Rebind(bindings);
            if (!rebound.Succeeded)
            {
                hostToggleBindings.Remove(binding.Control);
                return rebound;
            }

            return OperationResult<bool>.Success(true);
        }

        private void RemoveToggleBindingLocked(string key)
        {
            if (string.IsNullOrWhiteSpace(key)
                || !hostToggleBindings.TryGetValue(key, out var existing))
            {
                return;
            }

            existing.ReferenceCount--;
            if (existing.ReferenceCount > 0) return;
            hostToggleBindings.Remove(key);

            var action = toggleAction;
            if (action == null) return;
            var physicallyRemoved = !ContainsBinding(providerToggleBindings, existing.Binding)
                && !hostToggleBindings.ContainsKey(existing.Binding.Control);
            var rebound = action.Rebind(BuildToggleBindingsLocked());
            if (!rebound.Succeeded)
            {
                // A stale host-only key is less safe than temporarily losing the hotkey. Disposal guarantees
                // the old action cannot route that key to another host; provider teardown remains exact-once.
                logger.Warn(
                    "Creator toggle binding cleanup failed; disabling the shared action: "
                    + rebound.ErrorMessage);
                action.Dispose();
                toggleAction = null;
            }
            if (physicallyRemoved)
            {
                suppressToggleUntilRelease = true;
            }
        }

        private IReadOnlyList<InputBinding> BuildToggleBindingsLocked()
        {
            var bindings = new List<InputBinding>(
                providerToggleBindings.Count + hostToggleBindings.Count);
            foreach (var binding in providerToggleBindings)
            {
                AddUnique(bindings, binding);
            }
            foreach (var entry in hostToggleBindings.Values.OrderBy(
                value => value.Binding.Control,
                StringComparer.OrdinalIgnoreCase))
            {
                AddUnique(bindings, entry.Binding);
            }
            return bindings.AsReadOnly();
        }

        private static void AddUnique(List<InputBinding> bindings, InputBinding candidate)
        {
            if (!ContainsBinding(bindings, candidate)) bindings.Add(candidate);
        }

        private static bool ContainsBinding(
            IEnumerable<InputBinding> bindings,
            InputBinding candidate) =>
            bindings.Any(existing => existing.Kind == candidate.Kind
                && string.Equals(
                    existing.Control,
                    candidate.Control,
                    StringComparison.OrdinalIgnoreCase));

        private sealed class HostToggleBinding
        {
            public HostToggleBinding(InputBinding binding)
            {
                Binding = binding;
                ReferenceCount = 1;
            }

            public InputBinding Binding { get; }
            public int ReferenceCount { get; set; }
        }
    }
}
