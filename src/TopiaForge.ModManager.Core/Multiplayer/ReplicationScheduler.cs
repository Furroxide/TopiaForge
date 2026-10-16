using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>A relevant entity competing for one client's bytes this tick.</summary>
    internal readonly struct ReplicationCandidate
    {
        public ReplicationCandidate(NetworkEntityId id, int priority, int sizeBytes, bool atRest)
        {
            if (!id.IsValid) throw new ArgumentException("A valid id is required.", nameof(id));
            if (priority < 1 || priority > ReplicationScheduler.MaximumPriority)
            {
                throw new ArgumentOutOfRangeException(nameof(priority));
            }

            if (sizeBytes < 1 || sizeBytes > ReplicationScheduler.MaximumSizeBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeBytes));
            }

            Id = id;
            Priority = priority;
            SizeBytes = sizeBytes;
            AtRest = atRest;
        }

        public NetworkEntityId Id { get; }

        /// <summary>How fast the entity's claim grows each tick it waits; at least one.</summary>
        public int Priority { get; }

        /// <summary>The encoded size of the update.</summary>
        public int SizeBytes { get; }

        /// <summary>Whether the body is asleep, as the physics engine reports it.</summary>
        public bool AtRest { get; }
    }

    /// <summary>
    /// Chooses which entity updates one client receives each downstream tick. Every waiting entity's accumulator
    /// grows by its priority, and the highest go first while the byte budget lasts. A tie goes to the higher priority,
    /// then to the entity sent longest ago, then to the lower id, so equal claims take turns rather than favouring low
    /// ids. A sent entity's accumulator resets to zero, so everyone eventually reaches the front: no entity starves,
    /// and equal priorities share bandwidth equally. A body at rest is sent three more times and then costs nothing
    /// until it moves.
    /// </summary>
    internal sealed class ReplicationScheduler
    {
        public const int MaximumPriority = 1_000_000;
        public const int MaximumSizeBytes = 64 * 1024;
        public const int MaximumBudgetBytesPerTick = 1 << 20;

        /// <summary>How many times a body that came to rest is sent before it is suppressed.</summary>
        public const int RestConfirmationSends = 3;

        /// <summary>The Realtime class's 64 kbit/s at the 10 Hz downstream rate.</summary>
        public const int DefaultBudgetBytesPerTick = 800;

        private readonly Dictionary<uint, EntityState> states = new Dictionary<uint, EntityState>();
        private readonly List<Pending> order = new List<Pending>();
        private readonly List<uint> stale = new List<uint>();
        private long credit;
        private int tick;
        private long sends;

        public ReplicationScheduler(int budgetBytesPerTick = DefaultBudgetBytesPerTick)
        {
            if (budgetBytesPerTick < 1 || budgetBytesPerTick > MaximumBudgetBytesPerTick)
            {
                throw new ArgumentOutOfRangeException(nameof(budgetBytesPerTick));
            }

            BudgetBytesPerTick = budgetBytesPerTick;
        }

        public int BudgetBytesPerTick { get; }

        /// <summary>Bytes the next tick may spend before its budget is added; negative after an overdraw.</summary>
        public long Credit => credit;

        public int TrackedCount => states.Count;

        /// <summary>
        /// Runs one tick over this tick's candidates and fills <paramref name="selected"/> in send order. Unused budget
        /// does not carry over, but an overdraw does: the first, highest candidate is sent even when it alone exceeds
        /// the budget, and the debt delays the next tick. Entities that are not candidates are forgotten.
        /// </summary>
        /// <exception cref="ArgumentException">A candidate is listed twice.</exception>
        public void Schedule(IReadOnlyList<ReplicationCandidate> candidates, List<NetworkEntityId> selected)
        {
            if (candidates == null) throw new ArgumentNullException(nameof(candidates));
            if (selected == null) throw new ArgumentNullException(nameof(selected));
            selected.Clear();
            order.Clear();
            tick++;
            for (var index = 0; index < candidates.Count; index++)
            {
                var id = candidates[index].Id.Value;
                if (states.TryGetValue(id, out var existing) && existing.SeenTick == tick)
                {
                    throw new ArgumentException("Entity " + candidates[index].Id + " is listed twice.", nameof(candidates));
                }

                if (existing == null) states.Add(id, new EntityState());
                states[id].SeenTick = tick;
            }

            credit = Math.Min(credit, 0) + BudgetBytesPerTick;
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                var state = states[candidate.Id.Value];
                if (candidate.AtRest)
                {
                    if (!state.AtRest)
                    {
                        state.AtRest = true;
                        state.RestSendsRemaining = RestConfirmationSends;
                    }

                    if (state.RestSendsRemaining == 0)
                    {
                        state.Accumulator = 0;
                        continue;
                    }
                }
                else
                {
                    state.AtRest = false;
                    state.RestSendsRemaining = 0;
                }

                state.Accumulator = NetIntegerMath.SaturatingAdd(state.Accumulator, candidate.Priority);
                order.Add(new Pending(candidate.Id, state.Accumulator, candidate.Priority, candidate.SizeBytes, state));
            }

            ForgetUnseen();
            order.Sort(PendingOrder.Instance);
            for (var index = 0; index < order.Count && credit > 0; index++)
            {
                var pending = order[index];
                if (index > 0 && pending.SizeBytes > credit) continue;
                credit -= pending.SizeBytes;
                selected.Add(pending.Id);
                pending.State.Accumulator = 0;
                pending.State.LastSend = ++sends;
                if (pending.State.AtRest) pending.State.RestSendsRemaining--;
            }
        }

        /// <summary>Re-arms the rest confirmations of a resting body, for example after the client resynchronised.</summary>
        public bool Refresh(NetworkEntityId id)
        {
            if (!states.TryGetValue(id.Value, out var state)) return false;
            if (state.AtRest) state.RestSendsRemaining = RestConfirmationSends;
            return true;
        }

        public bool Forget(NetworkEntityId id) => states.Remove(id.Value);

        public long AccumulatorOf(NetworkEntityId id) => states.TryGetValue(id.Value, out var state) ? state.Accumulator : 0;

        /// <summary>Whether a resting body has had its confirmations and now costs nothing.</summary>
        public bool IsSuppressed(NetworkEntityId id) =>
            states.TryGetValue(id.Value, out var state) && state.AtRest && state.RestSendsRemaining == 0;

        private void ForgetUnseen()
        {
            stale.Clear();
            foreach (var pair in states)
            {
                if (pair.Value.SeenTick != tick) stale.Add(pair.Key);
            }

            foreach (var id in stale) states.Remove(id);
        }

        private sealed class EntityState
        {
            public long Accumulator;
            public int RestSendsRemaining;
            public bool AtRest;
            public int SeenTick;

            /// <summary>The scheduler's send count when this entity was last sent; zero if never.</summary>
            public long LastSend;
        }

        private readonly struct Pending
        {
            public Pending(NetworkEntityId id, long accumulator, int priority, int sizeBytes, EntityState state)
            {
                Id = id;
                Accumulator = accumulator;
                Priority = priority;
                SizeBytes = sizeBytes;
                State = state;
                LastSend = state.LastSend;
            }

            public NetworkEntityId Id { get; }

            public long Accumulator { get; }

            public int Priority { get; }

            public long LastSend { get; }

            public int SizeBytes { get; }

            public EntityState State { get; }
        }

        private sealed class PendingOrder : IComparer<Pending>
        {
            public static readonly PendingOrder Instance = new PendingOrder();

            public int Compare(Pending x, Pending y)
            {
                var byAccumulator = y.Accumulator.CompareTo(x.Accumulator);
                if (byAccumulator != 0) return byAccumulator;
                var byPriority = y.Priority.CompareTo(x.Priority);
                if (byPriority != 0) return byPriority;
                var bySendOrder = x.LastSend.CompareTo(y.LastSend);
                return bySendOrder != 0 ? bySendOrder : x.Id.CompareTo(y.Id);
            }
        }
    }
}
