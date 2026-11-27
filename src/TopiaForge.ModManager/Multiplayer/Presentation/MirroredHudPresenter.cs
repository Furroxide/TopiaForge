using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Retains the exact scope's HUD while its local service preserves ordinary canvas behavior.</summary>
    internal sealed class MirroredHudPresenter : IUiSurface, IUiSurfaceDismissalSource
    {
        private readonly MultiplayerPresentation owner;
        internal readonly RuntimeReplication Context;
        internal readonly UiSurfaceRequest Request;
        internal readonly IUiPresentationThemeSource? ThemeSource;
        private readonly IUiService local;
        private readonly ulong token;
        private IUiSurface inner = null!;
        private string body;
        private UiNode? content;
        private UiNode? presentationContent;
        private bool shown = true, disposed;
        private ulong revision;
        internal EncodedHud? Encoded;
        internal UiThemeDescriptor? Theme;
        internal byte[]? Payload => Encoded?.Bytes;
        internal string? InvalidComposition;
        internal IDisposable? Tracking;
        internal int Bytes => (Payload?.Length ?? 0) + 4;

        internal MirroredHudPresenter(MultiplayerPresentation owner, RuntimeReplication context, UiSurfaceRequest request, ulong token,
            IUiService local, UiThemeDescriptor? theme = null)
        {
            this.owner = owner; Context = context; Request = request; this.token = token; this.local = local;
            body = request.Body; content = request.Content; ThemeSource = local as IUiPresentationThemeSource; Theme = theme;
        }
        internal void Attach(IUiSurface surface) => inner = surface;
        internal UiNode? Content => content;
        internal UiNode? PresentationContent => presentationContent;
        internal bool Prepare(UiNode? presentation, UiWireEnvelope? envelope)
        {
            presentationContent = presentation;
            Encoded = Encode(body, presentation, shown, owner.UiClockEpoch, envelope: envelope, needsTheme: Theme != null);
            return Encoded.HasValue;
        }

        internal EncodedHud? StageRetained(UiWireEnvelope? envelope, UiThemeDescriptor? theme) =>
            Encode(body, presentationContent, shown, owner.UiClockEpoch, retained: true, envelope: envelope, needsTheme: theme != null);
        internal void RefreshClockBinding(string epoch)
        {
            if (Encoded?.ClockEpoch is not string previous || previous == epoch) return;
            if (owner.Change(this, Encode(body, presentationContent, shown, epoch, retained: true,
                envelope: Encoded?.Envelope, needsTheme: Theme != null))) return;
            // Keep the host handle usable even if a changed wire encoding cannot fit the shared budget.
            owner.Remove(this); Encoded = null;
        }
        private EncodedHud? Encode(string text, UiNode? node, bool visible, string epoch, bool retained = false,
            UiWireEnvelope? envelope = null, bool needsTheme = false)
        {
            InvalidComposition = null;
            try
            {
                UiWireValidation.Validate(node);
                if (envelope == null && needsTheme) return null;
                if (!MultiplayerPlayers.Finite(Request.Width) || !MultiplayerPlayers.Finite(Request.Height) ||
                    Request.Width < 80 || Request.Width > 1920 || Request.Height < 80 || Request.Height > 1440 ||
                    UiComposition.ContainsInteractive(node))
                    throw new ArgumentException("Shared HUDs need a finite 80-1920 by 80-1440 size and read-only content.");
                var w = new WorldWireWriter(MultiplayerPresentation.MaximumBytes);
                w.Begin(0); w.WriteVarUInt(token); UiNodeWireCodec.Text(w, Request.Title);
                UiNodeWireCodec.Text(w, text); w.WriteSingle(Request.Width); w.WriteSingle(Request.Height);
                w.WriteByte((byte)Request.HudAnchor); w.WriteByte((byte)Request.HudVisibility);
                w.WriteByte(visible ? (byte)1 : (byte)0);
                var timed = UiWireCodec.Prepare(w, node, epoch, retained, envelope);
                return new EncodedHud(MultiplayerPresentation.Copy(w), timed ? epoch : null, envelope);
            }
            catch (ArgumentException exception) { InvalidComposition = exception.Message; return null; }
            catch (InvalidOperationException) { return null; }
        }
        public string Id => inner.Id;
        public bool IsVisible => !disposed && inner.IsVisible;
        internal bool IsAlive => !disposed && MultiplayerPresentation.SurfaceAlive(inner);
        public event Action? Dismissed
        {
            add { if (inner is IUiSurfaceDismissalSource source) source.Dismissed += value; }
            remove { if (inner is IUiSurfaceDismissalSource source) source.Dismissed -= value; }
        }
        private void RequireActive()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed || Context.Lifetime.IsStopping) throw new ObjectDisposedException(nameof(MirroredHudPresenter));
        }
        // A canvas destroyed outside TopiaForge leaves the shared set. As in solo, the handle stays usable and tracked
        // by its scope, and returns the local surface's own lost-state answers; only the author's Dispose or a
        // stopping scope throws.
        internal void Detach()
        {
            owner.Remove(this); Encoded = null;
        }
        private bool Lost()
        {
            if (MultiplayerPresentation.SurfaceAlive(inner)) return false;
            Detach();
            return true;
        }
        public void Show()
        {
            SetVisible(true);
        }
        public void Hide()
        {
            SetVisible(false);
        }
        private void SetVisible(bool visible)
        {
            RequireActive();
            if (shown == visible || Lost()) { shown = visible; if (visible) inner.Show(); else inner.Hide(); return; }
            var payload = Encode(body, presentationContent, visible, owner.UiClockEpoch, retained: true,
                envelope: Encoded?.Envelope, needsTheme: Theme != null);
            // A solo HUD outside the shared bounds still owns a fully usable local surface.
            if (!owner.ChangeVisibility(this, payload, false)) throw TooLarge();
            var previous = shown;
            var operation = ++revision;
            shown = visible;
            try { if (visible) inner.Show(); else inner.Hide(); }
            catch
            {
                if (!disposed && revision == operation) shown = previous;
                throw;
            }
            // Dismissed may reopen, rewrite or dispose this surface. Its newer state wins.
            if (!disposed && revision == operation) owner.ChangeVisibility(this, payload);
        }
        public void SetBody(string value)
        {
            RequireActive(); value ??= string.Empty;
            if (body == value) return;
            if (Lost()) { inner.SetBody(value); body = value; return; }
            var payload = Encode(value, presentationContent, shown, owner.UiClockEpoch, retained: true,
                envelope: Encoded?.Envelope, needsTheme: Theme != null);
            if (!owner.Change(this, payload, false)) throw TooLarge();
            inner.SetBody(value);
            if (!disposed) { body = value; revision++; owner.Change(this, payload); }
        }
        public OperationResult<bool> SetContent(UiNode value)
        {
            RequireActive();
            if (Lost()) return inner.SetContent(value);
            if (ReferenceEquals(content, value))
            {
                RefreshClockBinding(owner.UiClockEpoch);
                return inner.SetContent(value);
            }
            var validated = UiPresentationImages.Prepare(value, ThemeSource?.PresentationThemeScope, Theme, out var presentation);
            if (!validated.Succeeded) return validated;
            if (UiComposition.ContainsInteractive(presentation)) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
                "HUD surfaces are read-only; put controls in a window or a player's targeted UI.");
            validated = UiPresentationInputs.Prepare(local, presentation, out presentation);
            if (!validated.Succeeded) return validated;
            var staged = owner.StageEnvelope(this, Theme, presentation, out var envelope);
            if (!staged.Succeeded) return staged;
            var payload = Encode(body, presentation, shown, owner.UiClockEpoch, envelope: envelope, needsTheme: Theme != null);
            if (owner.Hosting && InvalidComposition != null) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, InvalidComposition);
            if (owner.Hosting && payload == null) return MultiplayerPresentation.Limit<bool>();
            if (!owner.Change(this, payload, false)) return MultiplayerPresentation.Limit<bool>();
            var result = inner.SetContent(value);
            if (result.Succeeded && !disposed)
            {
                content = value;
                presentationContent = presentation;
                revision++;
                owner.Change(this, payload);
            }
            return result;
        }
        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed) return;
            disposed = true; owner.Release(this); inner.Dispose(); Tracking?.Dispose(); Tracking = null;
        }
        private static InvalidOperationException TooLarge() => new InvalidOperationException(
            "The shared HUD is too large; shorten its content before updating it.");

        internal readonly struct EncodedHud
        {
            internal readonly byte[] Bytes;
            internal readonly string? ClockEpoch;
            internal readonly UiWireEnvelope? Envelope;
            internal EncodedHud(byte[] bytes, string? clockEpoch, UiWireEnvelope? envelope = null)
            { Bytes = bytes; ClockEpoch = clockEpoch; Envelope = envelope; }
        }
    }
}
