using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods
{
    /// <summary>One player's host-assigned communication group. Groups are numbers from 1 through 16.</summary>
    [RequiresTopiaForge("0.2.0")]
    public readonly struct CommunicationMember
    {
        /// <summary>Assigns a participant to one group; neither names nor network addresses are used.</summary>
        public CommunicationMember(ParticipantId participant, int group)
        {
            if (!participant.IsValid) throw new ArgumentException("A participant is required.", nameof(participant));
            CommunicationRoute.CheckGroup(group);
            Participant = participant;
            Group = group;
        }

        /// <summary>Gets the participant assigned by the host.</summary>
        public ParticipantId Participant { get; }
        /// <summary>Gets the group number.</summary>
        public int Group { get; }
    }

    /// <summary>A directed permission for voice and chat, optionally limited by host-validated distance.</summary>
    [RequiresTopiaForge("0.2.0")]
    public readonly struct CommunicationRoute
    {
        /// <summary>
        /// Allows one group to reach another. Zero distance means global; a positive distance, at most 1,000 metres,
        /// requires fresh positions in the host's current world. A reverse route must be allowed separately.
        /// </summary>
        public CommunicationRoute(int fromGroup, int toGroup, float maximumDistance = 0)
        {
            CheckGroup(fromGroup);
            CheckGroup(toGroup);
            if (float.IsNaN(maximumDistance) || float.IsInfinity(maximumDistance) || maximumDistance < 0 || maximumDistance > 1000)
                throw new ArgumentOutOfRangeException(nameof(maximumDistance));
            FromGroup = fromGroup;
            ToGroup = toGroup;
            MaximumDistance = maximumDistance;
        }

        /// <summary>Gets the sender group.</summary>
        public int FromGroup { get; }
        /// <summary>Gets the listener group.</summary>
        public int ToGroup { get; }
        /// <summary>Gets the maximum distance in metres, or zero for global delivery.</summary>
        public float MaximumDistance { get; }

        internal static void CheckGroup(int group)
        {
            if (group < 1 || group > 16) throw new ArgumentOutOfRangeException(nameof(group));
        }
    }

    /// <summary>
    /// An immutable, default-deny audience for both voice and chat: at most 64 participants and 256 directed routes.
    /// Unassigned players and missing routes receive nothing. The constructor copies both lists. No group state is
    /// published to clients. Chat echoes follow the same rules, including delivery to the sender's own group.
    /// </summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class CommunicationPolicy
    {
        private readonly Dictionary<ParticipantId, int> groups = new Dictionary<ParticipantId, int>();
        private readonly Dictionary<int, float> distances = new Dictionary<int, float>();

        /// <summary>Copies a complete audience; duplicate participants or group pairs are rejected.</summary>
        public CommunicationPolicy(IReadOnlyList<CommunicationMember> members, IReadOnlyList<CommunicationRoute> routes)
        {
            if (members == null) throw new ArgumentNullException(nameof(members));
            if (routes == null) throw new ArgumentNullException(nameof(routes));
            if (members.Count > 64 || routes.Count > 256) throw new ArgumentException("The communication policy exceeds its bounds.");
            foreach (var member in members)
            {
                if (!member.Participant.IsValid) throw new ArgumentException("A participant is required.", nameof(members));
                CommunicationRoute.CheckGroup(member.Group);
                if (groups.ContainsKey(member.Participant)) throw new ArgumentException("A participant is assigned twice.", nameof(members));
                groups.Add(member.Participant, member.Group);
            }
            foreach (var route in routes)
            {
                CommunicationRoute.CheckGroup(route.FromGroup);
                CommunicationRoute.CheckGroup(route.ToGroup);
                var key = route.FromGroup * 16 + route.ToGroup;
                if (distances.ContainsKey(key)) throw new ArgumentException("A route is assigned twice.", nameof(routes));
                distances.Add(key, route.MaximumDistance);
            }
        }

        internal bool TryGetDistance(ParticipantId sender, ParticipantId listener, out float distance)
        {
            distance = 0;
            return groups.TryGetValue(sender, out var from) && groups.TryGetValue(listener, out var to)
                && distances.TryGetValue(from * 16 + to, out distance);
        }
    }

    /// <summary>
    /// The active gamemode server's communication lease, automatically owned by its context lifetime. Main thread only.
    /// Disposing restores ordinary global routing. Session replacement retires the lease; it cannot affect a later session.
    /// </summary>
    [RequiresTopiaForge("0.2.0")]
    public interface ICommunicationRouting : IDisposable
    {
        /// <summary>Atomically replaces the whole audience. In-flight traffic already authorized cannot be recalled.</summary>
        OperationResult<bool> SetPolicy(CommunicationPolicy policy);
    }

    /// <summary>Host-authoritative communication for gamemode server controllers.</summary>
    [RequiresTopiaForge("0.2.0")]
    public static class CommunicationExtensions
    {
        /// <summary>
        /// Installs a policy for voice and chat. Only the active gamemode server context may acquire the single lease.
        /// Participant and ordinary mod contexts are refused. The runtime owns proximity, relay and lifetime cleanup.
        /// </summary>
        public static OperationResult<ICommunicationRouting> RouteCommunications(this IModContext context, CommunicationPolicy policy)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            return context is IInternalCommunicationContext source ? source.RouteCommunications(policy)
                : OperationResult<ICommunicationRouting>.Failure(ModErrorCode.Unavailable, "Communication routing is unavailable.");
        }
    }
}
