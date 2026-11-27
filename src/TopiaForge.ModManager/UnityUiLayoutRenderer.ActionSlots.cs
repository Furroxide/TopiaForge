using System;
using System.Collections.Generic;
using TMPro;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.UI;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed partial class UnityUiLayoutRenderer
        {
            private readonly Dictionary<UiNode, NativeActionSlot> actionSlots =
                new Dictionary<UiNode, NativeActionSlot>(NodeIdentity.Instance);
            private readonly List<NativeActionSlot> actionSlotViews = new List<NativeActionSlot>();

            private void CreateActionSlot(NativeNode view, UiActionSlot slot, UiCallbackGate callbacks)
            {
                var scope = input ?? new UiSurfaceInputContext(null, string.Empty);
                var binding = scope.BindAction(slot.ActionName, slot.WireDefaults);
                var native = new NativeActionSlot(view.Container, slot, binding, callbacks,
                    slot.Image == null ? null : () => artwork!.Resolve(slot));
                view.ActionSlot = native;
                actionSlots.TryAdd(slot, native);
                actionSlotViews.Add(native);
            }

            private sealed partial class NativeActionSlot
            {
                private static readonly UiInsets ContentInsets = new UiInsets(UiSpace.Sm);
                private const float Gap = 4f;
                private const float CueSize = 18f;
                private const float ChargeHeight = 8f;

                private readonly TopiaForgeContainer host;
                private readonly UiActionSlot source;
                private readonly UiNativeActionBinding binding;
                private readonly UiCallbackGate callbacks;
                private readonly Func<(Sprite? Sprite, bool IsMask)>? imageResource;
                private readonly TopiaForgeActionKeyCap key;
                private readonly TopiaForgeRoundedImage shadow;
                private readonly TopiaForgeRoundedImage background;
                private readonly TopiaForgeRoundedImage outline;
                private readonly TopiaForgeContainer sweepMask;
                private readonly TopiaForgeRoundedImage sweep;
                private readonly Image art;
                private readonly TopiaForgeRoundedImage imageFallback;
                private readonly Image labelPlate;
                private readonly Image statusPlate;
                private readonly TopiaForgeLabel caption;
                private readonly TopiaForgeLabel status;
                private readonly TMP_Text captionText;
                private readonly TMP_Text statusText;
                private readonly Image statusCue;
                private readonly ChargeMark[] marks;
                private readonly char[] digits = new char[UiCountdownFormatting.MaximumLength + 4];

                private UiThemeScheme? scheme;
                private TopiaForgeResolvedTheme? theme;
                private UiPaintRole backdrop;
                private UiLayoutDecoration decoration;
                private SlotLayout layout;
                private UiActionSlotPresentation painted;
                private Color paintInk;
                private bool initialized;

                internal NativeActionSlot(TopiaForgeContainer host, UiActionSlot source, UiNativeActionBinding binding,
                    UiCallbackGate callbacks, Func<(Sprite? Sprite, bool IsMask)>? imageResource)
                {
                    this.host = host;
                    this.source = source;
                    this.binding = binding;
                    this.callbacks = callbacks;
                    this.imageResource = imageResource;
                    shadow = AddGraphic<TopiaForgeRoundedImage>(host, "Action shadow");
                    background = AddGraphic<TopiaForgeRoundedImage>(host, "Action fill");
                    sweepMask = host.CreateChild("Action sweep mask");
                    sweepMask.Go.AddComponent<RectMask2D>();
                    sweep = AddGraphic<TopiaForgeRoundedImage>(sweepMask, "Action sweep");
                    outline = AddGraphic<TopiaForgeRoundedImage>(host, "Action boundary");
                    key = new TopiaForgeActionKeyCap(host);
                    imageFallback = AddGraphic<TopiaForgeRoundedImage>(host, "Action image placeholder");
                    art = AddGraphic<Image>(host, "Action art");
                    labelPlate = AddGraphic<Image>(host, "Action label backing");
                    caption = host.Label(source.Label, TopiaForgeTextStyle.Label).PlainText().AlignTopLeft();
                    caption.Go.name = "Action label";
                    captionText = caption.Go.GetComponent<TMP_Text>();
                    statusPlate = AddGraphic<Image>(host, "Action status backing");
                    status = host.Label(string.Empty, TopiaForgeTextStyle.Caption).PlainText().AlignTopLeft();
                    status.Go.name = "Action status";
                    statusText = status.Go.GetComponent<TMP_Text>();
                    statusCue = AddGraphic<Image>(host, "Action status cue");
                    marks = CreateChargeMarks(host, source.Charges?.Maximum ?? 0);
                    host.Go.AddComponent<UiSurfaceLayoutFrame>().Initialize(Tick);
                }

                internal bool RefreshBinding() => binding.Refresh();

                internal UiLayoutLeafMeasurement Measure(UiThemeDescriptor effective, UiThemeDescriptor? declared,
                    bool highContrast)
                {
                    var measurement = ReadLayout(effective);
                    var style = UiLayoutDecoration.Resolve(source, effective, declared, measurement.Size, highContrast);
                    return new UiLayoutLeafMeasurement(measurement.Size, UiLayoutPaintKind.Protected, style.ShadowOutsets,
                        localPaintBounds: new UiLayoutRect(0, 0, measurement.Size.Width, measurement.Size.Height));
                }

                internal void Apply(UiPaintRole surface, UiThemeScheme colors, TopiaForgeResolvedTheme native,
                    UiThemeDescriptor effective, UiThemeDescriptor? declared)
                {
                    layout = ReadLayout(effective);
                    backdrop = surface;
                    scheme = colors;
                    theme = native;
                    decoration = UiLayoutDecoration.Resolve(source, effective, declared, layout.Size, native.HighContrast);
                    initialized = false;
                    Tick();
                }

                private SlotLayout ReadLayout(UiThemeDescriptor descriptor)
                {
                    var scale = host.Host.EffectiveUiScale;
                    var readability = Math.Max(1f, scale) / scale;
                    var panel = UiActionSlotGeometry.ContentBounds(scale);
                    var inside = UiActionSlotGeometry.ContentBounds(scale, ContentInsets);
                    var gap = Gap * readability;
                    var artWidth = source.Glyph.HasValue || source.Image != null ? UiActionSlotGeometry.IconSize * readability : 0;
                    var keyLimit = inside.Width - (artWidth > 0 ? artWidth + gap : 0);
                    var keySize = key.Measure(binding.Face, keyLimit).Size;
                    if (keySize.Width > keyLimit + 0.001)
                        throw new ArgumentException("The action binding does not fit its key cap.");

                    var label = MeasureText(captionText, source.Label, descriptor.GetText(UiTextStyle.Label), inside.Width);
                    var headerHeight = Math.Max(keySize.Height, artWidth);
                    var cueWidth = source.UnavailableReason != null ? CueSize * readability : 0f;
                    var statusWidth = inside.Width - (cueWidth > 0f ? cueWidth + gap : 0);
                    var format = descriptor.GetText(UiTextStyle.Caption);
                    var statusHeight = source.UnavailableReason != null
                        ? Math.Max(MeasureText(statusText, source.UnavailableReason, format, statusWidth).y, cueWidth)
                        : RequiredStatusHeight(format, statusWidth, gap, readability);
                    var chargeLabelHeight = source.Charges == null || source.UnavailableReason != null ? 0f
                        : MeasureText(statusText, "32 / 32", format, statusWidth).y;
                    var contentHeight = headerHeight + label.y + statusHeight;
                    if (contentHeight > panel.Height + 0.001)
                        throw new ArgumentException("The action label and status do not fit inside the slot.");

                    // A live binding may wrap after admission. Give back vertical spacing before changing readable content.
                    var (top, rowGap) = UiActionSlotGeometry.FitVertical(panel.Height, contentHeight, inside.Y, gap, statusHeight > 0 ? 2 : 1);
                    inside = new UiLayoutRect(inside.X, top, inside.Width, panel.Height - 2 * top);
                    var labelTop = inside.Y + headerHeight + rowGap;
                    var statusTop = labelTop + label.y + rowGap;
                    var labelBounds = new UiLayoutRect(inside.X, labelTop, label.x, label.y);
                    var keyBounds = new UiLayoutRect(inside.X, inside.Y, keySize.Width, keySize.Height);
                    var artBounds = new UiLayoutRect(inside.Right - artWidth, inside.Y, artWidth, artWidth);
                    return new SlotLayout(new UiLayoutExtent(panel.Width, panel.Height), inside, keyBounds, artBounds,
                        labelBounds, statusTop, statusWidth, statusHeight, chargeLabelHeight, cueWidth, readability);
                }

                private double RequiredStatusHeight(UiThemeTextRole format, double width, double gap, double readability)
                {
                    var required = source.Detail == null ? 0d : MeasureText(statusText, source.Detail, format, width).y;
                    if (source.Cooldown != null)
                        required = Math.Max(required, MeasureText(statusText, "2147483647 s", format, width).y);
                    if (source.Charges != null)
                        required = Math.Max(required, MeasureText(statusText, "32 / 32", format, width).y + gap + ChargeHeight * readability);
                    return required;
                }

                private static Vector2 MeasureText(TMP_Text text, string value, UiThemeTextRole format, double width)
                {
                    var visual = format.IsUppercase ? value.ToUpperInvariant() : value;
                    var size = text.GetPreferredValues(visual, (float)width, 0f);
                    var limit = text.GetPreferredValues("M\nM", (float)width, 0f).y;
                    if (size.x <= 0f || size.x > width + 0.001 || size.y <= 0f || size.y > limit + 0.001f)
                        throw new ArgumentException("An action label or status needs more than two readable lines.");
                    return size;
                }

                private readonly struct SlotLayout
                {
                    internal SlotLayout(UiLayoutExtent size, UiLayoutRect interior, UiLayoutRect key, UiLayoutRect art,
                        UiLayoutRect caption, double statusTop, double statusWidth, double statusHeight, float chargeLabelHeight,
                        float cueWidth, float readability)
                    {
                        Size = size;
                        Interior = interior;
                        Key = key;
                        Art = art;
                        Caption = caption;
                        StatusTop = statusTop;
                        StatusWidth = statusWidth;
                        StatusHeight = statusHeight;
                        ChargeLabelHeight = chargeLabelHeight;
                        CueWidth = cueWidth;
                        Readability = readability;
                    }

                    internal UiLayoutExtent Size { get; }
                    internal UiLayoutRect Interior { get; }
                    internal UiLayoutRect Key { get; }
                    internal UiLayoutRect Art { get; }
                    internal UiLayoutRect Caption { get; }
                    internal double StatusTop { get; }
                    internal double StatusWidth { get; }
                    internal double StatusHeight { get; }
                    internal float ChargeLabelHeight { get; }
                    internal float CueWidth { get; }
                    internal float Readability { get; }
                    internal double GapSize => Gap * Readability;
                }
            }
        }
    }
}
