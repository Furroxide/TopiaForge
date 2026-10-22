using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The session's time at the runtime (WS3.13): who publishes it in each realm, how it reaches the players and a late
    /// joiner, its rate, a player a full window skipped, hostile peers on either side, the channel's lifetime, and frames at
    /// rest that send and allocate nothing.
    /// </summary>
    internal static class MultiplayerSessionClockTests
    {
        private static readonly InternalSessionTime Slow = new InternalSessionTime(0.5f, InternalTimeMode.Slowed);
        private static readonly InternalSessionTime Frozen = new InternalSessionTime(0f, InternalTimeMode.Paused);

        internal static void Run()
        {
            PublishingFollowsTheRealm();
            TheHostsTimeReachesEveryPlayerAndALateJoinerAtOnce();
            ChangesGoAtMostTenTimesASecondAndEndOnTheNewest();
            APlayerAFullWindowSkippedCatchesUp();
            HostileTimeIsRefusedOnEitherSide();
            TheChannelFollowsTheProvider();
            FramesAtRestSendAndAllocateNothing();
        }

        private static void PublishingFollowsTheRealm()
        {
            var (services, control, runtime) = RuntimeReplicationTests.Services();
            var log = (RecordingRuntimeLog)runtime.Log;
            var seam = services.Bind("io.example.chronos", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), new OwnerModLifetime());
            That(seam.GameClock == null && seam.SessionTime.Equals(InternalSessionTime.Realtime), "without a game clock there is none to drive, and time is real time");
            var game = new FakeGameClock();
            runtime.GameClock = game;
            That(ReferenceEquals(seam.GameClock, game), "the seam hands first-party providers the runtime's game clock");

            // Offline the time is kept, so a game opened to other players later starts from it.
            var published = seam.PublishSessionTime(Slow);
            That(published.Succeeded && published.Value && seam.SessionTime.Equals(Slow) && runtime.SessionTime.Published.Equals(Slow),
                "offline, this process's time authority publishes its time");
            var again = seam.PublishSessionTime(Slow);
            That(again.Succeeded && !again.Value, "publishing the same time again changes nothing");

            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(seam.SessionTime.Equals(Slow), "a host starts from the time it had");
            That(seam.PublishSessionTime(Frozen).Value && seam.SessionTime.Equals(Frozen), "and publishes the session's time");

            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            control.Role = InternalSessionRole.Client;
            Frame(runtime);
            That(seam.SessionTime.Equals(InternalSessionTime.Realtime) && !runtime.SessionTime.HasHostTime,
                "a joined player runs at real time until its host's time arrives");
            var refused = seam.PublishSessionTime(Slow);
            That(refused.ErrorCode == ModErrorCode.NotAuthoritative && refused.ErrorMessage == MultiplayerSessionClock.FollowsHost &&
                 runtime.SessionTime.Published.Equals(Frozen), "and never publishes: it follows its host");

            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            That(seam.SessionTime.Equals(Frozen), "leaving, it runs at its own time again");

            var stopping = new OwnerModLifetime();
            var stopped = services.Bind("io.example.chronos", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), stopping);
            stopping.BeginStop();
            That(stopped.PublishSessionTime(Slow).ErrorCode == ModErrorCode.Cancelled && runtime.SessionTime.Published.Equals(Frozen),
                "a stopping context publishes nothing");
            That(log.Lines.Count(line => line.Contains("session time channel could not be opened", StringComparison.Ordinal)) == 1,
                "a provider without the channel is reported once, at attach (" + string.Join(" | ", log.Lines) + ")");
        }

        private static void TheHostsTimeReachesEveryPlayerAndALateJoinerAtOnce()
        {
            var rig = new TimeRig(2);
            var (first, late) = (rig.Players[0], rig.Players[1]);
            var host = rig.Host.Seam();
            That(host.PublishSessionTime(Slow).Value, "the host slows its world before opening it");
            rig.StartHosting();
            rig.Join(first);
            rig.Frames(1);
            That(first.Time.HasHostTime && first.Time.Current.Equals(Slow) && first.Seam().SessionTime.Equals(Slow),
                "a player that joins follows the host's time at once (" + first.Time.Current + ")");

            // A tenth of a second after the last send, a change goes at once.
            rig.Frames(7);
            var log = rig.Host.Channel!.Log = new System.Collections.Generic.List<(ParticipantId To, byte[] Payload)>();
            That(host.PublishSessionTime(Frozen).Value, "the host freezes its world");
            rig.Frames(1);
            That(first.Time.Current.Equals(Frozen) && log.Count == 1, "the player follows that frame, from one message (" + log.Count + ")");

            rig.Join(late);
            rig.Frames(1);
            That(late.Time.HasHostTime && late.Time.Current.Equals(Frozen), "a late joiner follows the time the host has now, at once");

            var before = log.Count;
            rig.Network.Rejoin(first.Control);
            rig.Frames(2);
            That(log.Count > before && log.Skip(before).Any(sent => sent.To.Equals(first.Control.Id)) && first.Time.Current.Equals(Frozen),
                "a player that rejoins hears the time again on its new stream");
            before = log.Count;
            rig.Network.ReplaceSession();
            rig.Frames(2);
            That(log.Skip(before).Select(sent => sent.To).Distinct().Count() == 2 && late.Time.Current.Equals(Frozen),
                "and so does everyone when the host replaces the session");

            rig.Network.Leave(first.Control);
            rig.Frames(1);
            That(!first.Time.HasHostTime && first.Time.Current.Equals(InternalSessionTime.Realtime) && first.Runtime.Realm == NetRealm.Offline,
                "a player that leaves forgets the host's time and runs at its own");
            That(rig.Peers.All(peer => !peer.Log.Lines.Any(line => line.StartsWith("warn:", StringComparison.Ordinal) || line.StartsWith("error:", StringComparison.Ordinal))),
                "and nothing was refused or failed on the way (" + string.Join(" | ", rig.Peers.SelectMany(peer => peer.Log.Lines)) + ")");
        }

        private static void ChangesGoAtMostTenTimesASecondAndEndOnTheNewest()
        {
            var rig = new TimeRig(1);
            var player = rig.Players[0];
            var host = rig.Host.Seam();
            rig.StartHosting();
            rig.Join(player);
            rig.Frames(2);
            var log = rig.Host.Channel!.Log = new System.Collections.Generic.List<(ParticipantId To, byte[] Payload)>();
            var sentBefore = rig.Host.Time.Sent;

            // A driver that changes the scale every frame, for 62 frames of 16 ms: about a second.
            var last = InternalSessionTime.Realtime;
            for (var frame = 0; frame < 62; frame++)
            {
                last = new InternalSessionTime(0.9f - frame * 0.01f, InternalTimeMode.Slowed);
                rig.Host.Mods = () => host.PublishSessionTime(last);
                rig.Frames(1);
            }

            rig.Host.Mods = null;
            That(log.Count >= 9 && log.Count <= 11, "a change every frame for a second goes out ten times (" + log.Count + " messages)");
            var gaps = log.Count;
            rig.Frames(7);
            That(player.Time.Current.Equals(last) && log.Count == gaps + 1, "the newest change goes out once the interval passes, so the player ends on it");
            rig.Frames(120);
            That(log.Count == gaps + 1 && rig.Host.Time.Sent == sentBefore + log.Count, "at rest nothing goes (" + rig.Host.Time.Sent + " sent in all)");

            // A change after a quiet spell goes at once.
            host.PublishSessionTime(Frozen);
            rig.Frames(1);
            That(player.Time.Current.Equals(Frozen) && log.Count == gaps + 2, "a change after a quiet spell goes at once");
        }

        private static void APlayerAFullWindowSkippedCatchesUp()
        {
            var rig = new TimeRig(2);
            var (stalled, steady) = (rig.Players[0], rig.Players[1]);
            var host = rig.Host.Seam();
            rig.StartHosting();
            rig.Join(stalled);
            rig.Join(steady);
            rig.Frames(2);
            rig.Network.HostWindowMessages = 2;

            // One player's game stalls and takes nothing, so its window fills; the other keeps up throughout.
            stalled.Control.Stalled = true;
            var times = new[] { 0.9f, 0.8f, 0.7f, 0.6f, 0.4f }.Select(scale => new InternalSessionTime(scale, InternalTimeMode.Slowed)).ToArray();
            foreach (var time in times)
            {
                host.PublishSessionTime(time);
                rig.Frames(7);
                That(steady.Time.Current.Equals(time), "a steady player follows every change (" + time + ")");
            }

            That(rig.Host.Channel!.GetStatistics().Refused > 0 && !stalled.Time.Current.Equals(times[^1]), "the stalled player's full window skipped it");
            stalled.Control.Stalled = false;
            rig.Frames(4);
            That(stalled.Time.Current.Equals(times[^1]), "once it takes its messages again it catches up on the newest time (" + stalled.Time.Current + ")");
        }

        private static void HostileTimeIsRefusedOnEitherSide()
        {
            var rig = new TimeRig(1);
            var player = rig.Players[0];
            var host = rig.Host.Seam();
            rig.StartHosting();
            host.PublishSessionTime(Slow);
            rig.Join(player);
            rig.Frames(1);
            That(player.Time.Current.Equals(Slow), "the player follows the host's slow");

            // A hostile host sends scales outside the bounds every player keeps: each is refused, and the player keeps its time.
            var hostId = rig.Network.Host.Id;
            foreach (var bad in new[] { Bytes(2f, 1), Bytes(float.NaN, 1), Bytes(float.PositiveInfinity, 3), Bytes(-1f, 3), Bytes(0.5f, 9), Bytes(0.5f, 0), new byte[] { 1, 1, 0 } })
            {
                player.Channel!.Inject(hostId, bad);
            }

            rig.Frames(1);
            That(player.Time.Current.Equals(Slow) && player.Time.Refused == 7, "every time outside the bounds is refused (" + player.Time.Refused + ")");
            That(player.Log.Lines.Count(line => line == "warn: " + MultiplayerSessionClock.HostTimeRefused) == 1, "and said once");
            player.Channel!.Inject(hostId, Bytes(0.25f, 1));
            rig.Frames(1);
            That(player.Time.Current.Equals(new InternalSessionTime(0.25f, InternalTimeMode.Slowed)), "a valid time after them is followed");

            // A player that sends the session's time is refused by the host, which keeps its own.
            rig.Host.Channel!.Inject(player.Control.Id, Bytes(0f, 2));
            rig.Host.Channel!.Inject(player.Control.Id, Bytes(1f, 0));
            rig.Frames(1);
            That(rig.Host.Time.Current.Equals(Slow) && rig.Host.Time.Refused == 2 &&
                 rig.Host.Log.Lines.Count(line => line == "warn: " + MultiplayerSessionClock.PlayerTimeRefused) == 1,
                "a player's time is refused, counted and said once, and the host keeps its own");
            That(rig.Host.Channel!.Waiting == 0, "and taken, so it holds nothing of the player's window");
        }

        private static void TheChannelFollowsTheProvider()
        {
            var rig = new TimeRig(1);
            That(rig.Host.Time.IsOpen && rig.Players[0].Time.IsOpen && rig.Host.Channel != null, "attaching opens the time channel");
            rig.Host.Runtime.Disconnect();
            That(!rig.Host.Time.IsOpen && rig.Network.Host.Channel(InternalSystemChannelKind.Time) == null, "detaching closes it");
            That(rig.Host.Runtime.Connect() && rig.Host.Time.IsOpen, "and attaching again opens it again");
        }

        private static void FramesAtRestSendAndAllocateNothing()
        {
            var rig = new TimeRig(1);
            var player = rig.Players[0];
            rig.Host.Seam().PublishSessionTime(Slow);
            rig.StartHosting();
            rig.Join(player);
            rig.Frames(20);
            var sent = rig.Host.Time.Sent;
            var allocated = Allocated(() =>
            {
                rig.Host.Frame();
                player.Frame();
                rig.Clock.Advance(16_000);
            });
            That(allocated == 0, "a host and a player at rest allocate nothing per frame, the pause guard armed on both (" + allocated + " bytes)");
            That(rig.Host.Time.Sent == sent && player.Time.Current.Equals(Slow), "and send nothing");

            // Offline, with a solo provider attached, the guard never even reads the game's clock.
            var (runtime, control, _) = Runtime(() => SessionPhase.Running);
            var game = new FakeGameClock();
            runtime.GameClock = game;
            runtime.Connect();
            control.Role = InternalSessionRole.Solo;
            var offline = Allocated(() => Frame(runtime));
            That(offline == 0 && game.Touches == 0, "an offline frame allocates nothing and touches no clock (" + offline + " bytes, " + game.Touches + " touches)");
        }

        private static byte[] Bytes(float scale, byte mode)
        {
            var bits = BitConverter.SingleToInt32Bits(scale);
            return new byte[] { SessionTimeWire.Version, SessionTimeWire.TimeType, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24), mode };
        }
    }
}
