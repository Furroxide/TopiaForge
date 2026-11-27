using System;
using System.Collections.Generic;
using System.Diagnostics;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// Owns the Chat channel, even when its view is closed. Only authenticated, connected players send requests; the host
    /// assigns their identity, enforces the same rate independently, checks moderation again at relay, and echoes accepted
    /// messages. A received message is always text, never a command. Nothing here logs or persists conversation content.
    /// </summary>
    internal sealed class SessionChat : IDisposable
    {
        private static readonly Stopwatch Monotonic = Stopwatch.StartNew();
        private readonly Queue<ChatLine> lines = new Queue<ChatLine>();
        private readonly Dictionary<MultiplayerPlayerKey, ChatRateLimit> rates = new Dictionary<MultiplayerPlayerKey, ChatRateLimit>();
        private readonly List<ParticipantId> skipped = new List<ParticipantId>();
        private IInternalMultiplayerControl? control;
        private IInternalSystemChannel? channel;
        private MultiplayerSessionSnapshot? snapshot;
        private MultiplayerSessionId sessionId;
        private InternalSessionRole role;
        private ChatRateLimit sendRate;

        internal Func<long> Clock { get; set; } = () => Monotonic.Elapsed.Ticks / 10;
        internal int Revision { get; private set; }
        internal bool IsAvailable => channel?.IsOpen == true && role != InternalSessionRole.Solo && snapshot?.LocalParticipantId != null;

        internal void Tick(IInternalMultiplayerControl? next, MultiplayerSessionSnapshot? current)
        {
            if (!ReferenceEquals(control, next))
            {
                Dispose();
                control = next;
                if (next != null && next.OpenSystemChannel(InternalSystemChannelKind.Chat).TryGetValue(out var opened)) channel = opened;
            }
            var nextRole = next?.Role ?? InternalSessionRole.Solo;
            if (nextRole != role || (current != null && !current.Id.Equals(sessionId)))
            {
                role = nextRole;
                sessionId = current?.Id ?? default;
                Reset();
            }
            snapshot = current;
            if (channel == null) return;
            // The provider's inbox is bounded too; this frame never spends unbounded time decoding a hostile stream.
            for (var count = 0; count < 64 && channel.TryReceive(out var message); count++)
            {
                if (!IsAvailable) continue;
                if (role == InternalSessionRole.ListenHost) ReceiveRequest(message);
                else ReceiveRelay(message);
            }
        }

        internal string Send(string text)
        {
            if (!IsAvailable) return "Chat is unavailable. Join a session and try again.";
            if (!ChatWire.IsValid(text)) return "Write a message of 1 to 256 characters.";
            var local = Find(snapshot!.LocalParticipantId!.Value);
            if (local == null || !local.IsConnected) return "Chat is reconnecting. Try again once the link returns.";
            var safe = HostText.ForDisplay(text, ChatWire.MaximumCharacters);
            if (safe.Length == 0) return "Write a message of 1 to 256 characters.";
            // Only a message that leaves spends the allowance: a refusal above, or a send the link refuses, costs nothing.
            var allowance = sendRate;
            if (!allowance.Take(Clock())) return "Chat is sending too quickly. Wait two seconds and try again.";
            if (role == InternalSessionRole.ListenHost)
            {
                if (!Relay(local, safe)) return "The message could not be sent. Wait for the connection and try again.";
            }
            else
            {
                var result = channel!.Send(new ArraySegment<byte>(ChatWire.Write(safe)), InternalSystemTarget.Host);
                if (!result.Succeeded) return "The message could not be sent. Wait for the connection and try again.";
            }
            sendRate = allowance;
            return string.Empty;
        }

        private void ReceiveRequest(InternalSystemMessage message)
        {
            var sender = Find(message.Sender);
            if (sender == null || sender.IsHost || !sender.IsConnected || !sender.PlayerKey.IsValid || control!.IsServerMuted(sender.Id)) return;
            if (!ChatWire.TryRead(message.Payload, false, out _, out var text)) return;
            if (!rates.TryGetValue(sender.PlayerKey, out var rate) && !MakeRoom(ChatRateLimit.HostSlack)) return;
            var allowed = rate.Take(Clock(), ChatRateLimit.HostSlack);
            rates[sender.PlayerKey] = rate;
            if (!allowed) return;
            Relay(sender, HostText.ForDisplay(text, ChatWire.MaximumCharacters));
        }

        private void ReceiveRelay(InternalSystemMessage message)
        {
            if (!snapshot!.HostParticipantId.HasValue || !message.Sender.Equals(snapshot.HostParticipantId.Value)) return;
            if (!ChatWire.TryRead(message.Payload, true, out var id, out var text)) return;
            var sender = Find(id);
            if (sender == null) return;
            // A hostile host cannot fill an unbounded log or spend unbounded rendering work. This allowance is per
            // displayed sender so a legitimate burst from several players does not crowd out another player's line.
            if (!rates.TryGetValue(sender.PlayerKey, out var rate) && !MakeRoom(ChatRateLimit.RelaySlack)) return;
            var allowed = rate.Take(Clock(), ChatRateLimit.RelaySlack);
            rates[sender.PlayerKey] = rate;
            if (allowed) Append(new ChatLine(Author(sender), HostText.ForDisplay(text, ChatWire.MaximumCharacters)));
        }

        private bool Relay(MultiplayerParticipant sender, string text)
        {
            if (text.Length == 0) return false;
            if (control is IInternalCommunicationControl routing && routing.HasCommunicationRouting)
                return RelayAudience(sender, text, routing);
            skipped.Clear();
            var result = channel!.Send(new ArraySegment<byte>(ChatWire.Write(text, sender.Id.Value)), InternalSystemTarget.Everyone, skipped);
            // Everyone broadcasts succeed with zero recipients when all windows are full. That differs from a host
            // with nobody else joined, whose local chat still works. A partial broadcast must not be retried as a whole.
            if (!result.TryGetValue(out var recipients) || (recipients == 0 && skipped.Count > 0)) return false;
            Append(new ChatLine(Author(sender), text));
            return true;
        }

        private bool RelayAudience(MultiplayerParticipant sender, string text, IInternalCommunicationControl routing)
        {
            var payload = new ArraySegment<byte>(ChatWire.Write(text, sender.Id.Value));
            var delivered = 0;
            var refused = 0;
            var local = false;
            foreach (var listener in snapshot!.Participants)
            {
                if (!routing.CanCommunicate(sender.Id, listener.Id)) continue;
                if (listener.IsLocal) { local = true; continue; }
                var result = channel!.Send(payload, InternalSystemTarget.Only(listener.Id));
                if (result.TryGetValue(out var count) && count > 0) delivered += count;
                else refused++;
            }
            // Preserve the ordinary partial-send rule: never retry a line some recipients already received.
            if (delivered == 0 && refused > 0) return false;
            if (local) Append(new ChatLine(Author(sender), text));
            return true;
        }

        private static string Author(MultiplayerParticipant sender)
        {
            // The provider assigns p + a UInt64, at most 21 characters. Keep that discriminator even when two players
            // choose the same name; a request never supplies it. The host has its own unambiguous role marker.
            var prefix = sender.IsHost ? "HOST / " : "PLAYER " + HostText.ForDisplay(sender.Id.Value, 21) + " / ";
            return prefix + HostText.ForDisplay(sender.DisplayName, 32 - prefix.Length);
        }

        private bool MakeRoom(double slack)
        {
            if (rates.Count < 64) return true;
            var now = Clock();
            MultiplayerPlayerKey expired = default;
            foreach (var entry in rates) if (entry.Value.IsExpired(now, slack)) { expired = entry.Key; break; }
            return expired.IsValid && rates.Remove(expired);
        }

        internal void Feedback(string text, bool error = false) => Append(new ChatLine(string.Empty, HostText.ForDisplay(text, 256), error));

        private void Append(ChatLine line)
        {
            while (lines.Count >= 128) lines.Dequeue();
            lines.Enqueue(line);
        }

        internal bool TryTakeLine(out ChatLine line)
        {
            if (lines.Count > 0) { line = lines.Dequeue(); return true; }
            line = default;
            return false;
        }

        private MultiplayerParticipant? Find(ParticipantId id) => Find(id.Value);
        private MultiplayerParticipant? Find(string id)
        {
            var players = snapshot?.Participants;
            if (players == null) return null;
            for (var index = 0; index < players.Count; index++) if (players[index].Id.Value == id) return players[index];
            return null;
        }

        private void Reset()
        {
            lines.Clear();
            rates.Clear();
            skipped.Clear();
            sendRate = default;
            Revision++;
        }

        public void Dispose()
        {
            var owned = channel;
            channel = null;
            control = null;
            snapshot = null;
            role = InternalSessionRole.Solo;
            Reset();
            owned?.Dispose();
        }
    }
}
