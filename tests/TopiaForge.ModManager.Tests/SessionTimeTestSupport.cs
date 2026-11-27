using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Runs the pause and time suites (WS3.13): the session's time and its wire, the pause guard, and Chronos' multiplayer
    /// rules and bridge.
    /// </summary>
    internal static class SessionTimeTests
    {
        internal static void Run()
        {
            SessionTimeWireTests.Run();
            MultiplayerSessionClockTests.Run();
            MultiplayerPauseGuardTests.Run();
            ChronosNetRulesTests.Run();
            ChronosNetBridgeTests.Run();
            Console.WriteLine("Pause and time tests passed.");
        }
    }

    /// <summary>
    /// One peer as ModRuntime composes it for time: a composition root over a loopback control, its replication services,
    /// the game's clock, and the clock the session's send rate is measured by.
    /// </summary>
    internal sealed class TimePeer
    {
        internal const string ModId = "io.example.time";

        internal TimePeer(LoopbackControl control, ManualClock clock)
        {
            Control = control;
            Log = new RecordingRuntimeLog();
            Runtime = new MultiplayerRuntime(() => Control, new MultiplayerRuntimeHost("2545", "0.2.0", Log), Log);
            Runtime.SessionTime.Clock = clock.Read;
            Runtime.GameClock = Game;
            Replication = new MultiplayerReplication(Runtime);
        }

        internal LoopbackControl Control { get; }

        internal RecordingRuntimeLog Log { get; }

        internal MultiplayerRuntime Runtime { get; }

        internal MultiplayerReplication Replication { get; }

        internal FakeGameClock Game { get; } = new FakeGameClock();

        internal OwnerModLifetime Lifetime { get; } = new OwnerModLifetime();

        internal MultiplayerSessionClock Time => Runtime.SessionTime;

        internal LoopbackChannel? Channel => Control.Channel(InternalSystemChannelKind.Time);

        /// <summary>Gets or sets what runs between the composition root's steps, where the mods' Update would.</summary>
        internal Action? Mods { get; set; }

        /// <summary>Binds a replication seam for the test mod, running in <paramref name="context"/>.</summary>
        internal RuntimeReplication Seam(
            ModExecutionContext context = ModExecutionContext.Process,
            ModNetworkClass networkClass = ModNetworkClass.Shared,
            IModLifetime? lifetime = null) =>
            Replication.Bind(ModId, new ModNetProfile(networkClass, context), lifetime ?? Lifetime);

        /// <summary>One frame as ModRuntime runs it: the pump and receive, the mods' Update, the send and flush, then LateUpdate.</summary>
        internal void Frame()
        {
            Runtime.BeforeModsUpdate(0.016f);
            Mods?.Invoke();
            Runtime.AfterModsUpdate(0.016f);
            Runtime.BeforeModsLateUpdate(new GameTimeSample(GameLoopPhase.Late, 0.016f, 0.016f, 0d, 0));
        }
    }

    /// <summary>A listen host and its players over the loopback network, every peer reading one clock the test moves.</summary>
    internal sealed class TimeRig
    {
        internal TimeRig(int players)
        {
            Host = new TimePeer(Network.Host, Clock);
            if (!Host.Runtime.Connect()) throw new InvalidOperationException("The host did not attach.");
            for (var index = 1; index <= players; index++)
            {
                var player = new TimePeer(Network.AddPlayer("p" + index), Clock);
                if (!player.Runtime.Connect()) throw new InvalidOperationException("A player did not attach.");
                Players.Add(player);
            }
        }

        internal LoopbackNetPair Network { get; } = new LoopbackNetPair();

        internal ManualClock Clock { get; } = new ManualClock();

        internal TimePeer Host { get; }

        internal List<TimePeer> Players { get; } = new List<TimePeer>();

        internal IEnumerable<TimePeer> Peers
        {
            get
            {
                yield return Host;
                foreach (var player in Players) yield return player;
            }
        }

        /// <summary>Opens the host's game to other players and runs a frame, so its realm is a listen host.</summary>
        internal void StartHosting()
        {
            Network.StartHosting();
            Frames(1);
        }

        internal void Join(TimePeer player)
        {
            Network.Join(player.Control);
            Frames(1);
        }

        /// <summary>Runs frames of 16 ms: the host first, then each player, as the network delivers at each pump.</summary>
        internal void Frames(int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                foreach (var peer in Peers) peer.Frame();
                Clock.Advance(16_000);
            }
        }
    }
}
