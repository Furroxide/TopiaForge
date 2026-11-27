using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>An entity that could be replicated to one viewer this tick.</summary>
    internal readonly struct RelevancyCandidate
    {
        public RelevancyCandidate(NetworkEntityId id, NetQuantizedPosition position, bool alwaysRelevant = false)
        {
            if (!id.IsValid) throw new ArgumentException("A valid id is required.", nameof(id));
            Id = id;
            Position = position;
            AlwaysRelevant = alwaysRelevant;
        }

        public NetworkEntityId Id { get; }

        public NetQuantizedPosition Position { get; }

        /// <summary>Relevant at any distance, such as the viewer's own avatar or a body the viewer holds.</summary>
        public bool AlwaysRelevant { get; }
    }

    /// <summary>
    /// The entities one viewer receives. An entity enters within 150 m and leaves beyond 170 m, so one hovering at the
    /// edge does not flicker. Distances are exact integers on the 1/512 m grid, so every peer agrees at the edges.
    /// </summary>
    internal sealed class RelevancySet
    {
        public const int EnterRadiusMeters = 150;
        public const int LeaveRadiusMeters = 170;

        /// <summary>An entity at most this far (squared grid units) enters.</summary>
        public const long EnterRadiusSquaredUnits =
            (long)EnterRadiusMeters * NetQuantization.PositionUnitsPerMeter *
            EnterRadiusMeters * NetQuantization.PositionUnitsPerMeter;

        /// <summary>A relevant entity at most this far (squared grid units) stays.</summary>
        public const long LeaveRadiusSquaredUnits =
            (long)LeaveRadiusMeters * NetQuantization.PositionUnitsPerMeter *
            LeaveRadiusMeters * NetQuantization.PositionUnitsPerMeter;

        private readonly HashSet<uint> members = new HashSet<uint>();
        private readonly HashSet<uint> seen = new HashSet<uint>();
        private readonly List<uint> departed = new List<uint>();

        public int Count => members.Count;

        /// <summary>The hysteresis rule: inclusive at both radii.</summary>
        public static bool IsRelevant(bool wasRelevant, long distanceSquaredUnits)
        {
            if (distanceSquaredUnits < 0) throw new ArgumentOutOfRangeException(nameof(distanceSquaredUnits));
            return distanceSquaredUnits <= (wasRelevant ? LeaveRadiusSquaredUnits : EnterRadiusSquaredUnits);
        }

        public bool Contains(NetworkEntityId id) => members.Contains(id.Value);

        /// <summary>
        /// Re-evaluates every candidate against the viewer. Members missing from the candidates leave. Both output
        /// lists are cleared first and come back in ascending id order.
        /// </summary>
        /// <exception cref="ArgumentException">A candidate is listed twice.</exception>
        public void Update(
            NetQuantizedPosition viewer,
            IReadOnlyList<RelevancyCandidate> candidates,
            List<NetworkEntityId> entered,
            List<NetworkEntityId> left)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (entered == null) throw new ArgumentNullException(nameof(entered));
            if (left == null) throw new ArgumentNullException(nameof(left));
            entered.Clear();
            left.Clear();
            seen.Clear();
            for (var index = 0; index < candidates.Count; index++)
            {
                if (!seen.Add(candidates[index].Id.Value))
                {
                    seen.Clear();
                    throw new ArgumentException("Entity " + candidates[index].Id + " is listed twice.", nameof(candidates));
                }
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var id = candidate.Id.Value;
                var was = members.Contains(id);
                var now = candidate.AlwaysRelevant || IsRelevant(was, viewer.DistanceSquaredTo(candidate.Position));
                if (now && !was)
                {
                    members.Add(id);
                    entered.Add(candidate.Id);
                }
                else if (!now && was)
                {
                    members.Remove(id);
                    left.Add(candidate.Id);
                }
            }

            departed.Clear();
            foreach (var id in members)
            {
                if (!seen.Contains(id)) departed.Add(id);
            }

            foreach (var id in departed)
            {
                members.Remove(id);
                left.Add(new NetworkEntityId(id));
            }

            entered.Sort();
            left.Sort();
        }

        /// <summary>Drops an entity without reporting it, as when it despawns.</summary>
        public bool Remove(NetworkEntityId id) => members.Remove(id.Value);

        /// <summary>Reports every member as left, in ascending id order, and empties the set.</summary>
        public void Clear(List<NetworkEntityId> left)
        {
            if (left == null) throw new ArgumentNullException(nameof(left));
            left.Clear();
            foreach (var id in members) left.Add(new NetworkEntityId(id));
            members.Clear();
            left.Sort();
        }
    }
}
