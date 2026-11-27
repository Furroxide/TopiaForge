using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private static Action<UiPaintRole>? RenderNode(
            UiNode node,
            TopiaForgeContainer parent,
            UiCallbackGate callbacks,
            UiGraphRetentionTransaction graphs,
            bool inheritedDisabled = false,
            UiPaintRole backdrop = UiPaintRole.Surface)
        {
            if (node is UiText text)
            {
                var label = parent.Label(text.Text, ToNativeTextStyle(text.Style)).Tone(ToNativeTone(text.Tone));
                label.SetBackdrop(backdrop);
                return label.SetBackdrop;
            }
            if (node is UiProgress progress)
            {
                var label = RenderProgress(progress, parent);
                if (label == null) return null;
                label.SetBackdrop(backdrop);
                return label.SetBackdrop;
            }
            if (node is UiCountdown countdown)
            {
                var label = RenderCountdown(countdown, parent, () => countdown.ClockEpoch == null || countdown.ClockEpoch == callbacks.ClockEpoch()
                    ? callbacks.Clock() : countdown.EndsAt);
                label.SetBackdrop(backdrop);
                return label.SetBackdrop;
            }

            if (node is UiColumn column)
            {
                var container = parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                foreach (var child in column.Children) RenderNode(child, container, callbacks, graphs, inheritedDisabled, backdrop);
                return null;
            }

            if (node is UiRow row)
            {
                var container = parent.Row(TopiaForgeGap.Sm, TopiaForgeGap.None);
                foreach (var child in row.Children) RenderNode(child, container, callbacks, graphs, inheritedDisabled, backdrop);
                return null;
            }

            if (node is UiScroll scrollNode)
            {
                var scroll = parent.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.None)
                    .FixedHeight(scrollNode.Height);
                RenderNode(scrollNode.Content, scroll.Content, callbacks, graphs, inheritedDisabled, backdrop);
                return null;
            }

            if (node is UiSplitPane split)
            {
                if (split.Orientation == UiSplitOrientation.Horizontal)
                {
                    var splitRow = parent.Row(TopiaForgeGap.Md, TopiaForgeGap.None, expandChildWidth: true);
                    var primary = splitRow.Column(TopiaForgeGap.Sm, TopiaForgeGap.None)
                        .Flex(split.PrimaryFraction, 1f);
                    var secondary = splitRow.Column(TopiaForgeGap.Sm, TopiaForgeGap.None)
                        .Flex(1f - split.PrimaryFraction, 1f);
                    RenderNode(split.Primary, primary, callbacks, graphs, inheritedDisabled, backdrop);
                    RenderNode(split.Secondary, secondary, callbacks, graphs, inheritedDisabled, backdrop);
                }
                else
                {
                    var splitColumn = parent.Column(TopiaForgeGap.Md, TopiaForgeGap.None);
                    var primary = splitColumn.Column(TopiaForgeGap.Sm, TopiaForgeGap.None)
                        .Flex(1f, split.PrimaryFraction);
                    var secondary = splitColumn.Column(TopiaForgeGap.Sm, TopiaForgeGap.None)
                        .Flex(1f, 1f - split.PrimaryFraction);
                    RenderNode(split.Primary, primary, callbacks, graphs, inheritedDisabled, backdrop);
                    RenderNode(split.Secondary, secondary, callbacks, graphs, inheritedDisabled, backdrop);
                }

                return null;
            }

            if (node is UiGraphCanvas graph)
            {
                graphs.Render(graph, parent, callbacks, inheritedDisabled);
                return null;
            }

            if (node is UiButton button)
            {
                var native = parent.Button(
                    button.Label,
                    callbacks.Wrap(button.Activated, "button '" + button.Id + "'"),
                    ToNativeButtonStyle(button.Style));
                native.SetEnabled(button.Enabled && !inheritedDisabled);
                native.SetBackdrop(backdrop);
                Action<UiPaintRole> repaint = native.SetBackdrop;
                return repaint + TopiaForgeControlFocus.Install(native, backdrop);
            }

            if (node is UiCheckbox checkbox)
            {
                var native = parent.Checkbox(checkbox.Label, checkbox.Value,
                    callbacks.Wrap(checkbox.Changed, "checkbox '" + checkbox.Id + "'"));
                native.SetEnabled(checkbox.Enabled && !inheritedDisabled);
                native.SetBackdrop(backdrop);
                Action<UiPaintRole> repaint = native.SetBackdrop;
                return repaint + TopiaForgeControlFocus.Install(native, backdrop);
            }

            if (node is UiToggle toggle)
            {
                var native = parent.Toggle(
                    toggle.Label,
                    toggle.Value,
                    callbacks.Wrap(toggle.Changed, "toggle '" + toggle.Id + "'"));
                native.SetEnabled(toggle.Enabled && !inheritedDisabled);
                native.SetBackdrop(backdrop);
                Action<UiPaintRole> repaint = native.SetBackdrop;
                return repaint + TopiaForgeControlFocus.Install(native, backdrop);
            }

            if (node is UiSlider slider)
            {
                var native = parent.Slider(
                    slider.Label,
                    slider.Minimum,
                    slider.Maximum,
                    slider.Value,
                    callbacks.Wrap(slider.Changed, "slider '" + slider.Id + "'"));
                native.UseDeclarativePaint(slider.Enabled && !inheritedDisabled, slider.Divisions,
                    slider.UsesExplicitFormat ? slider.Format : null);
                native.SetBackdrop(backdrop);
                Action<UiPaintRole> repaint = native.SetBackdrop;
                return repaint + TopiaForgeControlFocus.Install(native, backdrop);
            }

            if (node is UiTextInput input)
            {
                var container = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.None);
                var label = container.Label(input.Label, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
                label.SetBackdrop(backdrop);
                var native = container.Input(
                    input.Placeholder,
                    input.Value,
                    callbacks.Wrap(input.Changed, "text input '" + input.Id + "'"));
                native.SetCharacterLimit(input.MaximumLength);
                native.SetEnabled(input.Enabled && !inheritedDisabled);
                Action<UiPaintRole> repaint = label.SetBackdrop;
                if (input.Error is { } error)
                {
                    native.SetValidationBackdrop(backdrop);
                    native.SetError(true);
                    repaint += native.SetValidationBackdrop;
                    repaint += RenderValidationMessage(container, error, backdrop);
                }
                return repaint + TopiaForgeControlFocus.Install(native, backdrop);
            }

            if (node is UiDropdown dropdown)
            {
                var container = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.None);
                var label = container.Label(dropdown.Label, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
                label.SetBackdrop(backdrop);
                var labels = dropdown.Choices.Select(choice => choice.Label).ToArray();
                var selectedIndex = 0;
                for (var index = 0; index < dropdown.Choices.Count; index++)
                {
                    if (string.Equals(dropdown.Choices[index].Value, dropdown.SelectedValue, StringComparison.Ordinal))
                    {
                        selectedIndex = index;
                        break;
                    }
                }

                var native = container.Dropdown(labels, selectedIndex, index =>
                {
                    if (index >= 0 && index < dropdown.Choices.Count)
                    {
                        callbacks.Invoke(
                            dropdown.Changed,
                            dropdown.Choices[index].Value,
                            "dropdown '" + dropdown.Id + "'");
                    }
                });
                native.SetEnabled(dropdown.Enabled && !inheritedDisabled);
                Action<UiPaintRole> repaint = label.SetBackdrop;
                return repaint + TopiaForgeControlFocus.Install(native, backdrop);
            }

            if (node is UiVirtualList list)
            {
                var enabled = list.Enabled && !inheritedDisabled;
                var native = parent.ListView<UiListItem>(UiVirtualListGeometry.RowHeight)
                    .FixedHeight(UiVirtualListGeometry.Height(list.VisibleRows));
                native.UseDeclarativeRows(list.VisibleRows, backdrop);
                native.Bind((row, item, _) =>
                {
                    row.Title.SetText(item.Title);
                    row.Subtitle.SetText(item.Subtitle);
                    row.Badge.Set(item.Badge, TopiaForgeTone.Neutral);
                });
                native.OnSelected(index =>
                {
                    if (enabled && index >= 0 && index < list.Items.Count)
                    {
                        callbacks.Invoke(list.Selected, list.Items[index].Id, "virtual list '" + list.Id + "'");
                    }
                });
                native.SetItems(list.Items);
                native.SetEnabled(enabled);
                if (list.SelectedItemId != null)
                {
                    for (var index = 0; index < list.Items.Count; index++)
                    {
                        if (string.Equals(list.Items[index].Id, list.SelectedItemId, StringComparison.Ordinal))
                        {
                            native.SetSelectedIndex(index);
                            break;
                        }
                    }
                }

                return native.SetRowBackdrop;
            }

            throw new NotSupportedException("Unsupported safe UI node type: " + node.GetType().FullName + ".");
        }

        private static TopiaForgeTextStyle ToNativeTextStyle(UiTextStyle style)
        {
            switch (style)
            {
                case UiTextStyle.Banner:
                    return TopiaForgeTextStyle.Banner;
                case UiTextStyle.Display:
                    return TopiaForgeTextStyle.Display;
                case UiTextStyle.Title:
                    return TopiaForgeTextStyle.Title;
                case UiTextStyle.Heading:
                    return TopiaForgeTextStyle.Heading;
                case UiTextStyle.Label:
                    return TopiaForgeTextStyle.Label;
                case UiTextStyle.Caption:
                    return TopiaForgeTextStyle.Caption;
                case UiTextStyle.Numeral:
                    return TopiaForgeTextStyle.Numeral;
                case UiTextStyle.Mono:
                    return TopiaForgeTextStyle.Mono;
                default:
                    return TopiaForgeTextStyle.Body;
            }
        }

        private static TopiaForgeButtonStyle ToNativeButtonStyle(UiButtonStyle style)
        {
            switch (style)
            {
                case UiButtonStyle.Secondary:
                    return TopiaForgeButtonStyle.Outline;
                case UiButtonStyle.Ghost:
                    return TopiaForgeButtonStyle.Ghost;
                case UiButtonStyle.Danger:
                    return TopiaForgeButtonStyle.Danger;
                default:
                    return TopiaForgeButtonStyle.Filled;
            }
        }

        private sealed class UiCallbackGate
        {
            internal Func<double> Clock { get; set; } = () => 0;
            internal Func<string> ClockEpoch { get; set; } = () => "offline";
            private readonly IModLifetime lifetime;
            private readonly IModLogger logger;
            private int active = 1;

            public UiCallbackGate(IModLifetime lifetime, IModLogger logger)
            {
                this.lifetime = lifetime;
                this.logger = logger;
            }

            public Action Wrap(Action callback, string description) => () => Invoke(callback, description);
            public Action<T> Wrap<T>(Action<T> callback, string description) => value => Invoke(callback, value, description);

            public void Invoke(Action callback, string description)
            {
                if (!CanInvoke()) return;
                foreach (var subscriber in callback.GetInvocationList())
                {
                    try { ((Action)subscriber)(); }
                    catch (Exception exception) { Report(description, exception); }
                }
            }

            public void Invoke<T>(Action<T> callback, T value, string description)
            {
                if (!CanInvoke()) return;
                foreach (var subscriber in callback.GetInvocationList())
                {
                    try { ((Action<T>)subscriber)(value); }
                    catch (Exception exception) { Report(description, exception); }
                }
            }

            public void Close() => Interlocked.Exchange(ref active, 0);

            private bool CanInvoke() => Volatile.Read(ref active) != 0 && !lifetime.IsStopping;

            private void Report(string description, Exception exception)
            {
                try { logger.Error(exception, "A mod UI " + description + " callback failed."); }
                catch { }
            }
        }
    }
}
