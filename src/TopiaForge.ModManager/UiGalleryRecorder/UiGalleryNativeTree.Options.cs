using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private bool CaptureDropdownPopup(UiDropdown fixture, TMP_Dropdown target, string path, UiThemeColor? backdrop)
        {
            var popup = TopiaForgeDropdown.VisiblePopup(target);
            if (popup == null) return true;
            var ownCanvas = popup.GetComponent<Canvas>();
            var surface = popup.GetComponent<Graphic>();
            if (ownCanvas == null || ownCanvas.overrideSorting || popup.parent != canvas.transform ||
                surface == null || !surface.isActiveAndEnabled || dropdownChoicesPath != null) return false;
            var options = popup.GetComponentsInChildren<Toggle>();
            if (options.Length != fixture.Choices.Count) return false;
            var choicesPath = path + "/choices";
            dropdownChoicesPath = choicesPath + "/";
            var popupFill = Over(surface, backdrop);
            Add(choicesPath, null, "DropdownPopup", popup, EmptyContent, new UiCaptureNodePaint(null, popupFill));
            for (var index = 0; index < options.Length; index++)
            {
                var option = options[index];
                var label = fixture.Choices[index].Label;
                var optionPath = ChildPath(choicesPath, index);
                var flags = Flags(option);
                if (option.isOn)
                {
                    var check = option.graphic;
                    if (check == null || !check.isActiveAndEnabled || EffectiveColor(check).a <= 0.001f) return false;
                    flags |= UiCaptureNodeFlags.Selected | UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
                }
                if (option.isOn != (index == target.value)) return false;
                var row = (RectTransform)option.transform;
                HitAndFocus(optionPath, option, label, popupFill);
                if (!Text(optionPath, null, "DropdownChoice", Named<TMP_Text>(row, "Item Label"), label,
                    UiTextStyle.Body, Over(option.targetGraphic, popupFill), flags)) return false;
            }
            return true;
        }

        private bool CaptureBindingField(UiKeyBindingField fixture, RectTransform rect, string path, UiThemeColor? backdrop)
        {
            var field = rect.GetComponent<TopiaForgeInputBindingFieldDriver>()?.Field;
            if (field == null || fixture.Label is not { } label || field.Device != fixture.Device ||
                field.State == TopiaForgeInputBindingFieldState.Failed) return false;

            // Rebind labels can come from player settings. Only the Gallery's fixed input vocabulary is published.
            var key = field.BindingText;
            if (fixture.Device == UiInputDevice.KeyboardMouse ? key is not ("J" or "K" or "Unbound")
                : key is not ("X" or "LB" or "Unbound")) return false;
            var status = field.StatusText;
            var state = field.State switch
            {
                TopiaForgeInputBindingFieldState.Listening when status == TopiaForgeInputBindingState.ListeningText => "listening",
                TopiaForgeInputBindingFieldState.Conflict when status == "Also bound to Open supply ledger" => "conflict",
                TopiaForgeInputBindingFieldState.Unavailable when status == TopiaForgeInputBindingState.UnavailableText => "unavailable",
                TopiaForgeInputBindingFieldState.Reserved when status is "Reserved by Back" or "Reserved by TopiaForge Workbench"
                    or "Reserved by TopiaForge Gallery" or "Reserved by TopiaForge Manager" => "idle",
                TopiaForgeInputBindingFieldState.Idle when status.Length == 0 => "idle",
                _ => null,
            };
            if (state == null) return false;

            var button = field.RebindButton.Button;
            var flags = Flags(button);
            var capFill = Over(field.KeyCapFill, backdrop);
            var edge = field.KeyCapBorder;
            if (!edge.isActiveAndEnabled) return false;
            Add(path + "/key-boundary", null, "KeyCapBoundary", edge.rectTransform, ArtEdge, ArtPaint(edge, backdrop));
            HitAndFocus(path + "/rebind", button, label, backdrop);
            if (!Text(path, fixture.Id, nameof(UiKeyBindingField), field.NameLabel.Go.GetComponent<TMP_Text>(), label,
                    UiTextStyle.Caption, backdrop, flags, state: state, keyLabel: key) ||
                !Text(path + "/key", null, "KeyBindingValue", field.KeyCap.Go.GetComponent<TMP_Text>(), key,
                    UiTextStyle.Label, capFill, flags) ||
                !Text(path + "/rebind", null, "RebindAction", Named<TMP_Text>(field.RebindButton.Rect, "Label"), "Rebind",
                    UiTextStyle.Label, Over(button.targetGraphic, backdrop), flags, Decoration(field.RebindButton.Rect, button.targetGraphic)))
                return false;
            if (status.Length == 0) return true;

            var warning = field.State is TopiaForgeInputBindingFieldState.Conflict or TopiaForgeInputBindingFieldState.Reserved;
            if (warning)
            {
                var glyph = field.WarningIcon.Go.GetComponent<Image>();
                if (glyph == null || !glyph.isActiveAndEnabled || glyph.sprite == null) return false;
                Add(path + "/warning-glyph", null, "BindingWarningGlyph", glyph.rectTransform,
                    State(UiCaptureNodeFlags.SemanticMark, "Warning"), ArtPaint(glyph, backdrop));
            }
            return Text(path + "/status", null, "BindingStatus", field.StatusLabel.Go.GetComponent<TMP_Text>(), status,
                UiTextStyle.Caption, backdrop);
        }

        private bool CaptureTooltip(UiTooltip fixture, RectTransform native, string path, UiThemeColor? backdrop)
        {
            if (native.childCount == 0 || native.GetChild(0) is not RectTransform child ||
                child.name != (fixture.Child.Id ?? fixture.Child.GetType().Name) ||
                !Visit(fixture.Child, child, ChildPath(path, 0), backdrop)) return false;

            var popup = TopiaForgeScopedTooltip.VisiblePopup(native.gameObject);
            Add(path, fixture.Id, nameof(UiTooltip), native,
                new UiCaptureNodeContent(string.Empty, fixture.Message, 0, 0, UiCaptureNodeFlags.RequiresLabel,
                    state: popup == null ? "hidden" : "shown"), default);
            if (native.GetComponent<Selectable>() is { } passive)
                HitAndFocus(path, passive, fixture.Message, backdrop);
            if (popup == null) return true;

            var fill = popup.Find("Fill")?.GetComponent<Graphic>();
            if (fill == null || !fill.isActiveAndEnabled || popup.parent != canvas.transform) return false;
            var popupFill = Over(fill, backdrop);
            return Text(path + "/popup", null, "TooltipPopup", Named<TMP_Text>(popup, "Label"), fixture.Message,
                UiTextStyle.Label, popupFill, paint: Decoration(popup, fill));
        }
    }
}
