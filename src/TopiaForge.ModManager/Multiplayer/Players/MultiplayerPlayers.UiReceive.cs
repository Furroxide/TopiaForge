using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        private bool ReceiveUi(PlayerContext context, ParticipantId sender, byte kind, WorldWireReader r)
        {
            if (kind == 14 || kind == 15) return ReceiveReply(context, sender, kind, r);
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
                    if (removed.Context.Mod != context.Mod) return false;
                    receivedUi.Remove(token); removed.Dispose();
                }
                return true;
            }
            if (kind == 12)
            {
                var text = UiNodeWireCodec.Text(r, 512); var tone = (UiTone)UiNodeWireCodec.Byte(r, 3); UiNodeWireCodec.End(r);
                if (context.UiService is IRemoteUiReceiver plain) plain.RemoteToast(text, tone);
                else context.UiService.ShowToast(text, tone);
                return true;
            }
            if (token == 0 || revision == 0) return false;
            if (!receivedUi.TryGetValue(token, out var ui))
            {
                if (receivedUi.Count >= 64) return false;
                ui = new ReceivedUi(context);
            }
            else if (ui.Context != context || ui.Disposed) return false;
            if (kind == 11)
            {
                var request = new UiModalRequest(UiNodeWireCodec.Text(r), UiNodeWireCodec.Text(r), UiNodeWireCodec.Text(r), UiNodeWireCodec.Text(r), UiNodeWireCodec.Byte(r, 1) != 0);
                UiNodeWireCodec.End(r);
                if (receivedUi.ContainsKey(token)) return false;
                Action<bool> completed = answer =>
                {
                    if (ui.Disposed) return;
                    Reply(context, sender, token, revision, "", answer ? "1" : "0", 14);
                    receivedUi.Remove(token); ui.Disposed = true;
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
                if (!ui.Disposed && ui.Surface?.IsVisible == true && ui.Revision == revision) Reply(context, sender, token, revision, control, value, 14);
            });
            UiNodeWireCodec.End(r);
            var surfaceRequest = new UiSurfaceRequest("remote-" + token, title, body, surfaceKind, width, height, content);
            if (surfaceKind == UiSurfaceKind.Hud) surfaceRequest = surfaceRequest.WithHudAnchor(anchor).WithHudVisibility(visibility);
            if (ui.Modal != null || revision < ui.Revision) return false;
            if (ui.Surface == null)
            {
                var created = context.UiService is IRemoteUiReceiver plain ? plain.RemoteSurface(surfaceRequest) : context.UiService.CreateSurface(surfaceRequest);
                if (!created.TryGetValue(out var surface)) return false;
                ui.Surface = surface; receivedUi.Add(token, ui);
                if (surface is IUiSurfaceDismissalSource dismissal) dismissal.Dismissed += () =>
                {
                    if (!ui.Disposed) Reply(context, sender, token, ui.Revision, "", "", 15);
                };
            }
            else
            {
                if (body != ui.Body) ui.Surface.SetBody(body);
                if (revision != ui.Revision && !ui.Surface.SetContent(content ?? new UiColumn()).Succeeded) return false;
            }
            ui.Body = body; ui.Revision = revision;
            if (visible) ui.Surface.Show(); else ui.Surface.Hide();
            return true;
        }

        private void Reply(PlayerContext context, ParticipantId host, ulong token, ulong revision, string control, string value, byte kind)
        {
            if (IsHost || context.Lifetime.IsStopping || control.Length > 128 || value.Length > 4096) return;
            Begin(kind, context.Mod); Writer.WriteVarUInt(token); Writer.WriteVarUInt(revision);
            Writer.WriteString(control); Writer.WriteString(value);
            Send(host);
        }

        private bool ReceiveReply(PlayerContext context, ParticipantId sender, byte kind, WorldWireReader r)
        {
            if (!IsHost || !Present(sender)) return false;
            var token = UiNodeWireCodec.Id(r); var revision = UiNodeWireCodec.Id(r);
            var control = UiNodeWireCodec.Text(r, 128); var value = UiNodeWireCodec.Text(r); UiNodeWireCodec.End(r);
            if (!uiReplies.TryGetValue(sender, out var budget)) uiReplies.Add(sender, budget = new TokenBucket(32, 20, Now));
            if (!budget.TryTake(Now)) return false;
            for (var i = 0; i < surfaces.Count; i++)
            {
                var surface = surfaces[i];
                if (surface.Token != token || surface.Context.Mod != context.Mod) continue;
                if (!surface.Target.Id.Equals(sender) || !surface.Target.IsPresent || !surface.IsVisible || surface.Revision != revision) return false;
                if (kind == 15) { surface.Hide(); return true; }
                using (runtime.Intents.EnterForwarded(context.Mod, IntentSource.UiCallback, sender.Value))
                    return UiNodeWireCodec.Invoke(surface.Content, control, value);
            }
            for (var i = 0; i < modals.Count; i++)
            {
                var modal = modals[i];
                if (modal.Token != token || modal.Context.Mod != context.Mod) continue;
                if (!modal.Target.Id.Equals(sender) || !modal.IsOpen || revision != 1 || kind != 14 || control.Length != 0 || (value != "0" && value != "1")) return false;
                var callback = modal.Completed;
                modal.Completed = null;
                modals.RemoveAt(i);
                using (runtime.Intents.EnterForwarded(context.Mod, IntentSource.UiCallback, sender.Value)) callback?.Invoke(value == "1");
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
