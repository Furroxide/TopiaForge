using System;
using System.Collections.Generic;
using System.Diagnostics;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The session's time (WS3.13): what this process's time authority, Chronos, publishes, and on a joined player the host's,
    /// which it follows. It owns the <c>Time</c> system channel while a provider is attached. A listen host sends its time to
    /// every player when it changes, at most ten times a second with the newest winning, and to everyone again whenever a
    /// stream starts, restarts or ends, so a player that joins, rejoins or moves to a replaced session follows it at once; a
    /// player a full window skipped gets the newest time once its window has room. A joined player takes the host's time and
    /// refuses anything outside the bounds every peer keeps (<see cref="SessionTimeWire"/>), keeping the time it had. Nothing
    /// is sent at rest, and a frame at rest allocates nothing. It also holds the pause guard. Unity-free; main thread. See
    /// docs/internal/MultiplayerRuntimeIntegration.md, section 17.
    /// </summary>
    internal sealed class MultiplayerSessionClock : IMultiplayerRuntimeObserver
    {
        /// <summary>The least time between two sends of a changed time to the players: ten a second at most.</summary>
        internal const long IntervalMicros = 100_000;

        internal const string FollowsHost = "A joined player follows its host's time; only the host publishes the session's time.";
        internal const string HostTimeRefused =
            "Multiplayer: the host sent a session time outside the bounds every player keeps (a scale from zero to one, with a matching mode); this game keeps the time it had.";
        internal const string PlayerTimeRefused = "Multiplayer: a player sent the session's time, which only the host sends; it was ignored.";

        private static readonly Stopwatch Monotonic = Stopwatch.StartNew();
        private static readonly OperationResult<bool> Changed = OperationResult<bool>.Success(true);
        private static readonly OperationResult<bool> Unchanged = OperationResult<bool>.Success(false);
        private static readonly OperationResult<bool> Following = OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, FollowsHost);

        private readonly IMultiplayerLog log;
        private readonly byte[] buffer = new byte[SessionTimeWire.Length];
        private readonly List<ParticipantId> skipped = new List<ParticipantId>();
        private readonly List<ParticipantId> behind = new List<ParticipantId>();
        private Func<long> clock = ReadMonotonic;
        private IInternalSystemChannel? channel;
        private NetRealm realm;
        private InternalSessionTime published;
        private InternalSessionTime hostTime;
        private InternalSessionTime lastSent;
        private long lastSentMicros;
        private bool sentOnce;
        private ulong seenGeneration;
        private bool channelFailureLogged;
        private bool hostRefusalLogged;
        private bool playerRefusalLogged;

        internal MultiplayerSessionClock(IMultiplayerLog log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Gets the guard that keeps the shared world running behind a player's own pause menu online.</summary>
        internal MultiplayerPauseGuard PauseGuard { get; } = new MultiplayerPauseGuard();

        /// <summary>
        /// Gets or sets the clock the send rate is measured by, in microseconds: a monotonic one by default, the runtime's
        /// network clock in the game, and one a test moves by hand.
        /// </summary>
        internal Func<long> Clock
        {
            get => clock;
            set => clock = value ?? throw new ArgumentNullException(nameof(value));
        }

        /// <summary>
        /// Gets the session's time as this process runs it: on a joined player the host's, real time until the host's first
        /// arrives; otherwise what this process's time authority published last, real time until it publishes.
        /// </summary>
        internal InternalSessionTime Current => realm == NetRealm.Client ? hostTime : published;

        /// <summary>Gets what this process's time authority published last; a joined player keeps it for after it leaves.</summary>
        internal InternalSessionTime Published => published;

        /// <summary>Gets whether this process follows a host's time it has received: a joined player, once the host's arrived.</summary>
        internal bool HasHostTime { get; private set; }

        /// <summary>Gets whether the time channel is open.</summary>
        internal bool IsOpen => channel != null && channel.IsOpen;

        /// <summary>Gets the messages this host sent, one per recipient.</summary>
        internal long Sent { get; private set; }

        /// <summary>
        /// Gets the messages refused: on a joined player, a host's time outside the bounds; on a host, any time a player sent,
        /// which the engine already ends such a player for.
        /// </summary>
        internal long Refused { get; private set; }

        /// <summary>
        /// Publishes this process's time, as its time authority derives it. Offline and on a listen host it becomes the
        /// session's time; a joined player follows its host's and is refused.
        /// </summary>
        internal OperationResult<bool> Publish(InternalSessionTime time)
        {
            if (realm == NetRealm.Client) return Following;
            if (time.Equals(published)) return Unchanged;
            published = time;
            return Changed;
        }

        public void Attached(IInternalMultiplayerControl control)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            OperationResult<IInternalSystemChannel> opened;
            try
            {
                opened = control.OpenSystemChannel(InternalSystemChannelKind.Time);
            }
            catch (Exception exception)
            {
                opened = OperationResult<IInternalSystemChannel>.Failure(ModErrorCode.External, exception.GetType().Name);
            }

            if (opened.TryGetValue(out var opening))
            {
                channel = opening;
                seenGeneration = opening.Generation;
                channelFailureLogged = false;
                return;
            }

            if (!channelFailureLogged)
            {
                log.Warn("Multiplayer: the session time channel could not be opened (" + opened.ErrorCode +
                         "), so players will not follow this game's time when it hosts, nor this game the host's when it joins; restart the game before hosting or joining.");
            }

            channelFailureLogged = true;
        }

        public void RealmChanged(NetRealm previous, NetRealm next)
        {
            realm = next;
            behind.Clear();

            // A player that joins runs at real time until the host's time arrives, and one that leaves forgets the host's time:
            // from then on this game runs at its own.
            if (previous == NetRealm.Client || next == NetRealm.Client)
            {
                hostTime = default;
                HasHostTime = false;
            }

            // A new host sends its time to the first player that joins, and to everyone with each change after.
            if (next == NetRealm.ListenHost) sentOnce = false;
            PauseGuard.RealmChanged(next);
        }

        public void Detaching(IInternalMultiplayerControl control)
        {
            var current = channel;
            channel = null;
            behind.Clear();
            try
            {
                current?.Dispose();
            }
            catch (Exception)
            {
                // The provider is detaching anyway; its channels go with it.
            }
        }

        /// <summary>A joined player takes what its host sent, before the mods' Update, so Chronos follows it that frame.</summary>
        internal void Receive()
        {
            var current = channel;
            if (current == null) return;
            while (current.TryReceive(out var message))
            {
                if (SessionTimeWire.TryRead(message.Payload, out var time))
                {
                    hostTime = time;
                    HasHostTime = true;
                    continue;
                }

                Refused++;
                if (!hostRefusalLogged) log.Warn(HostTimeRefused);
                hostRefusalLogged = true;
            }
        }

        /// <summary>A listen host sends its time, after the mods' Update and before the flush. Nothing goes at rest.</summary>
        internal void Send()
        {
            var current = channel;
            if (current == null || !current.IsOpen) return;
            RefusePlayers(current);
            var now = clock();
            var generation = current.Generation;
            var toEveryone = generation != seenGeneration;
            seenGeneration = generation;
            if (!toEveryone && sentOnce && !published.Equals(lastSent) && now - lastSentMicros >= IntervalMicros) toEveryone = true;
            if (toEveryone)
            {
                // A stream started, restarted or ended, or the time changed and the interval has passed. Everyone hears the
                // newest time; a player whose window is full is named, and gets it once there is room.
                SessionTimeWire.Write(buffer, published);
                skipped.Clear();
                var sent = current.Send(new ArraySegment<byte>(buffer), InternalSystemTarget.Everyone, skipped);
                if (sent.TryGetValue(out var recipients)) Sent += recipients;
                for (var index = 0; index < skipped.Count; index++)
                {
                    if (!behind.Contains(skipped[index])) behind.Add(skipped[index]);
                }

                lastSent = published;
                lastSentMicros = now;
                sentOnce = true;
            }
            else if (!sentOnce)
            {
                // Nobody has joined this host yet: the first player hears the time with its stream's start.
                lastSent = published;
                sentOnce = true;
            }

            CatchUp(current);
        }

        /// <summary>Sends the newest time to each player a full window skipped, once its window has room.</summary>
        private void CatchUp(IInternalSystemChannel current)
        {
            if (behind.Count == 0) return;
            SessionTimeWire.Write(buffer, published);
            for (var index = behind.Count - 1; index >= 0; index--)
            {
                var player = behind[index];
                if (current.GetGeneration(player) == 0)
                {
                    behind.RemoveAt(index);
                    continue;
                }

                var only = InternalSystemTarget.Only(player);
                if (!current.CanSend(only, SessionTimeWire.Length)) continue;
                var sent = current.Send(new ArraySegment<byte>(buffer), only);
                if (!sent.TryGetValue(out var recipients)) continue;
                Sent += recipients;
                behind.RemoveAt(index);
            }
        }

        /// <summary>
        /// Takes, and refuses, anything a player sent on the channel. The engine ends such a player before its message
        /// arrives, so this only ever meets a provider that does not, and keeps it from holding the player's window.
        /// </summary>
        private void RefusePlayers(IInternalSystemChannel current)
        {
            while (current.TryReceive(out _))
            {
                Refused++;
                if (!playerRefusalLogged) log.Warn(PlayerTimeRefused);
                playerRefusalLogged = true;
            }
        }

        private static long ReadMonotonic() => Monotonic.Elapsed.Ticks / 10;
    }
}
