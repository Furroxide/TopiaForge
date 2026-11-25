using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.UiGallery.Pages
{
    internal enum ActionGalleryPage { KeyGlyphs, Slots }

    /// <summary>Read-only action and binding previews shared by Gallery and its capture fixtures.</summary>
    internal static class ActionGalleryPages
    {
        internal const string Confirm = "gallery-confirm";
        internal const string Scan = "gallery-scan";
        internal const string Marker = "gallery-marker";
        internal const string Shortcut = "gallery-shortcut";
        internal const string Pan = "gallery-pan";
        internal const string Spare = "gallery-spare";
        private static readonly UiFlexLayout CaptionAbove = new UiFlexLayout(UiSpace.Xs, crossAxisAlignment: UiCrossAxisAlignment.Start);

        internal static readonly InputActionDefinition[] Definitions =
        {
            new InputActionDefinition(Confirm, "Confirm", new[] { InputBinding.Key("E"), InputBinding.GamepadButton(InputGamepadButton.South) }),
            new InputActionDefinition(Scan, "Scan", new[] { InputBinding.Key("Space"), InputBinding.GamepadButton(InputGamepadButton.LeftShoulder) }),
            new InputActionDefinition(Marker, "Marker", new[] { InputBinding.MouseButton(InputMouseButton.Back) }),
            new InputActionDefinition(Shortcut, "Shortcut", new[] { InputBinding.Key("Backspace") }),
            new InputActionDefinition(Pan, "Pan", new[] { InputBinding.GamepadAxis(InputGamepadAxis.RightX) }),
            new InputActionDefinition(Spare, "Spare action", new[] { InputBinding.Key("U") }),
        };

        internal static bool RegisterActions(IInputService input)
        {
            var ready = true;
            foreach (var definition in Definitions)
            {
                if (!input.RegisterAction(definition).TryGetValue(out var action) || action == null)
                {
                    ready = false;
                    continue;
                }
                if (definition.Name == Spare) ready &= action.Rebind(Array.Empty<InputBinding>()).Succeeded;
            }
            return ready;
        }

        internal static IReadOnlyList<InputBinding>? DefaultBindings(string action)
        {
            foreach (var definition in Definitions)
                if (definition.Name == action) return definition.DefaultBindings;
            return ControlGalleryPages.DefaultBindings(action);
        }

        internal static UiSurfaceRequest CreateRequest(ActionGalleryPage page, UiSurfaceKind kind, double clockTime,
            UiThemeRef? theme = null, bool transport = false)
        {
            var (id, title, content) = page switch
            {
                ActionGalleryPage.KeyGlyphs => ("gallery-key-glyphs", "Key glyphs", KeyGlyphs()),
                ActionGalleryPage.Slots => ("gallery-action-slots", "Action slots", Slots(Math.Floor(clockTime), transport)),
                _ => throw new ArgumentOutOfRangeException(nameof(page)),
            };
            var request = new UiSurfaceRequest(id, title, string.Empty, kind, 840f, 360f, content).WithTheme(theme);
            return kind == UiSurfaceKind.Hud ? request.WithHudAnchor(UiHudAnchor.Top).WithHudVisibility(UiHudVisibility.Always) : request;
        }

        private static UiNode KeyGlyphs() => new UiGrid(new UiNode[]
        {
            KeyCell("Confirm", Confirm, "key-confirm"),
            KeyCell("Scan", Scan, "key-scan"),
            KeyCell("Mouse", Marker, "key-marker"),
            KeyCell("Long key name", Shortcut, "key-shortcut"),
            KeyCell("Gamepad axis", Pan, "key-pan"),
            KeyCell("Cleared action", Spare, "key-spare"),
        }, minimumCellWidth: 180f, maximumColumns: 3, runSpacing: UiSpace.Md, id: "binding-grid");

        private static UiNode Slots(double time, bool transport) => new UiColumn(
            new UiWrap(new UiNode[]
            {
                new UiActionSlot(Confirm, "Ready", detail: "Stand by", id: "slot-ready"),
                new UiActionSlot(Scan, "Scan", glyph: UiGlyph.Star, detail: "Powered", emphasis: true, id: "slot-emphasis"),
                new UiActionSlot(Confirm, "Charge", image: transport ? FoundationArtPages.TransportMark : FoundationArtPages.Mark,
                    cooldown: new UiCooldown(time + 6d, 12d), id: "slot-cooldown"),
                new UiActionSlot(Marker, "Marker", charges: new UiCharges(2, 3, new UiCooldown(time + 8d, 16d)), id: "slot-charging"),
                new UiActionSlot(Scan, "Request", unavailableReason: "After wave", id: "slot-unavailable"),
            }, spacing: UiSpace.Md, runSpacing: UiSpace.Md, id: "slot-bar"),
            UiPips.WithRefill(4, 2, "Cells", new UiCooldown(time + 8d, 16d), UiPipStyle.Segments, id: "pips-refill"));

        private static UiNode KeyCell(string label, string action, string id) =>
            new UiColumn(new UiNode[] { new UiText(label, UiTextStyle.Caption), new UiKeyGlyph(action, id) }, CaptionAbove);
    }
}
