using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The listen host's side of session follow (WS3.8): it tells every player what its game is in, a world session, a level
    /// outside one, or the main menu, on the <c>SceneFollow</c> channel. It sends to everyone whenever that changes and
    /// whenever a stream starts, restarts or ends, so a player that joins, rejoins or comes back to a replaced session hears
    /// it at once; a player a full window skipped gets the newest announcement alone once its window has room. Players may request
    /// the running map while joining; other messages are taken and dropped, so no window stays full. At rest a frame compares a few
    /// fields and allocates nothing. Main thread only.
    /// </summary>
    internal sealed class SessionFollowHost
    {
        private readonly Func<IInternalSystemChannel?> channel;
        private readonly Func<HostWorldState> describe;
        private readonly WorldWireWriter writer = new WorldWireWriter(SessionFollowWire.MaximumMessageBytes);
        private readonly List<ParticipantId> skipped = new List<ParticipantId>();
        private readonly List<ParticipantId> waiting = new List<ParticipantId>();
        private readonly SessionMapHost? maps;
        private HostWorldState? announced;
        private ulong seenGeneration;

        /// <param name="channel">The channel while it is open, or null.</param>
        /// <param name="describe">What this game is in now. It returns the same instance while nothing changed, and allocates nothing then.</param>
        internal SessionFollowHost(Func<IInternalSystemChannel?> channel, Func<HostWorldState> describe, SessionMapHost? maps = null)
        {
            this.maps = maps;
            this.channel = channel ?? throw new ArgumentNullException(nameof(channel));
            this.describe = describe ?? throw new ArgumentNullException(nameof(describe));
        }

        internal bool IsActive { get; private set; }

        /// <summary>Gets the announcement every player has, or will once its window has room.</summary>
        internal HostWorldState? Announced => announced;

        /// <summary>Gets how many announcements went out, one per player each, for tests and diagnostics.</summary>
        internal long Sent { get; private set; }

        /// <summary>Gets how many unrecognized or refused messages players sent on the channel.</summary>
        internal long Dropped { get; private set; }

        internal void Activate()
        {
            maps?.Clear();
            IsActive = true;
            announced = null;
            seenGeneration = 0;
            skipped.Clear();
        }

        internal void Deactivate()
        {
            maps?.Clear();
            IsActive = false;
            announced = null;
            skipped.Clear();
        }

        /// <summary>One frame: drops what players sent, announces a change or a stream that started, and catches up skipped players.</summary>
        internal void Pump()
        {
            if (!IsActive) return;
            var open = channel();
            if (open == null || !open.IsOpen) return;
            var state = describe();
            while (open.TryReceive(out var message))
                if (maps == null || !maps.Receive(open, state, message)) Dropped++;
            var generation = open.Generation;
            if (!ReferenceEquals(state, announced) && !state.SameAs(announced)) Announce(open, state, generation);
            else if (generation != seenGeneration) Announce(open, state, generation);
            else if (skipped.Count > 0) CatchUp(open);
            maps?.Pump(open, state);
        }

        private void Announce(IInternalSystemChannel open, HostWorldState state, ulong generation)
        {
            announced = state;
            seenGeneration = generation;
            SessionFollowWire.Write(writer, state);
            skipped.Clear();
            var sent = open.Send(writer.Segment, InternalSystemTarget.Everyone, skipped);
            if (sent.TryGetValue(out var count)) Sent += count;
        }

        /// <summary>Sends the newest announcement to each skipped player whose window has room now.</summary>
        private void CatchUp(IInternalSystemChannel open)
        {
            SessionFollowWire.Write(writer, announced!);
            waiting.Clear();
            foreach (var player in skipped)
            {
                var target = InternalSystemTarget.Only(player);
                if (open.GetGeneration(player) == 0) continue;
                if (!open.CanSend(target, writer.Length) || !open.Send(writer.Segment, target).Succeeded)
                {
                    waiting.Add(player);
                    continue;
                }

                Sent++;
            }

            skipped.Clear();
            skipped.AddRange(waiting);
            waiting.Clear();
        }
    }
}
