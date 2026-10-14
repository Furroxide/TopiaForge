using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Ice;

namespace TopiaForge.Multiplayer.NatTraversal.Signaling
{
    /// <summary>
    /// The signaling grammar as canonical JSON text (ControlPlaneApi.md, section 8). Frames this side sends are written
    /// with exactly the members the service expects, so they match its own transcript byte for byte. Frames it receives
    /// must carry their members with valid values; members the service may add later are ignored, and anything malformed
    /// reads as no frame at all. Nothing here throws on what the service sends, and no error names a value.
    /// </summary>
    internal static class SignalCodec
    {
        internal const int MaximumFrameBytes = 8192;
        internal const int MaximumCandidates = 16;
        internal const int MaximumPunchDelayMs = 10_000;
        internal const int MaximumPeers = 32;

        internal static byte[] Encode(SignalMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));
            var node = new JsonObjectNode().Add("type", TypeName(message.Type));
            if (message.Peer != null)
            {
                if (!IsPeerId(message.Peer)) throw new ArgumentException("A peer id is 16 lowercase hexadecimal characters.", nameof(message));
                node.Add("peer", message.Peer);
            }

            switch (message.Type)
            {
                case SignalType.Offer:
                case SignalType.Answer:
                    if (message.Ice == null || message.Candidates.Count > MaximumCandidates) throw new ArgumentException("An offer or answer carries credentials and at most 16 candidates.", nameof(message));
                    node.Add("ice", new JsonObjectNode().Add("pwd", message.Ice.Password).Add("ufrag", message.Ice.Ufrag));
                    var items = new List<JsonNode>(message.Candidates.Count);
                    foreach (var candidate in message.Candidates) items.Add(Write(candidate));
                    node.Add("candidates", new JsonArrayNode(items));
                    break;
                case SignalType.Candidate:
                    node.Add("candidate", Write(message.Candidate ?? throw new ArgumentException("A candidate frame carries a candidate.", nameof(message))));
                    break;
                case SignalType.Punch:
                    if (message.DelayMs < 0 || message.DelayMs > MaximumPunchDelayMs) throw new ArgumentException("A punch waits 0 to 10,000 ms.", nameof(message));
                    node.Add("delayMs", message.DelayMs);
                    break;
                case SignalType.Bye:
                    node.Add("reason", ReasonName(message.Reason));
                    break;
                default:
                    throw new ArgumentException("Only offers, answers, candidates, punches and byes are sent.", nameof(message));
            }

