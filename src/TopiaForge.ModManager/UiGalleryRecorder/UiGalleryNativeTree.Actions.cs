using System;
using System.Globalization;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using TopiaForge.UiGallery.Pages;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class UiGalleryNativeTree
    {
        private bool CaptureAction(UiNode fixture, RectTransform native, string path, UiThemeColor? backdrop)
        {
            if (onlyUnavailableImages) return true;
            var start = observations.Count;
            var accepted = fixture is UiKeyGlyph key
                ? CaptureActionCap(native, path, key.Id, key.ActionName, nameof(UiKeyGlyph), backdrop, out _)
                : CaptureActionSlot((UiActionSlot)fixture, native, path, backdrop);
            if (accepted) RegisterGroup(start);
            return accepted;
        }

        private bool CaptureActionCap(RectTransform native, string path, string? id, string action, string kind,
            UiThemeColor? backdrop, out UiActionBindingPresentation observed)
        {
            observed = default;
            if (native.Find("Key cap") is not RectTransform cap ||
                cap.Find("Key cap fill")?.GetComponent<TopiaForgeRoundedImage>() is not { } plate ||
                cap.Find("Key cap border")?.GetComponent<TopiaForgeRoundedImage>() is not { } edge ||
                !ReadPaint(plate, out var fill) || !ReadPaint(edge, out var boundary)) return false;

            var label = Named<TMP_Text>(cap, "Label");
            if (label == null) return false;
            var role = Descriptor.GetText(UiTextStyle.Label);
            var expected = UiActionBindingPresentation.Resolve(ActionGalleryPages.DefaultBindings(action),
                Array.Empty<UiInputBindingHint>(), TopiaForgeInputOwnership.LastViewerDevice);
            var cue = cap.Find("Key cap cue")?.GetComponent<Image>();
            if (cue == null) return false;
            if (UiGalleryCaptureFixture.SafeDisplayText(label.text, expected.Label, role.IsUppercase) != null)
                observed = expected;
            else if (UiGalleryCaptureFixture.SafeDisplayText(label.text, "Unbound", role.IsUppercase) != null &&
                cue.isActiveAndEnabled && cue.sprite != null && cue.sprite == TopiaForgeSprites.Glyph(UiGlyph.Info))
                observed = UiActionBindingPresentation.Resolve(Array.Empty<InputBinding>(), Array.Empty<UiInputBindingHint>(),
                    TopiaForgeInputOwnership.LastViewerDevice);
            else return false;

            if (cue.isActiveAndEnabled != observed.Cue.HasValue) return false;
            var semantic = "key " + observed.SpokenControl;
            var pixels = VerticalScale(cap);
            Add(path, id, kind, cap, new UiCaptureNodeContent(string.Empty, semantic, 0f, 0f,
                UiCaptureNodeFlags.RequiresLabel, keyLabel: observed.Label), new UiCaptureNodePaint(null, fill,
                    plate.ResolvedCornerRadius * pixels, edge.ResolvedBorderWidth * pixels));
            Add(path + "/edge", null, "KeyCapBoundary", edge.rectTransform, ArtEdge, new UiCaptureNodePaint(boundary, backdrop));
            if (observed.Cue.HasValue)
                Add(path + "/info", null, "KeyCapCue", cue.rectTransform, ArtMark, ArtPaint(cue, fill));
            return Text(path + "/label", null, "KeyCapLabel", label, observed.Label, UiTextStyle.Label, fill);
        }

        private bool CaptureActionSlot(UiActionSlot fixture, RectTransform native, string path, UiThemeColor? backdrop)
        {
            if (native.Find("Action fill")?.GetComponent<TopiaForgeRoundedImage>() is not { } plate ||
                native.Find("Action boundary")?.GetComponent<TopiaForgeRoundedImage>() is not { } edge ||
                !ReadPaint(plate, out var fill)) return false;
            var state = UiActionSlotPresentation.Resolve(fixture, clockTime, clockEpoch);
            var pixels = VerticalScale(native);
            if (state.Emphasized && (!edge.isActiveAndEnabled ||
                edge.ResolvedBorderWidth * pixels + 0.01f < UiActionSlotGeometry.EmphasisBorder * PixelsPerUnit)) return false;
            if (!CaptureActionCap(native, path + "/key", null, fixture.ActionName, "ActionKey", fill, out var key)) return false;

            var label = Named<TMP_Text>(native, "Action label");
            var status = Named<TMP_Text>(native, "Action status");
            var labelPlate = native.Find("Action label backing")?.GetComponent<Graphic>();
            var statusPlate = native.Find("Action status backing")?.GetComponent<Graphic>();
            if (labelPlate == null || statusPlate == null || !ReadPaint(labelPlate, out var labelFill) ||
                !Text(path + "/label", null, "ActionLabel", label, fixture.Label, UiTextStyle.Label, labelFill)) return false;

            var written = state.Status switch
            {
                UiActionSlotStatus.Cooldown => state.Cooldown.RemainingSeconds.ToString(CultureInfo.InvariantCulture) + " s",
                UiActionSlotStatus.Charges => CountText(state.Charges.Filled, state.Charges.Total),
                _ => state.Text,
            };
            var spoken = state.Status switch
            {
                UiActionSlotStatus.Cooldown => state.Cooldown.RemainingSeconds.ToString(CultureInfo.InvariantCulture) + " seconds",
                UiActionSlotStatus.Charges => state.Charges.Filled.ToString(CultureInfo.InvariantCulture) + " of " +
                    state.Charges.Total.ToString(CultureInfo.InvariantCulture) + " charges",
                UiActionSlotStatus.Unavailable => state.Text!,
                _ => "ready",
            };
            if (written != null)
            {
                if (!ReadPaint(statusPlate, out var statusFill) ||
                    !Text(path + "/status", null, "ActionStatus", status, written, UiTextStyle.Caption, statusFill)) return false;
            }
            else if (statusPlate.isActiveAndEnabled) return false;

            if (!CaptureActionMarks(fixture, native, path, fill, state, out var fraction)) return false;
            var flags = UiCaptureNodeFlags.RequiresLabel;
            if (state.Emphasized || state.StatusGlyph.HasValue)
                flags |= UiCaptureNodeFlags.RequiresNonColorCue | UiCaptureNodeFlags.HasNonColorCue;
            double? seconds = state.Status == UiActionSlotStatus.Cooldown ? state.Cooldown.RemainingSeconds :
                state.Charges.IsRefilling ? state.Charges.RemainingSeconds : null;
            var semantic = fixture.Label + ", key " + key.SpokenControl + ", " + spoken;
            Add(path, fixture.Id, nameof(UiActionSlot), native, new UiCaptureNodeContent(string.Empty, semantic, 0f, 0f,
                flags, state.StateName, key.Label, seconds, fraction), new UiCaptureNodePaint(null, fill,
                    plate.ResolvedCornerRadius * pixels, edge.isActiveAndEnabled ? edge.ResolvedBorderWidth * pixels : 0f,
                    styleId: ArtStyle(fixture.Style)));
            if (edge.isActiveAndEnabled) Add(path + "/edge", null, "ActionBoundary", edge.rectTransform, ArtEdge,
                ArtPaint(edge, state.Emphasized ? fill : backdrop));
            return true;
        }

        private bool CaptureActionMarks(UiActionSlot fixture, RectTransform native, string path, UiThemeColor fill,
            UiActionSlotPresentation state, out float? fraction)
        {
            fraction = null;
            var lockCue = native.Find("Action status cue")?.GetComponent<Image>();
            if (lockCue == null || lockCue.isActiveAndEnabled != state.StatusGlyph.HasValue) return false;
            if (state.StatusGlyph is { } glyph)
            {
                if (lockCue.sprite == null || lockCue.sprite != TopiaForgeSprites.Glyph(glyph)) return false;
                Add(path + "/lock", null, "ActionLock", lockCue.rectTransform, ArtMark, ArtPaint(lockCue, fill));
            }

            var marker = native.Find("Action art")?.GetComponent<Image>();
            var placeholder = native.Find("Action image placeholder")?.GetComponent<TopiaForgeRoundedImage>();
            if (marker == null || placeholder == null) return false;
            if (fixture.Glyph is { } symbol && (!marker.isActiveAndEnabled || marker.sprite != TopiaForgeSprites.Glyph(symbol))) return false;
            if (marker.isActiveAndEnabled && marker.sprite != null)
                Add(path + "/art", null, "ActionArtwork", marker.rectTransform, ArtDecoration, ArtPaint(marker, fill));
            else if (fixture.Image != null)
            {
                if (!placeholder.isActiveAndEnabled) return false;
                Add(path + "/art", null, "ActionArtwork", placeholder.rectTransform, ArtDecoration,
                    ArtPaint(placeholder, fill), localFallback: true);
            }

            if (state.Status == UiActionSlotStatus.Charges)
                return CaptureActionCharges(native.Find("Action charges"), path, fill, state.Charges, out fraction);

            if (native.Find("Action sweep mask") is not RectTransform sweep) return false;
            if (state.Status != UiActionSlotStatus.Cooldown) return !sweep.gameObject.activeSelf;
            var bounds = Bounds(native);
            var sweepWidth = sweep.gameObject.activeSelf ? Bounds(sweep).Width : 0f;
            if (bounds.Width <= 0f || sweepWidth < 0f || sweepWidth > bounds.Width + 0.01f) return false;
            fraction = Math.Min(1f, sweepWidth / bounds.Width);
            if (sweep.gameObject.activeSelf)
            {
                if (!sweep.TryGetComponent<RectMask2D>(out var mask) || !mask.isActiveAndEnabled ||
                    sweep.Find("Action sweep")?.GetComponent<TopiaForgeRoundedImage>() is not { } paint ||
                    !ReadPaint(paint, out var sweepFill)) return false;
                Add(path + "/sweep", null, "CooldownSweep", sweep, ArtDecoration, new UiCaptureNodePaint(null, sweepFill));
            }
            return true;
        }

        private bool CaptureActionCharges(Transform? marks, string path, UiThemeColor fill,
            UiRefillPresentation state, out float? fraction)
        {
            fraction = state.IsRefilling ? 0f : null;
            if (marks == null || marks.childCount != state.Total * 2) return false;
            for (var index = 0; index < state.Total; index++)
            {
                var edge = marks.GetChild(index * 2).GetComponent<TopiaForgeRoundedImage>();
                var painted = marks.GetChild(index * 2 + 1).GetComponent<TopiaForgeRoundedImage>();
                if (edge == null || painted == null || !edge.isActiveAndEnabled) return false;
                var mark = ChildPath(path + "/charges", index);
                Add(mark, null, "ActionChargeBoundary", edge.rectTransform, ArtEdge, ArtPaint(edge, fill));
                if (!painted.isActiveAndEnabled)
                {
                    if (index < state.Filled) return false;
                    continue;
                }
                if (index > state.Filled || index == state.Filled && !state.IsRefilling) return false;
                var bounds = Bounds(edge.rectTransform);
                var width = Bounds(painted.rectTransform).Width;
                if (bounds.Width <= 0f || width <= 0f || width > bounds.Width + 0.01f ||
                    index < state.Filled && Math.Abs(width - bounds.Width) > 0.01f) return false;
                if (index == state.Filled) fraction = Math.Min(1f, width / bounds.Width);
                Add(mark + "/fill", null, "ActionChargeFill", painted.rectTransform, ArtMark, ArtPaint(painted, fill));
            }
            return true;
        }

        private static string CountText(int filled, int total) =>
            filled.ToString(CultureInfo.InvariantCulture) + " / " + total.ToString(CultureInfo.InvariantCulture);
    }
}
