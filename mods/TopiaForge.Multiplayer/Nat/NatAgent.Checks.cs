using System.Net;
using TopiaForge.Multiplayer.Network;
using TopiaForge.Multiplayer.NatTraversal.Ice;
using TopiaForge.Multiplayer.NatTraversal.Stun;
using TopiaForge.Multiplayer.NatTraversal.Turn;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal
{
    /// <summary>
    /// Answering connectivity checks. A check must carry FINGERPRINT, a username naming one of this side's sessions and
    /// that session's remote fragment, MESSAGE-INTEGRITY under this side's password, and the role opposite this side's;
    /// anything else gets no answer at all, so a stranger learns nothing. The answer goes back on the path the check came
    /// in on, and the check triggers one of this side's own on that pair. Relay-only answers nothing that did not come
    /// through its own relay.
    /// </summary>
    internal sealed partial class NatAgent
    {
        private const int MaximumUsernameBytes = 513;

        /// <summary>Gets how many checks were answered, for tests.</summary>
        internal long ChecksAnswered { get; private set; }

        private void OnCheck(TransportPath from, StunMessage request, long now)
        {
            if (!request.HasFingerprint || !request.HasIntegrity || request.UnknownRequired.Count > 0) return;
            if (!request.TryGetText(StunAttribute.Username, MaximumUsernameBytes, out var username)) return;
            var colon = username.IndexOf(':');
            if (colon <= 0 || colon == username.Length - 1) return;
            if (!byUfrag.TryGetValue(username.Substring(0, colon), out var session)) return;
            var checker = session.Checker;
            if (checker.Remote == null || !string.Equals(username.Substring(colon + 1), checker.Remote.Ufrag, System.StringComparison.Ordinal)) return;
            if (!request.VerifyIntegrity(checker.Local.Key)) return;
            var claimsControlling = request.Has(StunAttribute.IceControlling);
            if (claimsControlling == request.Has(StunAttribute.IceControlled) || claimsControlling == checker.Controlling) return;

            LocalBase? arrival;
            IPEndPoint source;
            if (TurnRelayPath.TryParse(from, out var allocation, out source))
            {
                arrival = BaseNumbered(allocation);
            }
            else if (!RelayOnly && UdpPaths.TryGetEndPoint(from, out var direct))
            {
                source = new IPEndPoint(NatAddresses.Unmap(direct.Address), direct.Port);
                arrival = socketBase;
            }
            else
            {
                return;
            }

            // An answer names the source's port, so a check from port 0 (which nothing genuine sends from) gets none.
            if (arrival == null || source.Port == 0 || session.State == NatSessionState.Ended || session.State == NatSessionState.Failed) return;
            writer.Begin(scratch, StunMethod.Binding, StunClass.Success, request.TransactionId);
            writer.AddAddress(StunAttribute.XorMappedAddress, source);
            var length = writer.Finish(checker.Local.Key, true);
            SendRaw(from, scratch, length);
            ChecksAnswered++;
            var priority = request.TryGetUInt32(StunAttribute.Priority, out var claimed) ? claimed : IceCandidate.ComputePriority(IceCandidateKind.PeerReflexive, 65535);
            var known = checker.RemoteCandidates.Count;
            checker.OnRequest(arrival, source, priority, now);

            // A peer-reflexive address heard through the relay already holds a permission there; one heard directly may
            // be asked to use the relay later, so the current allocation lets it in too.
            if (checker.RemoteCandidates.Count > known) PermitRemote(checker.RemoteCandidates[checker.RemoteCandidates.Count - 1], session.PeerId, now);
            session.Touch(now);
        }
    }
}