            return CanonicalJson.Serialize(node);
        }

        /// <summary>
        /// Reads one frame. With <paramref name="strict"/>, as the service reads what clients send, a frame must hold
        /// exactly its members; otherwise members it does not need are ignored.
        /// </summary>
        internal static bool TryDecode(byte[]? frame, bool strict, out SignalMessage message)
        {
            message = null!;
            if (frame == null || frame.Length == 0 || frame.Length > MaximumFrameBytes) return false;
            if (!CanonicalJson.TryParse(frame, MaximumFrameBytes, out var node, out _) || !(node is JsonObjectNode root)) return false;
            if (!TryText(root, "type", out var typeName) || !TryType(typeName, out var type)) return false;
            var peer = TryText(root, "peer", out var named) ? named : null;
            if (peer != null && !IsPeerId(peer)) return false;
            message = new SignalMessage(type) { Peer = peer };
            switch (type)
            {
                case SignalType.Offer:
                case SignalType.Answer:
                    if (!TryIce(root, out var ice) || !root.TryGet("candidates", out var list) || !(list is JsonArrayNode array) || array.Items.Count > MaximumCandidates) return false;
                    var candidates = new List<SignalCandidate>(array.Items.Count);
                    foreach (var item in array.Items)
                    {
                        if (!TryCandidate(item, strict, out var candidate)) return false;
                        candidates.Add(candidate);
                    }

                    message.Ice = ice;
                    message.Candidates = candidates;
                    return Exactly(root, strict, peer, "type", "ice", "candidates");
                case SignalType.Candidate:
                    if (!root.TryGet("candidate", out var single) || !TryCandidate(single, strict, out var trickled)) return false;
                    message.Candidate = trickled;
                    return Exactly(root, strict, peer, "type", "candidate");
                case SignalType.Punch:
                    if (!TryInteger(root, "delayMs", 0, MaximumPunchDelayMs, out var delay)) return false;
                    message.DelayMs = (int)delay;
                    return Exactly(root, strict, peer, "type", "delayMs");
                case SignalType.Bye:
                    if (!TryText(root, "reason", out var reasonName) || !TryReason(reasonName, out var reason)) return false;
                    message.Reason = reason;
                    return Exactly(root, strict, peer, "type", "reason");
                case SignalType.Welcome:
                    return TryWelcome(root, message);
                case SignalType.Joined:
                    if (peer == null || !TryBoolean(root, "relayOnly", out var joinedRelayOnly)) return false;
                    message.RelayOnly = joinedRelayOnly;
                    return true;
                case SignalType.Left:
                    return peer != null;
                case SignalType.HostStatus:
                    if (!TryBoolean(root, "online", out var online) || !TryBoolean(root, "relayOnly", out var hostRelayOnly)) return false;
                    message.Online = online;
                    message.RelayOnly = hostRelayOnly;
                    return true;
                case SignalType.Notice:
                    if (!TryText(root, "code", out var notice) || notice != "candidates-withheld" || !TryInteger(root, "withheld", 0, 1024, out var withheld)) return false;
                    message.Withheld = (int)withheld;
                    return true;
                case SignalType.Error:
                    if (!TryText(root, "code", out var code)) return false;
                    message.Error = ErrorCode(code);
                    return true;
                default:
                    return false;
            }
        }

        internal static bool IsPeerId(string? text)
        {
            if (text == null || text.Length != 16) return false;
            foreach (var character in text)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return false;
            }

            return true;
        }

        private static bool TryWelcome(JsonObjectNode root, SignalMessage message)
        {
            if (!TryText(root, "role", out var role)) return false;
            if (role == "host")
            {
                if (!root.TryGet("peers", out var list) || !(list is JsonArrayNode array) || array.Items.Count > MaximumPeers) return false;
                var peers = new List<SignalPeer>(array.Items.Count);
                foreach (var item in array.Items)
                {
                    if (!(item is JsonObjectNode entry) || !TryText(entry, "peer", out var id) || !IsPeerId(id) || !TryBoolean(entry, "relayOnly", out var relayOnly)) return false;
                    peers.Add(new SignalPeer(id, relayOnly));
                }

                message.Role = SignalRole.Host;
                message.Peers = peers;
                return message.Peer == null;
            }

            if (role != "peer" || message.Peer == null) return false;
            if (!TryBoolean(root, "hostOnline", out var online) || !TryBoolean(root, "hostRelayOnly", out var hostRelayOnly)) return false;
            message.Role = SignalRole.Peer;
            message.Online = online;
            message.RelayOnly = hostRelayOnly;
            return true;
        }

        private static JsonObjectNode Write(SignalCandidate candidate) =>
            new JsonObjectNode()
                .Add("address", candidate.Address)
                .Add("kind", KindName(candidate.Kind))
                .Add("port", candidate.Port)
                .Add("priority", candidate.Priority);

        private static bool TryCandidate(JsonNode node, bool strict, out SignalCandidate candidate)
        {
            candidate = null!;
            if (!(node is JsonObjectNode entry)) return false;
            if (!TryText(entry, "kind", out var kindName) || !TryKind(kindName, out var kind)) return false;
            if (!TryText(entry, "address", out var address) || !NatAddresses.TryParseCanonical(address, out _)) return false;
            if (!TryInteger(entry, "port", 1, 65535, out var port) || !TryInteger(entry, "priority", 0, uint.MaxValue, out var priority)) return false;
            if (strict && !entry.HasExactly("kind", "address", "port", "priority")) return false;
            candidate = new SignalCandidate(kind, address, (int)port, (uint)priority);
            return true;
        }

        private static bool TryIce(JsonObjectNode root, out IceCredentials ice)
        {
            ice = null!;
            return root.TryGet("ice", out var node) && node is JsonObjectNode entry && entry.HasExactly("ufrag", "pwd") &&
                   TryText(entry, "ufrag", out var ufrag) && TryText(entry, "pwd", out var pwd) && IceCredentials.TryCreate(ufrag, pwd, out ice);
        }

        private static bool Exactly(JsonObjectNode root, bool strict, string? peer, params string[] keys)
        {
            if (!strict) return true;
            if (peer == null) return root.HasExactly(keys);
            var withPeer = new string[keys.Length + 1];
            keys.CopyTo(withPeer, 0);
            withPeer[keys.Length] = "peer";
            return root.HasExactly(withPeer);
        }

        private static bool TryText(JsonObjectNode node, string key, out string text)
        {
            text = string.Empty;
            if (!node.TryGet(key, out var value) || !(value is JsonStringNode item)) return false;
            text = item.Value;
            return true;
        }

        private static bool TryBoolean(JsonObjectNode node, string key, out bool value)
        {
            value = false;
            if (!node.TryGet(key, out var item) || !(item is JsonBooleanNode flag)) return false;
            value = flag.Value;
            return true;
        }

        private static bool TryInteger(JsonObjectNode node, string key, long minimum, long maximum, out long value)
        {
            value = 0;
            if (!node.TryGet(key, out var item) || !(item is JsonIntegerNode number) || number.Value < minimum || number.Value > maximum) return false;
            value = number.Value;
            return true;
        }

        private static string TypeName(SignalType type)
        {
            switch (type)
            {
                case SignalType.Offer: return "offer";
                case SignalType.Answer: return "answer";
                case SignalType.Candidate: return "candidate";
                case SignalType.Punch: return "punch";
                case SignalType.Bye: return "bye";
                case SignalType.Welcome: return "welcome";
                case SignalType.Joined: return "joined";
                case SignalType.Left: return "left";
                case SignalType.HostStatus: return "host-status";
                case SignalType.Notice: return "notice";
                default: return "error";
            }
        }

        private static bool TryType(string name, out SignalType type)
        {
            for (var value = SignalType.Offer; value <= SignalType.Error; value++)
            {
                if (TypeName(value) != name) continue;
                type = value;
                return true;
            }

            type = SignalType.Error;
            return false;
        }

        private static string KindName(IceCandidateKind kind) =>
            kind == IceCandidateKind.Host ? "host" : kind == IceCandidateKind.ServerReflexive ? "srflx" : "relay";

        private static bool TryKind(string name, out IceCandidateKind kind)
        {
            kind = name == "host" ? IceCandidateKind.Host : name == "srflx" ? IceCandidateKind.ServerReflexive : IceCandidateKind.Relayed;
            return name == "host" || name == "srflx" || name == "relay";
        }

        private static string ReasonName(SignalByeReason reason) =>
            reason == SignalByeReason.Done ? "done" : reason == SignalByeReason.Cancelled ? "cancelled" : reason == SignalByeReason.Failed ? "failed" : "timeout";

        private static bool TryReason(string name, out SignalByeReason reason)
        {
            for (var value = SignalByeReason.Done; value <= SignalByeReason.Timeout; value++)
            {
                if (ReasonName(value) != name) continue;
                reason = value;
                return true;
            }

            reason = SignalByeReason.Done;
            return false;
        }

        private static SignalErrorCode ErrorCode(string code)
        {
            switch (code)
            {
                case "invalid-message": return SignalErrorCode.InvalidMessage;
                case "host-offline": return SignalErrorCode.HostOffline;
                case "unknown-peer": return SignalErrorCode.UnknownPeer;
                case "rate-limited": return SignalErrorCode.RateLimited;
                default: return SignalErrorCode.Unknown;
            }
        }
    }
}
