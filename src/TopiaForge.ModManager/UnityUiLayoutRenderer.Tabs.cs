using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private readonly Dictionary<string, NativeTabGroup> tabGroups = new Dictionary<string, NativeTabGroup>(StringComparer.Ordinal);

            private void CreateTabs(UiTabs tabs, NativeNode view, UiThemeDescriptor? declared, UiPaintRole backdrop, bool disabled,
                UiCallbackGate callbacks, UiGraphRetentionTransaction graphs, UnityUiLayoutRenderer? previous, Action? pageChanged)
            {
                var selection = previous != null && previous.tabGroups.TryGetValue(tabs.Id!, out var old) ? old.Selection : default;
                var group = new NativeTabGroup(tabs, UiTabSelection.Restore(tabs, selection), callbacks, pageChanged);
                tabGroups.Add(tabs.Id!, group);
                var count = tabs.Tabs.Count;
                view.Children = new NativeNode[count * 2];
                for (var index = 0; index < count; index++)
                {
                    var page = Create(tabs.Tabs[index].Child, view.Container, declared, backdrop, disabled, callbacks, graphs,
                        previous, pageChanged);
                    group.Pages[index] = page.Container;
                    view.Children[index] = page;
                }
                for (var index = 0; index < count; index++)
                {
                    var tabIndex = index;
                    var source = tabs.Buttons[index];
                    var target = new NativeNode(view.Container.CreateChild(source.Id!));
                    var shadow = AddGraphic<TopiaForgeRoundedImage>(target.Container, "Shadow");
                    var plate = AddGraphic<TopiaForgeRoundedImage>(target.Container, "Tab background");
                    var outline = AddGraphic<TopiaForgeRoundedImage>(target.Container, "Ring");
                    var buttonHost = target.Container.CreateChild("Widget");
                    TopiaForgeAnchors.Stretch(buttonHost.Rect);
                    var button = buttonHost.Button(source.Label, () => group.Select(tabIndex), TopiaForgeButtonStyle.Ghost);
                    button.SetEnabled(!disabled && tabs.Enabled);
                    var focus = TopiaForgeControlFocus.Install(button, backdrop);
                    var leaf = new NativeLeaf(buttonHost, source, backdrop, null);
                    target.Backplate = plate;
                    target.Leaf = leaf;
                    target.TabButton = new NativeTabButton(button, focus, shadow, plate, outline,
                        AddGraphic<Image>(target.Container, "Tab selection mark"));
                    leaves.Add(source, leaf);
                    group.Buttons[index] = target.TabButton;
                    view.Children[count + index] = target;
                }
                group.Refresh();
            }

            private bool IsVisibleTabPage(UiLayoutPlacement placement)
            {
                for (var page = placement.TabPage; page != null; page = page.Parent)
                    if (tabGroups[page.Group.Node.Id!].SelectedIndex != page.PageIndex) return false;
                return true;
            }

            private sealed class NativeTabGroup
            {
                private readonly UiTabs source;
                private readonly UiCallbackGate callbacks;
                private readonly Action? pageChanged;

                internal NativeTabGroup(UiTabs source, UiTabSelection selection, UiCallbackGate callbacks, Action? pageChanged)
                {
                    this.source = source;
                    this.callbacks = callbacks;
                    this.pageChanged = pageChanged;
                    Selection = selection;
                    SelectedIndex = source.IndexOf(selection.Active);
                    Pages = new TopiaForgeContainer[source.Tabs.Count];
                    Buttons = new NativeTabButton[source.Tabs.Count];
                }

                internal TopiaForgeContainer[] Pages { get; }
                internal NativeTabButton[] Buttons { get; }
                internal UiTabSelection Selection { get; private set; }
                internal int SelectedIndex { get; private set; }

                internal void Select(int index)
                {
                    if (index == SelectedIndex) return;
                    SelectedIndex = index;
                    Selection = new UiTabSelection(source.SelectedTabId, source.Tabs[index].Id);
                    Refresh();
                    pageChanged?.Invoke();
                    if (source.Selected != null) callbacks.Invoke(source.Selected, Selection.Active, "tabs '" + source.Id + "'");
                }

                internal void Refresh()
                {
                    for (var index = 0; index < Pages.Length; index++)
                    {
                        var selected = index == SelectedIndex;
                        Pages[index].SetShown(selected);
                        Buttons[index].SetSelected(selected);
                    }
                }
            }

            private sealed class NativeTabButton
            {
                private readonly TopiaForgeButton button;
                private readonly Action<UiPaintRole>? focus;
                private readonly TopiaForgeRoundedImage shadow;
                private readonly TopiaForgeRoundedImage plate;
                private readonly TopiaForgeRoundedImage outline;
                private readonly Image mark;
                private bool selected;
                private UiPaintRole backdrop = UiPaintRole.Surface;

                internal NativeTabButton(TopiaForgeButton button, Action<UiPaintRole>? focus,
                    TopiaForgeRoundedImage shadow, TopiaForgeRoundedImage plate, TopiaForgeRoundedImage outline, Image mark)
                {
                    this.button = button;
                    this.focus = focus;
                    this.shadow = shadow;
                    this.plate = plate;
                    this.outline = outline;
                    this.mark = mark;
                    TopiaForgeAnchors.Stretch(shadow.rectTransform);
                    TopiaForgeAnchors.Stretch(plate.rectTransform);
                    TopiaForgeAnchors.Stretch(outline.rectTransform);
                    mark.rectTransform.anchorMin = new UnityEngine.Vector2(0f, 0f);
                    mark.rectTransform.anchorMax = new UnityEngine.Vector2(1f, 0f);
                    mark.rectTransform.pivot = new UnityEngine.Vector2(0.5f, 0f);
                }

                internal void SetSelected(bool next)
                {
                    selected = next;
                    Paint(backdrop);
                }

                internal void Paint(UiPaintRole surface)
                {
                    backdrop = surface;
                    var theme = button.ResolvedTheme;
                    var descriptor = theme.Descriptor ?? UiThemePackParser.Default;
                    var scheme = descriptor.SelectScheme(theme.Scheme == TopiaForgeScheme.Hud, theme.HighContrast);
                    var role = !button.Button.interactable ? UiPaintRole.Sunken : selected ? UiPaintRole.Selected : surface;
                    var component = descriptor.Components[UiThemeTarget.Tabs];
                    var rect = plate.rectTransform.rect;
                    var radius = descriptor.Shape.Resolve(component.Radius, rect.width, rect.height);
                    var stroke = (float)component.Border;
                    if (theme.HighContrast && stroke > 0f) stroke = Math.Max(3f, stroke);
                    plate.enabled = selected;
                    plate.color = ColorOf(scheme.GetPair(role).Fill);
                    plate.SetShape(radius, 0f);
                    outline.enabled = selected && stroke > 0f;
                    outline.SetShape(radius, stroke);
                    outline.color = ColorOf(scheme.ResolveBoundary(surface));
                    var shadowStyle = selected && button.Button.interactable && !theme.HighContrast
                        ? component.Shadow : UiThemeShadow.None;
                    var (x, y) = UiLayoutDecoration.ShadowOffset(shadowStyle);
                    var offset = new UnityEngine.Vector2((float)x, (float)-y);
                    shadow.enabled = shadowStyle != UiThemeShadow.None;
                    shadow.rectTransform.offsetMin = shadow.rectTransform.offsetMax = offset;
                    shadow.SetShape(radius);
                    shadow.color = theme.Shadow;
                    mark.enabled = selected;
                    mark.color = ColorOf(scheme.ResolveFocus(role));
                    mark.rectTransform.sizeDelta = new UnityEngine.Vector2(-16f, theme.HighContrast ? 3f : 2f);
                    button.SetBackdrop(role);
                    focus?.Invoke(surface);
                }
            }
        }
    }
}
