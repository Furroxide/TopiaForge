using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPresentation
    {
        private readonly Dictionary<ulong, ReceivedHud> received = new Dictionary<ulong, ReceivedHud>();
        private readonly UiPresentationGenerations sharedGenerations = new UiPresentationGenerations();
        private TokenBucket incomingBytes = new TokenBucket(ReceiveBurstBytes, ReceiveBytesPerSecond, 0);
        private TokenBucket incomingCount = new TokenBucket(EventBurst, EventsPerSecond, 0);
        private TokenBucket incomingBaselines = new TokenBucket(10, 10, 0);
        private ulong receivedVersion;
        private readonly List<ulong> removed = new List<ulong>();
        private List<HudState>? latestHuds;
        private long retryAt;

        internal bool Receive(ParticipantId sender, byte kind, WorldWireReader r)
        {
            if (!players.FromHost(sender) || kind < 30 || kind > UiStingerDelivery.RetireKind ||
                (kind != 30 && r.Remaining > 2304)) return false;
            // A closure is bounded by its counted send and still travels behind those events on this stream.
            if (kind == UiStingerDelivery.RetireKind) return ReceiveStingerRetirement(r);
            if (!(kind == 30 ? incomingBaselines : incomingCount).TryTake(Now) || !incomingBytes.TryTake(Now, r.Remaining)) return false;
            if (kind == UiStingerDelivery.EventKind) return ReceiveStinger(r);
            if (kind != 30) return ReceiveEffect(kind, r);
            var next = UiNodeWireCodec.Id(r);
            if (next == 0 || next <= receivedVersion || !r.TryReadInt32(MaximumSurfaces, out var count)) return false;
            var updates = new List<HudState>(count);
            var tokens = new HashSet<ulong>();
            var total = 0;
            for (var i = 0; i < count; i++)
            {
                if (!r.TryReadBytes(MaximumBytes, out var bytes)) return false;
                total += bytes.Length + 4;
                if (total > MaximumBytes) return false;
                var read = new WorldWireReader(); read.Reset(bytes);
                if (!read.TryReadHeader(out var tag) || (tag != (byte)UiSurfaceKind.Hud && tag != (byte)UiSurfaceKind.Overlay))
                    return false;
                var surfaceKind = (UiSurfaceKind)tag;
                var token = UiNodeWireCodec.Id(read);
                if (token == 0 || !tokens.Add(token)) return false;
                var title = UiNodeWireCodec.Text(read); var body = UiNodeWireCodec.Text(read);
                var width = UiNodeWireCodec.Number(read); var height = UiNodeWireCodec.Number(read);
                var anchor = (UiHudAnchor)UiNodeWireCodec.Byte(read, 8);
                var visibility = (UiHudVisibility)UiNodeWireCodec.Byte(read, 1);
                var visible = UiNodeWireCodec.Byte(read, 1) != 0;
                if (width < 80 || width > 1920 || height < 80 || height > 1440) return false;
                var content = UiNodeWireCodec.Read(read, IgnoreControl, UiClockEpoch, out var envelope);
                if (UiComposition.ContainsInteractive(content)) return false;
                var dismissible = UiWireCodec.ReadSurfaceDismissible(read, surfaceKind);
                // A shared overlay is read-only, so a request that requires an enabled button cannot be mirrored.
                if (surfaceKind == UiSurfaceKind.Overlay && (envelope == null || !dismissible)) return false;
                UiNodeWireCodec.End(read);
                var theme = default(UiRemoteThemeSelection);
                if (envelope != null && (!players.AuthenticateUiTheme(envelope, null, out theme) || !sharedGenerations.Allows(token, envelope)))
                    return false;
                if (!UiPresentationTheme.ViewerAdmits(content, theme)) return false;
                if (!KeepsSharedKind(token, surfaceKind)) return false;
                var request = new UiSurfaceRequest("shared-" + token, title, body, surfaceKind, width, height, content);
                if (surfaceKind == UiSurfaceKind.Hud) request = request.WithHudAnchor(anchor).WithHudVisibility(visibility);
                updates.Add(new HudState(token, request, visible, theme));
            }
            var audio = ReadLoops(r);
            UiNodeWireCodec.End(r);
            // Decode and validate the complete replacement before touching any existing presentation.
            sharedGenerations.ClearActive();
            for (var i = 0; i < updates.Count; i++) sharedGenerations.Commit(updates[i].Token, updates[i].Theme.Envelope);
            removed.Clear();
            foreach (var entry in received) if (!tokens.Contains(entry.Key)) removed.Add(entry.Key);
            for (var i = 0; i < removed.Count; i++) { received[removed[i]].Surface.Dispose(); received.Remove(removed[i]); }
            for (var i = 0; i < updates.Count; i++) updates[i].Retry = !TryApply(updates[i]);
            latestHuds = updates; retryAt = Now + 1_000_000;
            ApplyLoops(audio);
            receivedVersion = next;
            return true;
        }

        private bool KeepsSharedKind(ulong token, UiSurfaceKind kind)
        {
            if (latestHuds == null) return true;
            for (var index = 0; index < latestHuds.Count; index++)
                if (latestHuds[index].Token == token) return latestHuds[index].Request.Kind == kind;
            return true;
        }

        internal void RetryReceived()
        {
            if (latestHuds == null || Now < retryAt) return;
            for (var i = 0; i < latestHuds.Count; i++)
            {
                var state = latestHuds[i];
                if (state.Retry || !received.TryGetValue(state.Token, out var current) || !SurfaceAlive(current.Surface))
                    state.Retry = !TryApply(state);
            }
            retryAt = Now + 1_000_000;
        }

        internal static bool SurfaceAlive(IUiSurface surface) =>
            !(surface is IPresentationSurfaceState state) || state.IsAlive;

        private bool TryApply(HudState state)
        {
            try { return Apply(state); }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        private bool Apply(HudState state)
        {
            var request = state.Request;
            if (received.TryGetValue(state.Token, out var lost) && !SurfaceAlive(lost.Surface))
            {
                lost.Surface.Dispose(); received.Remove(state.Token);
            }
            if (!received.TryGetValue(state.Token, out var current))
            {
                var result = UiPresentationTheme.CreateRemote(Receiver.Ui, request, state.Theme);
                if (!result.TryGetValue(out var surface)) return false;
                current = new ReceivedHud(surface, request.Body); received.Add(state.Token, current);
            }
            else
            {
                if (!UiPresentationTheme.UpdateRemote(current.Surface, request.Content, state.Theme, current.Themed)) return false;
                if (current.Body != request.Body) current.Surface.SetBody(request.Body);
            }
            current.Body = request.Body;
            current.Themed = state.Theme.Envelope != null;
            if (state.Visible) current.Surface.Show(); else current.Surface.Hide();
            return true;
        }

        private void ClearReceived()
        {
            foreach (var hud in received.Values) hud.Surface.Dispose();
            received.Clear(); receivedVersion = 0; latestHuds = null;
            sharedGenerations.Reset();
            incomingBytes = new TokenBucket(ReceiveBurstBytes, ReceiveBytesPerSecond, Now);
            incomingCount = new TokenBucket(EventBurst, EventsPerSecond, Now);
            incomingBaselines = new TokenBucket(10, 10, Now);
        }
        private static void IgnoreControl(string control, string value) { }
        private sealed class ReceivedHud
        {
            internal readonly IUiSurface Surface;
            internal string Body;
            internal bool Themed;
            internal ReceivedHud(IUiSurface surface, string body) { Surface = surface; Body = body; }
        }
        private sealed class HudState
        {
            internal readonly ulong Token;
            internal readonly UiSurfaceRequest Request;
            internal readonly bool Visible;
            internal readonly UiRemoteThemeSelection Theme;
            internal bool Retry;
            internal HudState(ulong token, UiSurfaceRequest request, bool visible, UiRemoteThemeSelection theme)
            { Token = token; Request = request; Visible = visible; Theme = theme; }
        }
    }
}
