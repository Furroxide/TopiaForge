using System;
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
            int chargePercent,
            bool uplinkAvailable,
            bool shopEnabled)
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
        public int ChargePercent { get; }
        public bool UplinkAvailable { get; }
        public bool ShopEnabled { get; }
        public bool ShopAvailable => ShopEnabled
            && (Phase == ZombiesPhase.Starting || Phase == ZombiesPhase.InterWave);

        public bool Equals(ZombiesHudSnapshot other) =>
            Phase == other.Phase && Wave == other.Wave && Hostiles == other.Hostiles
            && Allies == other.Allies && Pending == other.Pending && Integrity == other.Integrity
            && MaximumIntegrity == other.MaximumIntegrity && Score == other.Score
            && Credits == other.Credits && ComboMultiplier == other.ComboMultiplier
            && UplinkCharges == other.UplinkCharges
            && MaximumUplinkCharges == other.MaximumUplinkCharges && Seconds == other.Seconds
            && ChargePercent == other.ChargePercent && UplinkAvailable == other.UplinkAvailable
            && ShopEnabled == other.ShopEnabled;

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
        private readonly IUiSurface surface;
        private bool hasSnapshot;
        private ZombiesHudSnapshot snapshot;
        private InputBindingKind fireKind;
        private string fireControl = string.Empty;
        private InputBindingKind overrideKind;
        private string overrideControl = string.Empty;
        private InputBindingKind broadcastKind;
        private string broadcastControl = string.Empty;
        private InputBindingKind shopKind;
        private string shopControl = string.Empty;
        private bool disposed;

        public ZombiesHudPresenter(IModContext context)
        {
            var result = context.Ui.CreateSurface(new UiSurfaceRequest(
                "zombies-status",
                "ZOMBIES // SURVIVAL",
                string.Empty,
                UiSurfaceKind.Hud,
                440f,
                230f));
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

            var bindingsChanged = CaptureBinding(fire, ref fireKind, ref fireControl)
                | CaptureBinding(overrideAction, ref overrideKind, ref overrideControl)
                | CaptureBinding(broadcast, ref broadcastKind, ref broadcastControl)
                | CaptureBinding(shop, ref shopKind, ref shopControl);
            if (hasSnapshot && next.Equals(snapshot) && !bindingsChanged)
            {
                return;
            }

            snapshot = next;
            hasSnapshot = true;
            surface.SetBody(BuildBody(next));
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

        private string BuildBody(in ZombiesHudSnapshot value)
        {
            var text = new StringBuilder(256);
            text.Append(PhaseLabel(value));
            if (value.Seconds > 0)
            {
                text.Append("  //  ").Append(value.Seconds.ToString(CultureInfo.InvariantCulture)).Append('s');
            }

            text.Append("\nWAVE  ").Append(value.Wave.ToString(CultureInfo.InvariantCulture))
                .Append("    HOSTILES  ").Append(value.Hostiles.ToString(CultureInfo.InvariantCulture));
            if (value.Pending > 0)
            {
                text.Append(" +").Append(value.Pending.ToString(CultureInfo.InvariantCulture));
            }

            if (value.Allies > 0)
            {
                text.Append("    ALLIES  ").Append(value.Allies.ToString(CultureInfo.InvariantCulture));
            }

            text.Append("\nINTEGRITY  ").Append(value.Integrity.ToString(CultureInfo.InvariantCulture))
                .Append(" / ").Append(value.MaximumIntegrity.ToString(CultureInfo.InvariantCulture))
                .Append("    SCORE  ").Append(value.Score.ToString(CultureInfo.InvariantCulture));
            if (value.ComboMultiplier > 1)
            {
                text.Append("  x").Append(value.ComboMultiplier.ToString(CultureInfo.InvariantCulture));
            }

            if (value.ShopEnabled)
            {
                text.Append("\nCREDITS  ").Append(value.Credits.ToString(CultureInfo.InvariantCulture));
            }

            if (value.UplinkAvailable)
            {
                text.Append(value.ShopEnabled ? "    UPLINK  " : "\nUPLINK  ")
                    .Append(value.UplinkCharges.ToString(CultureInfo.InvariantCulture))
                    .Append(" / ").Append(value.MaximumUplinkCharges.ToString(CultureInfo.InvariantCulture));
            }
            if (value.ChargePercent > 0)
            {
                text.Append("\nZAPPER CHARGE  ").Append(value.ChargePercent.ToString(CultureInfo.InvariantCulture)).Append('%');
            }

            text.Append("\n").Append(BindingLabel(fireKind, fireControl)).Append(" FIRE");
            if (value.UplinkAvailable)
            {
                text.Append("    ").Append(BindingLabel(overrideKind, overrideControl)).Append(" JACK IN")
                    .Append("    ").Append(BindingLabel(broadcastKind, broadcastControl)).Append(" STAND DOWN");
            }
            if (value.ShopAvailable)
            {
                text.Append("    ").Append(BindingLabel(shopKind, shopControl)).Append(" REQUISITIONS");
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
                    return value.ShopAvailable ? "WAVE CLEAR // REQUISITIONS OPEN" : "WAVE CLEAR";
                case ZombiesPhase.GameOver:
                    return "SYSTEM FAILURE";
                case ZombiesPhase.ReturningToMenu:
                    return "RETURNING TO MENU";
                default:
                    return "HORDE ACTIVE";
            }
        }

        private static bool CaptureBinding(
            IInputAction? action,
            ref InputBindingKind kind,
            ref string control)
        {
            var nextKind = default(InputBindingKind);
            var nextControl = string.Empty;
            if (action != null && action.Bindings.Count > 0)
            {
                nextKind = action.Bindings[0].Kind;
                nextControl = action.Bindings[0].Control ?? string.Empty;
            }

            if (nextKind == kind && string.Equals(nextControl, control, StringComparison.Ordinal))
            {
                return false;
            }

            kind = nextKind;
            control = nextControl;
            return true;
        }

        private static string BindingLabel(InputBindingKind kind, string control)
        {
            if (string.IsNullOrEmpty(control))
            {
                return "UNBOUND";
            }

            switch (kind)
            {
                case InputBindingKind.MouseButton:
                    return "MOUSE " + control.ToUpperInvariant();
                case InputBindingKind.GamepadButton:
                    return "PAD " + control.ToUpperInvariant();
                default:
                    return control.ToUpperInvariant();
            }
        }
    }
}
