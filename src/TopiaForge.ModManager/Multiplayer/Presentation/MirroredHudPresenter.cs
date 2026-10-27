using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>Retains the exact scope's HUD while its local service preserves ordinary canvas behavior.</summary>
    internal sealed class MirroredHudPresenter : IUiSurface, IUiSurfaceDismissalSource
    {
        private readonly MultiplayerPresentation owner;
        internal readonly RuntimeReplication Context;
        private readonly UiSurfaceRequest request;
        private readonly ulong token;
        private IUiSurface inner = null!;
        private string body;
        private UiNode? content;
        private bool shown = true, disposed;
        private ulong revision;
        internal byte[]? Payload;
        internal IDisposable? Tracking;
        internal int Bytes => (Payload?.Length ?? 0) + 4;

        internal MirroredHudPresenter(MultiplayerPresentation owner, RuntimeReplication context, UiSurfaceRequest request, ulong token)
        { this.owner = owner; Context = context; this.request = request; this.token = token; body = request.Body; content = request.Content; }
        internal void Attach(IUiSurface surface) => inner = surface;
        internal bool Prepare(string text, UiNode? node, bool visible)
        { Payload = Encode(text, node, visible); return Payload != null; }
        private byte[]? Encode(string text, UiNode? node, bool visible)
        {
            try
            {
                if (!MultiplayerPlayers.Finite(request.Width) || !MultiplayerPlayers.Finite(request.Height) ||
                    request.Width < 80 || request.Width > 1920 || request.Height < 80 || request.Height > 1440 ||
                    UiComposition.ContainsInteractive(node)) return null;
                var w = new WorldWireWriter(MultiplayerPresentation.MaximumBytes);
                w.Begin(0); w.WriteVarUInt(token); UiNodeWireCodec.Text(w, request.Title);
                UiNodeWireCodec.Text(w, text); w.WriteSingle(request.Width); w.WriteSingle(request.Height);
                w.WriteByte((byte)request.HudAnchor); w.WriteByte((byte)request.HudVisibility);
                w.WriteByte(visible ? (byte)1 : (byte)0); UiNodeWireCodec.Write(w, node);
                return MultiplayerPresentation.Copy(w);
            }
            catch (ArgumentException) { return null; }
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
            if (shown == visible) { if (visible) inner.Show(); else inner.Hide(); return; }
            var payload = Encode(body, content, visible);
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
            var payload = Encode(value, content, shown);
            if (!owner.Change(this, payload, false)) throw TooLarge();
            inner.SetBody(value);
            if (!disposed) { body = value; revision++; owner.Change(this, payload); }
        }
        public OperationResult<bool> SetContent(UiNode value)
        {
            RequireActive();
            if (ReferenceEquals(content, value)) return OperationResult<bool>.Success(true);
            if (UiComposition.ContainsInteractive(value)) return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
                "HUD surfaces are read-only; put controls in a window or a player's targeted UI.");
            var payload = Encode(body, value, shown);
            if (owner.Hosting && payload == null) return MultiplayerPresentation.Limit<bool>();
            if (!owner.Change(this, payload, false)) return MultiplayerPresentation.Limit<bool>();
            var result = inner.SetContent(value);
            if (result.Succeeded && !disposed) { content = value; revision++; owner.Change(this, payload); }
            return result;
        }
        public void Dispose()
        {
            UnityMainThreadGuard.AssertCurrent();
            if (disposed) return;
            disposed = true; owner.Remove(this); inner.Dispose(); Tracking?.Dispose(); Tracking = null;
        }
        private static InvalidOperationException TooLarge() => new InvalidOperationException(
            "The shared HUD is too large; shorten its content before updating it.");
    }
}
