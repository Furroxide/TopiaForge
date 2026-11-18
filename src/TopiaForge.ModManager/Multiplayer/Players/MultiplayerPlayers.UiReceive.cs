using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private bool ReceiveUi(string owner, PlayerContext? context, ParticipantId sender, byte kind, WorldWireReader r)
        {
            if (kind == 14 || kind == 15) return context != null && ReceiveReply(context, sender, kind, r);
            if (kind == 20) return ReceiveColumn(context, sender, r);
            if (!FromHost(sender) || kind < 10 || kind > 13) return false;
            if (kind != 13 && (!hostUiCount.TryTake(Now) || !hostUiBudget.TryTake(Now, r.Remaining))) return false;
            var target = UiNodeWireCodec.Text(r, 128);
            var token = UiNodeWireCodec.Id(r);
            var revision = UiNodeWireCodec.Id(r);
            if (target != Snapshot?.LocalParticipantId?.Value) return false;
            if (kind == 13)
            {
                UiNodeWireCodec.End(r);
                if (receivedUi.TryGetValue(token, out var removed))
                {
                    if (removed.Owner != owner) return false;
                    receivedUi.Remove(token); removed.Dispose();
                    targetedGenerations.Commit(token, null);
                }
                else if (context == null) return false;
                return true;
            }
            if (kind == 12)
            {
                if (context == null) return false;
                var text = UiNodeWireCodec.Text(r, 512); var tone = (UiTone)UiNodeWireCodec.Byte(r, 3); UiNodeWireCodec.End(r);
                if (context.UiService is IRemoteUiReceiver plain) plain.RemoteToast(text, tone);
                else context.UiService.ShowToast(text, tone);
                return true;
            }
            if (token == 0 || revision == 0) return false;
            if (!receivedUi.TryGetValue(token, out var ui))
            {
                if (receivedUi.Count >= 64) return false;
                ui = new ReceivedUi(owner, context);
            }
            else if (ui.Owner != owner || (!ui.PresentationOwned && ui.Context != context) || ui.Disposed) return false;
            if (kind == 11)
            {
                if (context == null) return false;
                var request = new UiModalRequest(UiNodeWireCodec.Text(r), UiNodeWireCodec.Text(r), UiNodeWireCodec.Text(r), UiNodeWireCodec.Text(r), UiNodeWireCodec.Byte(r, 1) != 0);
                UiNodeWireCodec.End(r);
                if (receivedUi.ContainsKey(token)) return false;
                Action<bool> completed = answer =>
                {
                    if (ui.Disposed) return;
                    receivedUi.Remove(token); ui.Disposed = true;
                    if (context.Lifetime.IsStopping) answer = false;
                    // Teardown still owes the host a cancellation, even after this recipient's scope starts stopping.
                    Reply(context, sender, token, revision, "", answer ? "1" : "0", 14, allowStopping: !answer);
                };
                var result = context.UiService is IRemoteUiReceiver plain ? plain.RemoteModal(request, completed) : context.UiService.ShowModal(request, completed);
                if (!result.TryGetValue(out var modal)) return false;
                ui.Modal = modal; ui.Revision = revision; receivedUi.Add(token, ui);
                return true;
            }
            var id = UiNodeWireCodec.Text(r, 128); var title = UiNodeWireCodec.Text(r); var body = UiNodeWireCodec.Text(r);
            var surfaceKind = (UiSurfaceKind)UiNodeWireCodec.Byte(r, 2);
            var width = UiNodeWireCodec.Number(r); var height = UiNodeWireCodec.Number(r);
            var anchor = (UiHudAnchor)UiNodeWireCodec.Byte(r, 8); var visibility = (UiHudVisibility)UiNodeWireCodec.Byte(r, 1);
            var visible = UiNodeWireCodec.Byte(r, 1) != 0;
            if (width < 80 || width > 1920 || height < 80 || height > 1440) return false;
            var content = UiNodeWireCodec.Read(r, (control, value) =>
            {
                if (!ui.Disposed && ui.Surface?.IsVisible == true && ui.Revision == revision)
                    Reply(owner, ui.PresentationOwned ? null : ui.Context?.Lifetime, sender, token, revision, control, value, 14);
            }, UiClockEpoch, out var envelope);
            UiNodeWireCodec.End(r);
            var theme = default(UiRemoteThemeSelection);
            if (envelope == null && context == null && !ui.PresentationOwned) return false;
            if (envelope != null && (!AuthenticateUiTheme(envelope, owner, out theme) || !targetedGenerations.Allows(token, envelope))) return false;
            var surfaceRequest = new UiSurfaceRequest("remote-" + token, title, body, surfaceKind, width, height, content);
            if (surfaceKind == UiSurfaceKind.Hud) surfaceRequest = surfaceRequest.WithHudAnchor(anchor).WithHudVisibility(visibility);
            if (ui.Modal != null || revision < ui.Revision ||
                (revision == ui.Revision && !UiPresentationTheme.SameEnvelope(ui.Envelope, envelope))) return false;
            if (ui.Surface == null)
            {
                var receiver = envelope != null ? Presentation?.UiReceiver : context?.UiService;
                if (receiver == null) return false;
                var created = UiPresentationTheme.CreateRemote(receiver, surfaceRequest, theme);
                if (!created.TryGetValue(out var surface)) return false;
                ui.Surface = surface; receivedUi.Add(token, ui);
                if (surface is IUiSurfaceDismissalSource dismissal) dismissal.Dismissed += () =>
                {
                    // A surface raises Dismissed for any hide, so only a close while the host shows the window is the player's.
                    // Answering the host's own hide would close the window again once the host shows it, whatever its revision.
                    if (!ui.Disposed && ui.Shown)
                        Reply(owner, ui.PresentationOwned ? null : ui.Context?.Lifetime, sender, token, ui.Revision, "", "", 15);
                };
            }
            else
            {
                if (revision != ui.Revision && !UiPresentationTheme.UpdateRemote(ui.Surface, content, theme, ui.Envelope != null)) return false;
                if (body != ui.Body) ui.Surface.SetBody(body);
            }
            targetedGenerations.Commit(token, envelope);
            ui.Body = body; ui.Revision = revision; ui.Shown = visible; ui.Envelope = envelope;
            ui.PresentationOwned |= envelope != null;
            if (visible) ui.Surface.Show(); else ui.Surface.Hide();
            return true;
        }

        private void Reply(PlayerContext context, ParticipantId host, ulong token, ulong revision, string control, string value, byte kind, bool allowStopping = false)
            => Reply(context.Mod, context.Lifetime, host, token, revision, control, value, kind, allowStopping);

        private void Reply(string owner, IModLifetime? lifetime, ParticipantId host, ulong token, ulong revision,
            string control, string value, byte kind, bool allowStopping = false)
        {
            if (IsHost || (lifetime?.IsStopping == true && !allowStopping) || control.Length > 128 || value.Length > 4096) return;
            Begin(kind, owner); Writer.WriteVarUInt(token); Writer.WriteVarUInt(revision);
            Writer.WriteString(control); Writer.WriteString(value);
            Send(host);
        }

        private bool ReceiveReply(PlayerContext context, ParticipantId sender, byte kind, WorldWireReader r)
        {
            if (!IsHost || !Present(sender)) return false;
            var token = UiNodeWireCodec.Id(r); var revision = UiNodeWireCodec.Id(r);
            var control = UiNodeWireCodec.Text(r, 128); var value = UiNodeWireCodec.Text(r); UiNodeWireCodec.End(r);
            if (!uiReplies.TryGetValue(sender, out var budget)) uiReplies.Add(sender, budget = new UiReplyBudget(Now));
            var cancelling = kind == 14 && revision == 1 && control.Length == 0 && value == "0";
            if (!(cancelling ? budget.Cancellations : budget.Controls).TryTake(Now)) return false;
            for (var i = 0; i < surfaces.Count; i++)
            {
                var surface = surfaces[i];
                if (surface.Token != token || surface.Context.Mod != context.Mod) continue;
                if (!surface.Target.Id.Equals(sender) || !surface.Target.IsPresent || !surface.IsVisible) return false;
                // An author's callback that throws is the author's failure, logged as the local UI kit logs it, never a
                // refused reply and never the end of this frame's other replies.
                if (kind == 15)
                {
                    // Revisions retire controls, not the player's choice to close the same window. A content update
                    // can cross that dismissal in flight; accept its earlier revision, never an unseen future one.
                    if (revision == 0 || revision > surface.Revision) return false;
                    using (runtime.Intents.EnterForwarded(context.Mod, IntentSource.UiCallback, sender.Value))
                    {
                        try { surface.Hide(); }
                        catch (Exception exception) { ReportCallback(context, "window dismissal", exception); }
                    }
                    return true;
                }
                if (surface.Revision != revision) return false;
                using (runtime.Intents.EnterForwarded(context.Mod, IntentSource.UiCallback, sender.Value))
                {
                    try { return UiNodeWireCodec.Invoke(surface.Content, control, value); }
                    catch (Exception exception) { ReportCallback(context, "window control '" + control + "'", exception); return true; }
                }
            }
            for (var i = 0; i < modals.Count; i++)
            {
                var modal = modals[i];
                if (modal.Token != token || modal.Context.Mod != context.Mod) continue;
                if (!modal.Target.Id.Equals(sender) || !modal.IsOpen || revision != 1 || kind != 14 || control.Length != 0 || (value != "0" && value != "1")) return false;
                var callback = modal.Take();
                modals.RemoveAt(i);
                using (runtime.Intents.EnterForwarded(context.Mod, IntentSource.UiCallback, sender.Value)) Complete(context, callback, value == "1");
                return true;
            }
            return false;
        }
    }

    internal interface IRemoteUiReceiver
    {
        OperationResult<IUiSurface> RemoteSurface(UiSurfaceRequest request);
        OperationResult<IUiModal> RemoteModal(UiModalRequest request, Action<bool> completed);
        OperationResult<bool> RemoteToast(string message, UiTone tone);
    }
}
