using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    internal enum ZombiesPhase
    {
        WaitingForWorld,
        Starting,
        Wave,
        InterWave,
        GameOver,
        ReturningToMenu
    }

    internal readonly struct ZombiesHudSnapshot : IEquatable<ZombiesHudSnapshot>
    {
        public ZombiesHudSnapshot(
            ZombiesPhase phase,
            int wave,
            int hostiles,
            int allies,
            int pending,
            int integrity,
            int maximumIntegrity,
            int score,
            int credits,
            int comboMultiplier,
            int uplinkCharges,
            int maximumUplinkCharges,
            int seconds,
            int? chargePercent,
            bool uplinkAvailable,
            bool shopEnabled,
            double? interWaveEndsAt = null,
            int uplinkRefillPercent = 0,
            bool shopReady = false)
        {
            Phase = phase;
            Wave = wave;
            Hostiles = hostiles;
            Allies = allies;
            Pending = pending;
            Integrity = integrity;
            MaximumIntegrity = maximumIntegrity;
            Score = score;
            Credits = credits;
            ComboMultiplier = comboMultiplier;
            UplinkCharges = uplinkCharges;
            MaximumUplinkCharges = maximumUplinkCharges;
            Seconds = seconds;
            ChargePercent = chargePercent;
            UplinkAvailable = uplinkAvailable;
            ShopEnabled = shopEnabled;
            InterWaveEndsAt = interWaveEndsAt;
            UplinkRefillPercent = uplinkRefillPercent;
            ShopReady = shopReady;
        }

        public ZombiesPhase Phase { get; }
        public int Wave { get; }
        public int Hostiles { get; }
        public int Allies { get; }
        public int Pending { get; }
        public int Integrity { get; }
        public int MaximumIntegrity { get; }
        public int Score { get; }
        public int Credits { get; }
        public int ComboMultiplier { get; }
        public int UplinkCharges { get; }
        public int MaximumUplinkCharges { get; }
        public int Seconds { get; }
        public double? InterWaveEndsAt { get; }
        public int? ChargePercent { get; }
        public int UplinkRefillPercent { get; }
        public bool UplinkAvailable { get; }
        public bool ShopEnabled { get; }
        public bool ShopReady { get; }

        public bool Equals(ZombiesHudSnapshot other) =>
            Phase == other.Phase && Wave == other.Wave && Hostiles == other.Hostiles
            && Allies == other.Allies && Pending == other.Pending && Integrity == other.Integrity
            && MaximumIntegrity == other.MaximumIntegrity && Score == other.Score
            && Credits == other.Credits && ComboMultiplier == other.ComboMultiplier
            && UplinkCharges == other.UplinkCharges
            && MaximumUplinkCharges == other.MaximumUplinkCharges && Seconds == other.Seconds
            && InterWaveEndsAt == other.InterWaveEndsAt
            && ChargePercent == other.ChargePercent && UplinkRefillPercent == other.UplinkRefillPercent
            && UplinkAvailable == other.UplinkAvailable && ShopEnabled == other.ShopEnabled && ShopReady == other.ShopReady;

        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Phase;
                hash = (hash * 397) ^ Wave;
                hash = (hash * 397) ^ Hostiles;
                hash = (hash * 397) ^ Allies;
                hash = (hash * 397) ^ Pending;
                hash = (hash * 397) ^ Integrity;
                hash = (hash * 397) ^ MaximumIntegrity;
                hash = (hash * 397) ^ Score;
                return hash;
            }
        }
    }

    /// <summary>Allocation-free-at-rest HUD bridge over the safe TopiaForgeUi surface.</summary>
    internal sealed class ZombiesHudPresenter : IDisposable
    {
        internal const string PhaseNodeId = "zombies-phase";
        internal const string InterWaveTimerNodeId = "zombies-inter-wave-timer";
        internal const string WaveNodeId = "zombies-wave";
        internal const string ScoreNodeId = "zombies-score";
        internal const string DetailsNodeId = "zombies-details";
        internal const string IntegrityNodeId = "zombies-integrity";
        internal const string CreditsNodeId = "zombies-credits";
        internal const string ComboNodeId = "zombies-combo";
        internal const string UplinkNodeId = "zombies-uplink";
        internal const string PhaseIconId = "zombies-phase-icon";
        internal const string IntegrityIconId = "zombies-integrity-icon";
        internal const string CreditsIconId = "zombies-credits-icon";
        internal const string FireSlotId = "zombies-fire-slot";
        internal const string JackInSlotId = "zombies-jack-in-slot";
        internal const string StandDownSlotId = "zombies-stand-down-slot";
        internal const string RequisitionsSlotId = "zombies-requisitions-slot";

        private static readonly UiFlexLayout IconAndLabel = new UiFlexLayout(
            spacing: UiSpace.Xs, crossAxisAlignment: UiCrossAxisAlignment.Center);
        private static readonly UiThresholds WaveThresholds = new UiThresholds(10, 5);

        private readonly IUiSurface surface;
        private readonly IModLogger logger;
        private bool hasSnapshot;
        private ZombiesHudSnapshot snapshot;
        private bool disposed;

        public ZombiesHudPresenter(IModContext context)
        {
            logger = context.Logger;
            var result = context.Ui.CreateSurface(new UiSurfaceRequest(
                "zombies-status",
                "ZOMBIES // SURVIVAL",
                string.Empty,
                UiSurfaceKind.Hud,
                604f,
                652f));
            if (!result.TryGetValue(out var created) || created == null)
            {
                throw new InvalidOperationException(
                    "TopiaForgeUi could not create the Zombies HUD: " + result.ErrorMessage);
            }

            surface = created;
            surface.Show();
        }

        public void Update(
            in ZombiesHudSnapshot next,
            IInputAction? fire,
            IInputAction? overrideAction,
            IInputAction? broadcast,
            IInputAction? shop)
        {
            if (disposed)
            {
                return;
            }

            if (hasSnapshot && next.Equals(snapshot))
            {
                return;
            }

            snapshot = next;
            hasSnapshot = true;
            var updated = surface.SetContent(BuildContent(next, fire, overrideAction, broadcast, shop));
            if (!updated.Succeeded)
            {
                logger.Warn("The Zombies HUD could not update (" + updated.ErrorCode + "): " + updated.ErrorMessage);
            }
        }

        public void ForceRefresh()
        {
            hasSnapshot = false;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            surface.Dispose();
        }

        private static UiNode BuildContent(in ZombiesHudSnapshot value, IInputAction? fire,
            IInputAction? overrideAction, IInputAction? broadcast, IInputAction? shop)
        {
            var phase = PhaseLabel(value);
            if (value.Phase == ZombiesPhase.Starting && value.Seconds > 0)
            {
                phase += "  //  " + value.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            }

            var wave = new UiText("WAVE  " + value.Wave.ToString(CultureInfo.InvariantCulture), UiTextStyle.Label, id: WaveNodeId);
            var score = new UiText("SCORE  " + value.Score.ToString(CultureInfo.InvariantCulture), UiTextStyle.Numeral, id: ScoreNodeId);
            var waveStats = value.ComboMultiplier > 1 ? new UiNode[]
            {
                wave,
                score,
                new UiBadge("Combo ×" + value.ComboMultiplier.ToString(CultureInfo.InvariantCulture),
                    UiPaintRole.Accent, UiBadgeStyle.Outlined, UiGlyph.Star, id: ComboNodeId),
            } : new UiNode[] { wave, score };

            UiNode waveContent = new UiWrap(waveStats, spacing: UiSpace.Lg, runSpacing: UiSpace.Xs);
            if (value.Phase == ZombiesPhase.InterWave)
                waveContent = new UiColumn(waveContent, InterWaveTimer(value));

            UiNode counters = new UiText(BuildDetails(value), id: DetailsNodeId);
            if (value.ShopEnabled)
            {
                var credits = WithGlyph(UiGlyph.Notes, CreditsIconId, new UiText(
                    "CREDITS  " + value.Credits.ToString(CultureInfo.InvariantCulture), UiTextStyle.Label, id: CreditsNodeId));
                counters = new UiWrap(new[] { credits, counters }, spacing: UiSpace.Lg, runSpacing: UiSpace.Xs);
            }
            var supplies = new List<UiNode>(3) { IntegrityStatus(value), counters };
            if (value.UplinkAvailable) supplies.Add(UplinkStatus(value));

            var content = new List<UiNode>(3)
            {
                new UiCard(waveContent,
                    header: WithGlyph(PhaseGlyph(value.Phase), PhaseIconId, new UiText(phase, UiTextStyle.Title, UiTone.Primary, PhaseNodeId)),
                    padding: new UiInsets(UiSpace.Md, UiSpace.Sm), id: "zombies-wave-card"),
                new UiCard(new UiColumn(supplies),
                    padding: new UiInsets(UiSpace.Md, UiSpace.Sm), id: "zombies-supplies-card"),
            };
            var actions = ActionSlots(value, fire, overrideAction, broadcast, shop);
            if (actions.Count != 0)
                content.Add(new UiWrap(actions, spacing: UiSpace.Sm, runSpacing: UiSpace.Sm));
            return new UiColumn(content);
        }

        private static UiRow WithGlyph(UiGlyph glyph, string id, UiText label) => new UiRow(
            new UiNode[] { new UiIcon(glyph, size: 18f, tone: label.Tone, decorative: true, id: id), label }, IconAndLabel);

        private static UiNode IntegrityStatus(in ZombiesHudSnapshot value)
        {
            var thresholds = new UiThresholds(value.MaximumIntegrity * 0.5d, value.MaximumIntegrity * 0.25d);
            var bar = new UiStatBar("INTEGRITY", value.Integrity, value.MaximumIntegrity, thresholds,
                UiStatFormat.ValueOfMaximum, id: IntegrityNodeId);
            return new UiRow(new UiNode[]
            {
                new UiIcon(UiGlyph.Health, size: 18f, tone: thresholds.ToneFor(value.Integrity, bar.Tone),
                    decorative: true, id: IntegrityIconId),
                new UiExpanded(bar),
            }, IconAndLabel);
        }

        private static UiNode InterWaveTimer(in ZombiesHudSnapshot value)
        {
            if (value.InterWaveEndsAt is double endsAt)
                return UiCountdown.WithFormat(endsAt, "Next wave", UiCountdownFormat.MinutesSeconds, WaveThresholds,
                    id: InterWaveTimerNodeId);

            var time = (value.Seconds / 60).ToString(CultureInfo.InvariantCulture) + ":" +
                (value.Seconds % 60).ToString("00", CultureInfo.InvariantCulture);
            return new UiText("Next wave paused " + time, UiTextStyle.Label, id: InterWaveTimerNodeId);
        }

        private static UiGlyph PhaseGlyph(ZombiesPhase phase) => phase switch
        {
            ZombiesPhase.WaitingForWorld => UiGlyph.Signal,
            ZombiesPhase.Starting => UiGlyph.Play,
            ZombiesPhase.InterWave => UiGlyph.Check,
            ZombiesPhase.GameOver => UiGlyph.Warning,
            ZombiesPhase.ReturningToMenu => UiGlyph.Leave,
            _ => UiGlyph.Flag,
        };

        private static List<UiNode> ActionSlots(in ZombiesHudSnapshot value, IInputAction? fire,
            IInputAction? overrideAction, IInputAction? broadcast, IInputAction? shop)
        {
            var slots = new List<UiNode>(4);
            if (fire != null)
            {
                var detail = value.ChargePercent is int percent ? "Charge " + PercentText(percent) : null;
                slots.Add(new UiActionSlot(fire.Name, "FIRE", detail: detail, emphasis: true, id: FireSlotId));
            }

            if (value.UplinkAvailable)
            {
                if (overrideAction != null)
                {
                    var charges = FitsPipTrack(value) ? new UiCharges(value.UplinkCharges, value.MaximumUplinkCharges) : null;
                    var detail = charges == null ? UplinkCount(value) : null;
                    slots.Add(new UiActionSlot(overrideAction.Name, "JACK IN", detail: detail, charges: charges, id: JackInSlotId));
                }

                if (broadcast != null)
                    slots.Add(new UiActionSlot(broadcast.Name, "STAND DOWN", id: StandDownSlotId));
            }

            if (value.ShopEnabled && shop != null)
            {
                var reason = value.ShopReady ? null : value.Phase == ZombiesPhase.Wave ? "After the wave" : "Unavailable";
                slots.Add(new UiActionSlot(shop.Name, "REQUISITIONS", unavailableReason: reason, id: RequisitionsSlotId));
            }

            return slots;
        }

        private static bool FitsPipTrack(in ZombiesHudSnapshot value) =>
            value.MaximumUplinkCharges is >= 1 and <= 32 && value.UplinkCharges >= 0 &&
            value.UplinkCharges <= value.MaximumUplinkCharges;

        private static UiNode UplinkStatus(in ZombiesHudSnapshot value)
        {
            if (FitsPipTrack(value))
            {
                var fraction = value.UplinkRefillPercent / 100f;
                // The kit truncates the percentage. Round one float upwards when conversion put it below its bucket.
                if ((int)(fraction * 100f) < value.UplinkRefillPercent)
                    fraction = BitConverter.Int32BitsToSingle(BitConverter.SingleToInt32Bits(fraction) + 1);
                return new UiPips(value.MaximumUplinkCharges, value.UplinkCharges, "Uplink", fraction,
                    presentation: UiPipStyle.Segments, id: UplinkNodeId);
            }

            // Run upgrades can take the real capacity beyond the kit's 32-mark track.
            var label = "Uplink: " + UplinkCount(value);
            if (value.UplinkRefillPercent > 0)
                label += " (" + PercentText(value.UplinkRefillPercent) + " refilling)";
            return new UiText(label, UiTextStyle.Label, id: UplinkNodeId);
        }

        private static string UplinkCount(in ZombiesHudSnapshot value) =>
            value.UplinkCharges.ToString(CultureInfo.InvariantCulture) + " / " +
            value.MaximumUplinkCharges.ToString(CultureInfo.InvariantCulture);

        private static string PercentText(int percent) => percent.ToString(CultureInfo.InvariantCulture) + "%";

        private static string BuildDetails(in ZombiesHudSnapshot value)
        {
            var text = new StringBuilder(64);
            text.Append("HOSTILES  ").Append(value.Hostiles.ToString(CultureInfo.InvariantCulture));
            if (value.Pending > 0)
            {
                text.Append(" +").Append(value.Pending.ToString(CultureInfo.InvariantCulture));
            }

            if (value.Allies > 0)
            {
                text.Append("    ALLIES  ").Append(value.Allies.ToString(CultureInfo.InvariantCulture));
            }

            return text.ToString();
        }

        private static string PhaseLabel(in ZombiesHudSnapshot value)
        {
            switch (value.Phase)
            {
                case ZombiesPhase.WaitingForWorld:
                    return "LINKING TO ARENA";
                case ZombiesPhase.Starting:
                    return "SYSTEM BOOT";
                case ZombiesPhase.InterWave:
                    return "WAVE CLEAR";
                case ZombiesPhase.GameOver:
                    return "SYSTEM FAILURE";
                case ZombiesPhase.ReturningToMenu:
                    return "RETURNING TO MENU";
                default:
                    return "HORDE ACTIVE";
            }
        }
    }
}
