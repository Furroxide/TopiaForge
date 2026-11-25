using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiSurface
    {
        private readonly Dictionary<string, bool> checkboxValues = new Dictionary<string, bool>(StringComparer.Ordinal);
        private Dictionary<string, UiTabSelection> tabSelections = new Dictionary<string, UiTabSelection>(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Message, bool Conflict, IReadOnlyList<InputBinding> Bindings)> bindingWarnings =
            new Dictionary<string, (string, bool, IReadOnlyList<InputBinding>)>(StringComparer.Ordinal);
        private string? hoveredTooltip;
        private double tooltipHoverStart;
        private string? listeningBinding;
        private string? openDropdown;

        /// <summary>Changes an enabled checkbox and invokes its callback with the checked value.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public OperationResult<bool> ChangeCheckbox(string id, bool value)
        {
            if (!TryFindNode(id, out var node) || node is not UiCheckbox checkbox) return NotFound(id, "checkbox");
            if (!CanInteract(id)) return Disabled(id);
            checkboxValues[id] = value;
            return Invoke(checkbox.Changed, value, "checkbox '" + id + "'");
        }

        /// <summary>Gets a checkbox's current checked value by its stable control ID.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public bool TryGetCheckboxValue(string id, out bool value) => checkboxValues.TryGetValue(id, out value);

        /// <summary>Changes a visible tab group immediately and invokes the authored selection callback.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public OperationResult<bool> SelectTab(string id, string tabId)
        {
            if (!TryFindNode(id, out var node) || node is not UiTabs tabs) return NotFound(id, "tabs");
            if (!tabs.Enabled || !CanInteract(id)) return Disabled(id);
            if (tabs.IndexOf(tabId) < 0)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "The selected tab is not in this group.");
            if (tabSelections[id].Active == tabId) return FakeUiThemePackage.Success;

            tabSelections[id] = new UiTabSelection(tabs.SelectedTabId, tabId);
            return tabs.Selected == null ? FakeUiThemePackage.Success : Invoke(tabs.Selected, tabId, "tabs '" + id + "'");
        }

        /// <summary>Gets a group's current selection, including changes made by a viewer.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public bool TryGetSelectedTab(string id, out string? tabId)
        {
            var found = tabSelections.TryGetValue(id, out var selection);
            tabId = found ? selection.Active : null;
            return found;
        }

        internal int ActiveTabIndex(UiTabs tabs)
        {
            tabSelections.TryGetValue(tabs.Id!, out var previous);
            return tabs.IndexOf(UiTabSelection.Restore(tabs, previous).Active);
        }

        /// <summary>Applies a captured control to the named action on the viewer's input device.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public OperationResult<bool> CaptureBinding(string id, InputBinding binding)
        {
            if (!TryFindNode(id, out var node) || node is not UiKeyBindingField field) return NotFound(id, "key binding field");
            if (!CanInteract(id)) return Disabled(id);
            var action = owner.FindInputAction(field.ActionName, Receiver);
            if (action == null) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Not available here");
            if (listeningBinding == id) listeningBinding = null;
            if (UiRebinding.Rejection(field.Device, binding) is { } refusal)
            {
                bindingWarnings[id] = (refusal, false, action.Bindings);
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, refusal);
            }

            var replaced = action.Rebind(UiRebinding.ReplaceDevice(action.Bindings, field.Device, binding));
            if (!replaced.Succeeded) return replaced;
            if (owner.ConflictingAction(action.Name, binding) is { } conflict)
                bindingWarnings[id] = ("Also bound to " + conflict, true, action.Bindings);
            else bindingWarnings.Remove(id);
            return replaced;
        }

        internal (string? Message, bool Conflict) BindingWarning(UiKeyBindingField field)
        {
            if (bindingWarnings.TryGetValue(field.Id!, out var warning) &&
                ReferenceEquals(warning.Bindings, owner.FindInputAction(field.ActionName, Receiver)?.Bindings))
                return (warning.Message, warning.Conflict);
            return default;
        }

        internal void HoverTooltip(string? id)
        {
            if (hoveredTooltip == id) return;
            hoveredTooltip = id;
            tooltipHoverStart = widgetClock();
        }

        internal bool TooltipHoverElapsed(string? id) =>
            id != null && id == hoveredTooltip && widgetClock() - tooltipHoverStart >= 0.45d;

        internal void SetBindingListening(string? id) => listeningBinding = id;
        internal bool IsBindingListening(string? id) => id != null && id == listeningBinding;
        internal void SetDropdownOpen(string? id) => openDropdown = id;
        internal bool DropdownIsOpen(string? id) => id != null && id == openDropdown;
        internal bool HasOpenDropdown => openDropdown != null;
    }
}
