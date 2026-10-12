using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>What a host's admission policy sees about one join attempt.</summary>
    internal sealed class JoinRequestContext
    {
        internal JoinRequestContext(
            IEngineLink link,
            string peerIdentity,
            JoinRequestMessage request,
            IReadOnlyList<ContractEntry> hostContracts,
            int connectedParticipants,
            int maximumParticipants)
        {
            Link = link;
            PeerIdentity = peerIdentity;
            Request = request;
            HostContracts = hostContracts;
            ConnectedParticipants = connectedParticipants;
            MaximumParticipants = maximumParticipants;
        }

        /// <summary>Gets the link the request came over, which a provider's policy may ask for its channel binding.</summary>
        internal IEngineLink Link { get; }

        internal string PeerIdentity { get; }
        internal JoinRequestMessage Request { get; }
        internal IReadOnlyList<ContractEntry> HostContracts { get; }
        internal int ConnectedParticipants { get; }
        internal int MaximumParticipants { get; }
    }

    internal readonly struct JoinDecision
    {
        private JoinDecision(bool accepted, MultiplayerSessionEndReason reason, string message, byte[] report)
        {
            Accepted = accepted;
            Reason = reason;
            Message = message;
            Report = report;
        }

        internal bool Accepted { get; }
        internal MultiplayerSessionEndReason Reason { get; }
        internal string Message { get; }

        /// <summary>Gets the opaque admission report shown to the joining player.</summary>
        internal byte[] Report { get; }

        internal static JoinDecision Accept(byte[]? report = null) =>
            new JoinDecision(true, MultiplayerSessionEndReason.None, string.Empty, report ?? Array.Empty<byte>());

        internal static JoinDecision Reject(MultiplayerSessionEndReason reason, string message, byte[]? report = null) =>
            new JoinDecision(false, reason, message, report ?? Array.Empty<byte>());
    }

    /// <summary>Decides whether a host admits a joining peer.</summary>
    internal interface IJoinPolicy
    {
        JoinDecision Evaluate(JoinRequestContext context);
    }

    /// <summary>
    /// The engine's built-in admission: the protocol major version must match, the session must have room, and a
    /// generated contract both peers registered must have the same schema digest and wire revision. Content and mod
    /// admission belong to the runtime host's policy, which wraps or replaces this one.
    /// </summary>
    internal sealed class DefaultJoinPolicy : IJoinPolicy
    {
        internal static readonly DefaultJoinPolicy Instance = new DefaultJoinPolicy();

        public JoinDecision Evaluate(JoinRequestContext context)
        {
            if (!SameMajor(context.Request.ProtocolVersion, SessionEngine.ProtocolVersion))
            {
                return JoinDecision.Reject(
                    MultiplayerSessionEndReason.ProtocolMismatch,
                    "The host speaks multiplayer protocol " + SessionEngine.ProtocolVersion + ".");
            }

            if (context.ConnectedParticipants >= context.MaximumParticipants)
            {
                return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "The session is full.");
            }

            var host = new Dictionary<WireKey, ContractEntry>();
            foreach (var contract in context.HostContracts) host[new WireKey(contract.Owner, contract.Id)] = contract;
            List<string>? mismatches = null;
            foreach (var contract in context.Request.Contracts)
            {
                if (!host.TryGetValue(new WireKey(contract.Owner, contract.Id), out var expected)) continue;
                if (expected.WireFormatRevision == contract.WireFormatRevision &&
                    string.Equals(expected.SchemaSha256, contract.SchemaSha256, StringComparison.Ordinal))
                {
                    continue;
                }

                mismatches ??= new List<string>();
                mismatches.Add(contract.Owner + "/" + contract.Id);
            }

            return mismatches == null
                ? JoinDecision.Accept()
                : JoinDecision.Reject(
                    MultiplayerSessionEndReason.ProtocolMismatch,
                    "These multiplayer contracts differ from the host's: " + string.Join(", ", mismatches) + ".");
        }

        private static bool SameMajor(string left, string right)
        {
            var leftMajor = left.Split('.')[0];
            var rightMajor = right.Split('.')[0];
            return leftMajor.Length > 0 && string.Equals(leftMajor, rightMajor, StringComparison.Ordinal);
        }
    }
}
