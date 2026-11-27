using System;

namespace TopiaForge.Mods
{
    internal enum UiActionSlotState { Ready, Emphasis, Cooldown, Charging, Unavailable }
    internal enum UiActionSlotStatus { None, Detail, Charges, Cooldown, Unavailable }

    /// <summary>A viewer's current presentation of an immutable action slot.</summary>
    internal readonly struct UiActionSlotPresentation
    {
        private UiActionSlotPresentation(UiActionSlotState state, UiActionSlotStatus status, bool emphasized,
            string? text = null, UiCooldownFrame cooldown = default, UiRefillPresentation charges = default)
        {
            State = state;
            Status = status;
            Emphasized = emphasized;
            Text = text;
            Cooldown = cooldown;
            Charges = charges;
        }

        internal UiActionSlotState State { get; }
        internal UiActionSlotStatus Status { get; }
        internal bool Emphasized { get; }
        internal string? Text { get; }
        internal UiCooldownFrame Cooldown { get; }
        internal UiRefillPresentation Charges { get; }
        internal UiGlyph? StatusGlyph => State == UiActionSlotState.Unavailable ? UiGlyph.Lock : null;
        internal UiTone Tone => State == UiActionSlotState.Unavailable ? UiTone.Muted : UiTone.Neutral;

        internal string StateName => State switch
        {
            UiActionSlotState.Emphasis => "emphasis",
            UiActionSlotState.Cooldown => "cooldown",
            UiActionSlotState.Charging => "charging",
            UiActionSlotState.Unavailable => "unavailable",
            _ => "ready",
        };

        internal UiPaintRole Backdrop(UiPaintRole readyFill) => State == UiActionSlotState.Unavailable ? UiPaintRole.Sunken
            : Emphasized ? UiPaintRole.Raised : readyFill;

        internal static UiActionSlotPresentation Resolve(UiActionSlot slot, double clockTime, string epoch)
        {
            if (slot.UnavailableReason is { } reason)
                return new UiActionSlotPresentation(UiActionSlotState.Unavailable, UiActionSlotStatus.Unavailable, false, text: reason);
            if (slot.Cooldown?.Sample(clockTime, epoch) is { IsRunning: true } cooldown)
                return new UiActionSlotPresentation(UiActionSlotState.Cooldown, UiActionSlotStatus.Cooldown, slot.Emphasis, cooldown: cooldown);

            var ready = slot.Emphasis ? UiActionSlotState.Emphasis : UiActionSlotState.Ready;
            if (slot.Charges is { } charges)
            {
                var shown = UiRefillPresentation.For(charges, clockTime, epoch);
                return new UiActionSlotPresentation(shown.IsRefilling ? UiActionSlotState.Charging : ready,
                    UiActionSlotStatus.Charges, slot.Emphasis, charges: shown);
            }

            return new UiActionSlotPresentation(ready, slot.Detail == null ? UiActionSlotStatus.None : UiActionSlotStatus.Detail,
                slot.Emphasis, text: slot.Detail);
        }
    }

    /// <summary>A pip or charge count and its next mark's fill at the viewer's current time.</summary>
    internal readonly struct UiRefillPresentation
    {
        private UiRefillPresentation(int filled, int total, float fraction, int remainingSeconds, UiPipStyle style)
        {
            Filled = filled;
            Total = total;
            Fraction = fraction;
            RemainingSeconds = remainingSeconds;
            Style = style;
        }

        internal int Filled { get; }
        internal int Total { get; }
        internal float Fraction { get; }
        internal int RemainingSeconds { get; }
        internal UiPipStyle Style { get; }
        internal bool IsRefilling => RemainingSeconds > 0;
        internal int? Percentage => IsRefilling ? Math.Min(99, (int)(Fraction * 100f)) : null;

        internal static UiRefillPresentation For(UiCharges charges, double clockTime, string epoch) => From(charges.Available,
            charges.Maximum, 0f, charges.Maximum <= 9 ? UiPipStyle.Dots : UiPipStyle.Segments, charges.Refill, clockTime, epoch);

        internal static UiRefillPresentation For(UiPips pips, double clockTime, string epoch) =>
            From(pips.Filled, pips.Total, pips.Fraction, pips.Presentation, pips.Refill, clockTime, epoch);

        private static UiRefillPresentation From(int filled, int total, float fraction, UiPipStyle style,
            UiCooldown? refill, double clockTime, string epoch)
        {
            if (refill == null) return new UiRefillPresentation(filled, total, fraction, 0, style);
            var progress = refill.Sample(clockTime, epoch);
            return progress.IsRunning
                ? new UiRefillPresentation(filled, total, progress.Elapsed, progress.RemainingSeconds, style)
                : new UiRefillPresentation(Math.Min(filled + 1, total), total, 0f, 0, style);
        }
    }

    /// <summary>Slot dimensions in layout units before the canvas applies the viewer's scale.</summary>
    internal static class UiActionSlotGeometry
    {
        internal const int MaximumLabelLines = 2;
        internal const float EmphasisBorder = 3f;

        internal static float EmphasisStroke(float standardBorder, float readableScale) =>
            Math.Max(EmphasisBorder * readableScale, standardBorder + readableScale);
        internal const float IconSize = 24f;
        private const float ReferenceWidth = 128f;
        private const float ReferenceHeight = 112f;

        internal static UiLayoutRect ContentBounds(float uiScale, UiInsets insets = default)
        {
            if (!(uiScale >= 0.75f && uiScale <= 2f)) throw new ArgumentOutOfRangeException(nameof(uiScale));
            var factor = Math.Max(1d, uiScale) / uiScale;
            return new UiLayoutRect((int)insets.Left * factor, (int)insets.Top * factor,
                (ReferenceWidth - insets.Horizontal) * factor, (ReferenceHeight - insets.Vertical) * factor);
        }

        internal static (double Inset, double Gap) FitVertical(double frameHeight, double contentHeight,
            double preferredInset, double preferredGap, int gapCount)
        {
            var freeHeight = Math.Max(0d, frameHeight - contentHeight);
            var ratio = Math.Min(1d, freeHeight / (2d * preferredInset + gapCount * preferredGap));
            return (preferredInset * ratio, preferredGap * ratio);
        }
    }
}
