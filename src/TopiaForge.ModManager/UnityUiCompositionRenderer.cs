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
        private static void RenderNode(UiNode node, TopiaForgeContainer parent, UiCallbackGate callbacks)
        {
            if (node is UiText text)
            {
                parent.Label(text.Text, ToNativeTextStyle(text.Style)).Tone(ToNativeTone(text.Tone));
                return;
            }

            if (node is UiColumn column)
            {
                var container = parent.Column(TopiaForgeGap.Sm, TopiaForgeGap.None);
                foreach (var child in column.Children) RenderNode(child, container, callbacks);
                return;
            }

            if (node is UiRow row)
            {
                var container = parent.Row(TopiaForgeGap.Sm, TopiaForgeGap.None);
                foreach (var child in row.Children) RenderNode(child, container, callbacks);
                return;
            }

            if (node is UiScroll scrollNode)
            {
                var scroll = parent.Scroll(TopiaForgeGap.Sm, TopiaForgeGap.None)
                    .FixedHeight(scrollNode.Height);
                RenderNode(scrollNode.Content, scroll.Content, callbacks);
                return;
            }

            if (node is UiButton button)
            {
                var native = parent.Button(
                    button.Label,
                    callbacks.Wrap(button.Activated, "button '" + button.Id + "'"),
                    ToNativeButtonStyle(button.Style));
                native.SetEnabled(button.Enabled);
                return;
            }

            if (node is UiToggle toggle)
            {
                var native = parent.Toggle(
                    toggle.Label,
                    toggle.Value,
                    callbacks.Wrap(toggle.Changed, "toggle '" + toggle.Id + "'"));
                native.SetEnabled(toggle.Enabled);
                return;
            }

            if (node is UiSlider slider)
            {
                var native = parent.Slider(
                    slider.Label,
                    slider.Minimum,
                    slider.Maximum,
                    slider.Value,
                    callbacks.Wrap(slider.Changed, "slider '" + slider.Id + "'"));
                native.SetEnabled(slider.Enabled);
                return;
            }

            if (node is UiTextInput input)
            {
                var container = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.None);
                container.Label(input.Label, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
                var native = container.Input(
                    input.Placeholder,
                    input.Value,
                    callbacks.Wrap(input.Changed, "text input '" + input.Id + "'"));
                native.SetCharacterLimit(input.MaximumLength);
                native.SetEnabled(input.Enabled);
                return;
            }

            if (node is UiDropdown dropdown)
            {
                var container = parent.Column(TopiaForgeGap.Xs, TopiaForgeGap.None);
                container.Label(dropdown.Label, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
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
                native.SetEnabled(dropdown.Enabled);
                return;
            }

            if (node is UiVirtualList list)
            {
                var native = parent.ListView<UiListItem>()
                    .FixedHeight(list.VisibleRows * (TopiaForgeTokens.ListRowHeight + 4f));
                native.Bind((row, item, _) =>
                {
                    row.Title.SetText(item.Title);
                    row.Subtitle.SetText(item.Subtitle);
                    row.Badge.Set(item.Badge, TopiaForgeTone.Neutral);
                });
                native.OnSelected(index =>
                {
                    if (list.Enabled && index >= 0 && index < list.Items.Count)
                    {
                        callbacks.Invoke(list.Selected, list.Items[index].Id, "virtual list '" + list.Id + "'");
                    }
                });
                native.SetItems(list.Items);
                native.SetEnabled(list.Enabled);
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

                return;
            }

            throw new NotSupportedException("Unsupported safe UI node type: " + node.GetType().FullName + ".");
        }

        private static TopiaForgeTextStyle ToNativeTextStyle(UiTextStyle style)
        {
            switch (style)
            {
                case UiTextStyle.Heading:
                    return TopiaForgeTextStyle.Heading;
                case UiTextStyle.Caption:
                    return TopiaForgeTextStyle.Caption;
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
