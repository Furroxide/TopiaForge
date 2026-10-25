using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private readonly List<RemoteUiSurface> surfaces = new List<RemoteUiSurface>();
        private readonly List<RemoteUiModal> modals = new List<RemoteUiModal>();
        private readonly Dictionary<ulong, ReceivedUi> receivedUi = new Dictionary<ulong, ReceivedUi>();
        private readonly Dictionary<ParticipantId, UiReplyBudget> uiReplies = new Dictionary<ParticipantId, UiReplyBudget>();
        private readonly Dictionary<ParticipantId, UiSendBudget> uiSends = new Dictionary<ParticipantId, UiSendBudget>();
        private TokenBucket hostUiBudget = new TokenBucket(65536, 32768, 0);
        private TokenBucket hostUiCount = new TokenBucket(32, 20, 0);
        private ulong nextUiToken;

        private OperationResult<T>? CheckUi<T>(PlayerContext context, ParticipantId player) where T : notnull
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!Present(player) || context.Lifetime.IsStopping) return Gone<T>();
            if (!IsHost && !IsLocal(player)) return Authority<T>();
            return null;
        }
        private IPlayer Target(PlayerContext context, ParticipantId id)
        {
            // A private directory records exactly the incarnation to which this UI was sent.
            var directory = context.Directory ?? throw new InvalidOperationException("Read the player's directory before targeting UI.");
            if (!directory.TryGet(id, out var target)) throw new InvalidOperationException("The player left.");
            return target!;
        }
        internal OperationResult<IUiSurface> Surface(PlayerContext context, ParticipantId player, UiSurfaceRequest request)
        {
            var refused = CheckUi<IUiSurface>(context, player);
            if (refused != null) return refused;
            if (IsLocal(player)) return context.UiService.CreateSurface(request);
            if (!context.Networked) return Unshared<IUiSurface>();
            if (request.Id.Length > 128 || !Finite(request.Width) || !Finite(request.Height) || request.Width < 80 || request.Width > 1920 || request.Height < 80 || request.Height > 1440)
                return OperationResult<IUiSurface>.Failure(ModErrorCode.InvalidArgument, "Use a short surface id and a window between 80 and 1920 units wide and 80 and 1440 units high.");
            if (UiFull(player)) return Limit<IUiSurface>();
            var surface = new RemoteUiSurface(this, context, Target(context, player), ++nextUiToken, request);
            var sent = SendSurface(surface);
            if (!sent.Succeeded) return OperationResult<IUiSurface>.Failure(sent.ErrorCode, sent.ErrorMessage);
            surfaces.Add(surface);
            return OperationResult<IUiSurface>.Success(surface);
        }
        internal OperationResult<IUiModal> Modal(PlayerContext context, ParticipantId player, UiModalRequest request, Action<bool> completed)
        {
            if (completed == null) throw new ArgumentNullException(nameof(completed));
            var refused = CheckUi<IUiModal>(context, player);
            if (refused != null) return refused;
            if (IsLocal(player)) return context.UiService.ShowModal(request, completed);
            if (!context.Networked) return Unshared<IUiModal>();
            if (UiFull(player)) return Limit<IUiModal>();
            var modal = new RemoteUiModal(context, Target(context, player), ++nextUiToken, request, completed);
            var sent = SendModal(modal);
            if (!sent.Succeeded) return OperationResult<IUiModal>.Failure(sent.ErrorCode, sent.ErrorMessage);
            modals.Add(modal);
            context.Modals.Add(modal);
            return OperationResult<IUiModal>.Success(modal);
        }
        private OperationResult<bool> SendModal(RemoteUiModal modal)
        {
            try
            {
                var request = modal.Request;
                BeginUi(11, modal.Context, modal.Target.Id, modal.Token, 1);
                UiNodeWireCodec.Text(Writer, request.Title); UiNodeWireCodec.Text(Writer, request.Body);
                UiNodeWireCodec.Text(Writer, request.ConfirmLabel); UiNodeWireCodec.Text(Writer, request.CancelLabel);
                Writer.WriteByte(request.Destructive ? (byte)1 : (byte)0);
                var sent = SendUi(modal.Target.Id);
                if (sent.Succeeded) modal.Stream = Channel?.GetGeneration(modal.Target.Id) ?? 0;
                return sent;
            }
            catch (ArgumentException) { return Limit<bool>(); }
            catch (InvalidOperationException) { return Limit<bool>(); }
        }
        internal OperationResult<bool> Toast(PlayerContext context, ParticipantId player, string message, UiTone tone)
        {
            var refused = CheckUi<bool>(context, player);
            if (refused != null) return refused;
            // The local player's toast is the ordinary one, with its rules; the remote limit applies to the wire alone.
            if (IsLocal(player)) return context.UiService.ShowToast(message, tone);
            if (!context.Networked) return Unshared<bool>();
            if (string.IsNullOrWhiteSpace(message) || message.Length > 512 || (uint)tone > (uint)UiTone.Danger)
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "A toast for another player needs text of at most 512 characters and one of the UiTone values.");
            BeginUi(12, context, player, 0, 0);
            Writer.WriteString(message); Writer.WriteByte((byte)tone);
            return SendUi(player);
        }
        internal OperationResult<bool> SendSurface(RemoteUiSurface surface)
        {
            try
            {
                var request = surface.Request;
                BeginUi(10, surface.Context, surface.Target.Id, surface.Token, surface.Revision);
                UiNodeWireCodec.Text(Writer, request.Id); UiNodeWireCodec.Text(Writer, request.Title); UiNodeWireCodec.Text(Writer, surface.Body);
                Writer.WriteByte((byte)request.Kind); Writer.WriteSingle(request.Width); Writer.WriteSingle(request.Height);
                Writer.WriteByte((byte)request.HudAnchor); Writer.WriteByte((byte)request.HudVisibility);
                Writer.WriteByte(surface.Visible ? (byte)1 : (byte)0);
                UiNodeWireCodec.Write(Writer, surface.Content);
                var result = SendUi(surface.Target.Id);
                if (result.Succeeded) { surface.Dirty = false; surface.Stream = Channel?.GetGeneration(surface.Target.Id) ?? 0; }
                return result;
            }
            catch (ArgumentException) { return Limit<bool>(); }
            catch (InvalidOperationException) { return Limit<bool>(); }
        }
        private void BeginUi(byte kind, PlayerContext context, ParticipantId player, ulong token, ulong revision)
        {
            Begin(kind, context.Mod); Writer.WriteString(player.Value); Writer.WriteVarUInt(token); Writer.WriteVarUInt(revision);
        }
        private static OperationResult<T> Limit<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.RateLimited, "This player's UI is full or too large; close a surface or use a smaller composition.");

        private bool UiFull(ParticipantId player)
        {
            if (surfaces.Count + modals.Count >= 256) return true;
            var count = 0;
            for (var i = 0; i < surfaces.Count; i++) if (surfaces[i].Target.Id.Equals(player)) count++;
            for (var i = 0; i < modals.Count; i++) if (modals[i].Target.Id.Equals(player)) count++;
            return count >= 64;
        }
        private OperationResult<bool> SendUi(ParticipantId player)
        {
            if (!uiSends.TryGetValue(player, out var budget)) uiSends.Add(player, budget = new UiSendBudget(Now));
            if (!budget.Count.TryTake(Now) || !budget.Bytes.TryTake(Now, Writer.Length))
                return OperationResult<bool>.Failure(ModErrorCode.RateLimited, "This player's UI updates are too frequent; combine updates or reduce their size.");
            return Send(player);
        }
        private sealed class UiSendBudget
        {
            internal readonly TokenBucket Count, Bytes;
            internal UiSendBudget(long now) { Count = new TokenBucket(32, 20, now); Bytes = new TokenBucket(65536, 32768, now); }
        }
        private sealed class UiReplyBudget
        {
            internal readonly TokenBucket Controls, Cancellations;
            internal UiReplyBudget(long now)
            {
                Controls = new TokenBucket(32, 20, now);
                // Cleanup may cancel all 64 open modals at once, followed by the host's 32-request send burst.
                // Refill matches the host's UI send rate; cancellation remains bounded independently of clicks.
                Cancellations = new TokenBucket(96, 20, now);
            }
        }

        private void ReconcileUi()
        {
            for (var i = surfaces.Count - 1; i >= 0; i--)
            {
                var s = surfaces[i];
                if (!s.Target.IsPresent || s.Context.Lifetime.IsStopping) { s.Closed = true; s.Dirty = true; }
                if (s.Closed)
                {
                    if (!Present(s.Target.Id) || CloseRemote(s.Context, s.Target.Id, s.Token)) surfaces.RemoveAt(i);
                }
                else if (s.Dirty || s.Stream != (Channel?.GetGeneration(s.Target.Id) ?? 0)) SendSurface(s);
            }
            for (var i = modals.Count - 1; i >= 0; i--)
            {
                var m = modals[i];
                if (m.Completed != null && (!m.Target.IsPresent || m.Context.Lifetime.IsStopping))
                {
                    // A player who left cannot answer, and a stopping scope may wait for native work before it disposes
                    // anything: either way the modal reports a cancellation now, as a local one does when it closes.
                    m.Cancel();
                    if (i >= modals.Count || !ReferenceEquals(modals[i], m))
                    {
                        i = Math.Min(i, modals.Count);
                        continue;
                    }
                }
                if (!m.IsOpen && (!Present(m.Target.Id) || CloseRemote(m.Context, m.Target.Id, m.Token))) modals.RemoveAt(i);
                else if (m.IsOpen && m.Stream != (Channel?.GetGeneration(m.Target.Id) ?? 0)) SendModal(m);
            }
            ReleaseReceivedUi(null);
        }

        /// <summary>Runs a modal's answer as the local UI kit does: one failing subscriber is logged against its mod and stops nothing.</summary>
        internal void Complete(PlayerContext context, Action<bool>? completed, bool answer)
        {
            if (completed == null) return;
            foreach (var subscriber in completed.GetInvocationList())
            {
                try { ((Action<bool>)subscriber)(answer); }
                catch (Exception exception) { ReportCallback(context, "modal completion", exception); }
            }
        }

        internal void Dismiss(PlayerContext context, Action? dismissed)
        {
            if (dismissed == null) return;
            foreach (var subscriber in dismissed.GetInvocationList())
            {
                try { ((Action)subscriber)(); }
                catch (Exception exception) { ReportCallback(context, "window dismissal", exception); }
            }
        }

        private static void ReportCallback(PlayerContext context, string what, Exception exception)
        {
            try { context.Logger.Error(exception, "A mod UI " + what + " callback failed."); }
            catch (Exception) { /* A failing log sink cannot stop the frame stage. */ }
        }
        private bool CloseRemote(PlayerContext context, ParticipantId player, ulong token)
        { BeginUi(13, context, player, token, 0); return Send(player).Succeeded; }
        internal void ReleaseUi(PlayerContext context)
        {
            for (var i = 0; i < surfaces.Count; i++) if (surfaces[i].Context == context) surfaces[i].Dispose();
            // Take removes ownership before invoking the callback, so reentrant disposal is safe, including during reset.
            while (context.Modals.Count != 0) context.Modals[context.Modals.Count - 1].Dispose();
            ReleaseReceivedUi(context);
        }
        private void ReleaseReceivedUi(PlayerContext? context)
        {
            List<ulong>? removed = null;
            foreach (var pair in receivedUi)
                if (context == null ? pair.Value.Context.Lifetime.IsStopping : pair.Value.Context == context)
                    (removed ??= new List<ulong>()).Add(pair.Key);
            if (removed == null) return;
            for (var i = 0; i < removed.Count; i++)
            {
                if (!receivedUi.TryGetValue(removed[i], out var ui)) continue;
                // Unlike a session reset, scope cleanup owes a cancellation to the still-connected host. Complete
                // before marking the received UI disposed; the callback may remove its own dictionary entry.
                ui.Modal?.Dispose();
                ui.Dispose();
                receivedUi.Remove(removed[i]);
            }
        }
        private void ResetUi()
        {
            foreach (var surface in surfaces) { surface.Closed = true; surface.Dirty = false; }
            var cancelled = modals.Count == 0 ? Array.Empty<RemoteUiModal>() : modals.ToArray();
            surfaces.Clear(); modals.Clear();
            foreach (var ui in receivedUi.Values) ui.Dispose();
            receivedUi.Clear(); uiReplies.Clear(); uiSends.Clear();
            hostUiBudget = new TokenBucket(65536, 32768, Now); hostUiCount = new TokenBucket(32, 20, Now);
            // The session that asked has ended, so every open modal reports a cancellation, once the lists are clear.
            for (var i = 0; i < cancelled.Length; i++) cancelled[i].Cancel();
        }
        private sealed class ReceivedUi : IDisposable
        {
            internal readonly PlayerContext Context;
            internal ReceivedUi(PlayerContext context) { Context = context; }
            internal IUiSurface? Surface;
            internal IUiModal? Modal;
            internal ulong Revision;
            internal string Body = "";
            internal bool Disposed;
            public void Dispose() { Disposed = true; Surface?.Dispose(); Modal?.Dispose(); }
        }
    }
}
