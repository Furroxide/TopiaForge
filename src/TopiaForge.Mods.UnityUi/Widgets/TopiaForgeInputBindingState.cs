using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Mods.UnityUi
{
    internal enum TopiaForgeInputBindingFieldState
    {
        Idle,
        Listening,
        Conflict,
        Reserved,
        Unavailable,
        Failed,
    }

    // Reads the viewer's action on refresh and commits only to the action that started listening.
    internal sealed class TopiaForgeInputBindingState
    {
        internal const string ListeningText = "Listening: press a key, Esc cancels";
        internal const string UnavailableText = "Not available here";
        private const string Unbound = "Unbound";

        private readonly UiKeyBindingField node;
        private readonly Func<(IInputAction Action, string DisplayName)?> resolveLocal;
        private readonly Func<IInputAction, InputBinding, string?> findConflict;
        private readonly string defaultHint;
        private IInputAction? action;
        private InputBinding? binding;
        private bool enabled;

        internal TopiaForgeInputBindingState(UiKeyBindingField node,
            Func<(IInputAction Action, string DisplayName)?> resolveLocal,
            Func<IInputAction, InputBinding, string?> findConflict, bool enabled)
        {
            this.node = node;
            this.resolveLocal = resolveLocal;
            this.findConflict = findConflict;
            this.enabled = enabled;
            defaultHint = ResolveHint(node.WireDefaults, node.Device);
            Refresh();
        }

        internal string DisplayLabel { get; private set; } = string.Empty;
        internal string BindingText { get; private set; } = Unbound;
        internal string StatusText { get; private set; } = string.Empty;
        internal TopiaForgeInputBindingFieldState State { get; private set; }
        internal bool CanEdit => enabled && node.Enabled && action != null;

        internal void Refresh()
        {
            var local = resolveLocal();
            var nextAction = local?.Action;
            DisplayLabel = node.Label ?? local?.DisplayName ?? node.ActionName;
            if (nextAction == null)
            {
                action = null;
                binding = null;
                BindingText = defaultHint;
                Show(TopiaForgeInputBindingFieldState.Unavailable, UnavailableText);
                return;
            }

            var nextBinding = FirstBinding(nextAction.Bindings, node.Device);
            if (ReferenceEquals(action, nextAction) && Nullable.Equals(binding, nextBinding)) return;

            var sameAction = ReferenceEquals(action, nextAction);
            action = nextAction;
            binding = nextBinding;
            BindingText = binding is { } selected ? UiRebinding.KeyLabel(selected.Kind, selected.Control) : Unbound;
            // A reset can change what we display while listening. A replacement registration never inherits a capture.
            if (!sameAction || State != TopiaForgeInputBindingFieldState.Listening)
                Show(TopiaForgeInputBindingFieldState.Idle);
        }

        internal void SetEnabled(bool value)
        {
            enabled = value;
            if (!CanEdit) Cancel();
        }

        internal bool Begin()
        {
            Refresh();
            if (!CanEdit || State == TopiaForgeInputBindingFieldState.Listening) return false;
            Show(TopiaForgeInputBindingFieldState.Listening, ListeningText);
            return true;
        }

        internal void Cancel()
        {
            if (State == TopiaForgeInputBindingFieldState.Listening) Show(TopiaForgeInputBindingFieldState.Idle);
        }

        internal void CancelOnBack()
        {
            if (State == TopiaForgeInputBindingFieldState.Listening)
                Show(TopiaForgeInputBindingFieldState.Reserved, "Reserved by Back");
        }

        internal bool Accept(InputBinding next)
        {
            Refresh();
            if (action == null || State != TopiaForgeInputBindingFieldState.Listening) return false;
            if (UiRebinding.ReservedBy(next) == "Back")
            {
                CancelOnBack();
                return true;
            }

            if (string.IsNullOrEmpty(next.Control) || UiRebinding.Device(next.Kind) != node.Device) return false;
            if (UiRebinding.Rejection(node.Device, next) is { } rejection)
            {
                Show(TopiaForgeInputBindingFieldState.Reserved, rejection);
                return true;
            }

            var edited = action;
            var result = edited.Rebind(UiRebinding.ReplaceDevice(edited.Bindings, node.Device, next));
            Refresh();
            if (!ReferenceEquals(action, edited)) return true;
            if (!result.Succeeded || !result.Value)
            {
                Fail("Could not rebind this action.");
                return true;
            }

            if (findConflict(edited, next) is { Length: > 0 } conflict)
                Show(TopiaForgeInputBindingFieldState.Conflict, "Also bound to " + conflict);
            else
                Show(TopiaForgeInputBindingFieldState.Idle);
            return true;
        }

        internal void Fail(string message) => Show(TopiaForgeInputBindingFieldState.Failed, message);

        private void Show(TopiaForgeInputBindingFieldState state, string text = "")
        {
            State = state;
            StatusText = text;
        }

        private static InputBinding? FirstBinding(IReadOnlyList<InputBinding> bindings, UiInputDevice device)
        {
            for (var index = 0; index < bindings.Count; index++)
            {
                var current = bindings[index];
                if (UiRebinding.Device(current.Kind) == device) return current;
            }

            return null;
        }

        private static string ResolveHint(IReadOnlyList<UiInputBindingHint> hints, UiInputDevice device)
        {
            for (var index = 0; index < hints.Count; index++)
            {
                var current = hints[index];
                if (UiRebinding.Device(current.Kind) == device) return UiRebinding.KeyLabel(current.Kind, current.Control);
            }

            return Unbound;
        }
    }
}
