using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakeUiSurface
    {
        private string admittedEpoch = string.Empty;
        private FakeUiThemeResolution admittedTheme;
        private UiAccessibilityPreferences admittedAccessibility = UiAccessibilityPreferences.Default;

        private bool PresentationMatches(string epoch, FakeUiThemeResolution theme, UiAccessibilityPreferences preferences) =>
            admittedEpoch == epoch && ReferenceEquals(admittedTheme.Descriptor, theme.Descriptor) &&
            admittedTheme.RequestedId == theme.RequestedId &&
            admittedAccessibility.HighContrast == preferences.HighContrast &&
            admittedAccessibility.UiScale == preferences.UiScale &&
            admittedAccessibility.ReducedMotion == preferences.ReducedMotion &&
            admittedAccessibility.MotionIntensity == preferences.MotionIntensity;

        private void RememberPresentation(string epoch, FakeUiThemeResolution theme, UiAccessibilityPreferences preferences)
        {
            admittedEpoch = epoch;
            admittedTheme = theme;
            admittedAccessibility = preferences;
        }

        /// <inheritdoc/>
        public OperationResult<bool> SetContent(UiNode content)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            if (release == null)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The fake UI surface is disposed.");

            var retained = ReferenceEquals(content, Content);
            var epoch = widgetEpoch();
            var theme = owner.ResolveTheme(Request.Theme);
            var preferences = owner.Accessibility;
            if (retained && PresentationMatches(epoch, theme, preferences)) return FakeUiThemePackage.Success;

            if (nextContentErrorCode != ModErrorCode.None)
            {
                var errorCode = nextContentErrorCode;
                var message = nextContentErrorMessage;
                nextContentErrorCode = ModErrorCode.None;
                nextContentErrorMessage = string.Empty;
                return OperationResult<bool>.Failure(errorCode, message);
            }

            try
            {
                if (!retained && Request.Kind == UiSurfaceKind.Hud && UiComposition.ContainsInteractive(content))
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "HUD surfaces are presentation-only.");
                wireTarget?.Measure(owner, revision + 1, Request, Body, IsVisible, content, epoch, retained);
                if (!retained)
                {
                    FakeUiTreeCapture.VerifyComposition(owner, this, content);
                    UiClockBinding.Bind(content, epoch);
                }
            }
            catch (ArgumentException exception)
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, exception.Message);
            }
            catch (InvalidOperationException) when (WireBounded)
            {
                return OperationResult<bool>.Failure(ModErrorCode.RateLimited, "The remote UI surface exceeds its packet budget.");
            }

            RememberPresentation(epoch, theme, preferences);
            revision++;
            // Retained trees keep their input values even when a theme, accessibility setting, or clock changes.
            if (retained) return FakeUiThemePackage.Success;

            Content = content;
            toggleValues.Clear();
            sliderValues.Clear();
            textValues.Clear();
            dropdownValues.Clear();
            listViewports.Clear();
            graphSelections.Clear();
            graphViewports.Clear();
            CaptureState(content);
            RefreshGraphConnections();
            if (HudOverflowText != null) RefreshHudLayoutNotice();
            return FakeUiThemePackage.Success;
        }

        private void CaptureState(UiNode? node)
        {
            if (node == null) return;
            if (node is UiToggle toggle) toggleValues[toggle.Id!] = toggle.Value;
            else if (node is UiSlider slider) sliderValues[slider.Id!] = slider.Value;
            else if (node is UiTextInput input) textValues[input.Id!] = input.Value;
            else if (node is UiDropdown dropdown) dropdownValues[dropdown.Id!] = dropdown.SelectedValue;
            else if (node is UiVirtualList list) RememberListViewport(list, list.SelectedItemId, ListIndex(list, list.SelectedItemId));
            else if (node is UiGraphCanvas graph)
            {
                graphSelections[graph.Id!] = graph.SelectedNodeId;
                graphViewports[graph.Id!] = graph.Viewport;
            }

            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++) CaptureState(children[index]);
        }

        private static int ListIndex(UiVirtualList list, string? itemId)
        {
            if (itemId == null) return -1;
            for (var index = 0; index < list.Items.Count; index++)
                if (string.Equals(list.Items[index].Id, itemId, StringComparison.Ordinal)) return index;
            return -1;
        }

        private void RememberListViewport(UiVirtualList list, string? selection, int index)
        {
            var offset = GetListViewport(list.Id!).Offset;
            if (index >= 0)
                offset = UiVirtualListGeometry.RevealRow(index, offset, UiVirtualListGeometry.Height(list.VisibleRows), list.Items.Count);
            listViewports[list.Id!] = (selection, offset);
        }

        private bool CanInteract(string id)
        {
            if (!UiLayoutTree.TryFind(Content, id, out _, out var disabled) || disabled) return false;
            try { FakeUiTreeCapture.VerifyComposition(owner, this, Content); }
            catch (UiLayoutException) { return false; }
            return true;
        }

        private OperationResult<bool> Invoke(Action callback, string description, bool playerAction = true)
        {
            var failures = 0;
            foreach (var subscriber in callback.GetInvocationList())
            {
                try { FakeGuestOrigin.Run(WireBounded && playerAction, () => ((Action)subscriber)()); }
                catch (Exception exception)
                {
                    failures++;
                    callbackErrors.Add(description + " callback failed: " + exception.Message);
                }
            }

            return CallbackResult(failures);
        }

        private OperationResult<bool> Invoke<T>(Action<T> callback, T value, string description)
        {
            var failures = 0;
            foreach (var subscriber in callback.GetInvocationList())
            {
                try { FakeGuestOrigin.Run(WireBounded, () => ((Action<T>)subscriber)(value)); }
                catch (Exception exception)
                {
                    failures++;
                    callbackErrors.Add(description + " callback failed: " + exception.Message);
                }
            }

            return CallbackResult(failures);
        }

        private static OperationResult<bool> CallbackResult(int failures) => failures == 0
            ? OperationResult<bool>.Success(true)
            : OperationResult<bool>.Failure(ModErrorCode.External, failures + " fake UI callback subscriber(s) failed.");

        private static OperationResult<bool> NotFound(string id, string kind) =>
            OperationResult<bool>.Failure(ModErrorCode.NotFound, "No " + kind + " uses id '" + id + "'.");

        private static OperationResult<bool> Disabled(string id) =>
            OperationResult<bool>.Failure(ModErrorCode.InvalidState, "UI control '" + id + "' is disabled.");

        private static UiGraphNode? FindGraphNode(UiGraphCanvas graph, string id)
        {
            foreach (var node in graph.Nodes)
            {
                if (string.Equals(node.Id, id, StringComparison.Ordinal)) return node;
            }

            return null;
        }

        private static UiGraphPort? FindGraphPort(UiGraphNode node, string id)
        {
            foreach (var port in node.Ports)
            {
                if (string.Equals(port.Id, id, StringComparison.Ordinal)) return port;
            }

            return null;
        }

        private void InvokeDismissed()
        {
            var handlers = Dismissed;
            if (handlers == null) return;
            Invoke(handlers, "surface '" + Id + "' dismissal", playerAction: false);
        }

        private void EnsureActive()
        {
            if (release == null)
            {
                throw new ObjectDisposedException(nameof(FakeUiSurface));
            }
        }
    }
}
