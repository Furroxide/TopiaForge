using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        /// <summary>
        /// Pause and time over the real runtime (WS3.13): three games over the loopback network, each loading the real Chronos
        /// package and a gamemode's package that resolves time control through its server, world and participant scopes, as
        /// a gamemode's controllers do. Every game reads one network clock the test moves, 16 ms a frame, and drives its own
        /// game clock, whose pause menu works as Robotopia's does.
        /// </summary>
        private static void TestPauseAndTime(string root)
        {
            TestTimeAcrossRuntimes(root);
            TestLeavingAsTheMenuCloses(root);
            TestTimeFramesCostNothing(root);
            Console.WriteLine("ModRuntime pause and time tests passed.");
        }

        private static void TestTimeAcrossRuntimes(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new TimeGame(Path.Combine(root, "time-host"), network.Host, clock);
            var player = new TimeGame(Path.Combine(root, "time-player"), network.AddPlayer("p1"), clock);
            var late = new TimeGame(Path.Combine(root, "time-late"), network.AddPlayer("p2"), clock);
            var games = new[] { host, player, late };
            try
            {
                foreach (var game in games)
                {
                    game.Load();
                    game.Attach();
                }

                // Single player: every part of the gamemode may hold time. Opening the game to other players ends the
                // participant's slow, and the shared world runs at real time.
                var tools = host.Participant.Slow("tools", 0.5f);
                Assert(tools.Succeeded && host.Clock.Scale == 0.5f, "offline the participant controller slows the world (" + tools.ErrorMessage + ")");
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Time", 4)).Succeeded, "the host opens its game");
                TimeFrames(games, clock, 2);
                Assert(host.Runtime.Multiplayer.Realm == NetRealm.ListenHost && !tools.Value!.IsActive && host.Clock.Scale == 1f,
                    "hosting, the participant's slow ends and the world runs at real time (" + host.Clock.Scale + ")");

                Assert(player.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "a player joins");
                Assert(TimeRunUntil(games, clock, () => player.Runtime.Multiplayer.Realm == NetRealm.Client && player.Time.HasHostTime, 4),
                    "the player is in the session and hears the host's time");

                // The host's own pause menu, at real time: the menu opens, and the shared world runs on.
                var worldTime = host.Clock.Time;
                host.Clock.OpenPauseMenu();
                TimeFrames(games, clock, 5);
                Assert(host.Clock.Scale == 1f && host.Clock.Time - worldTime > 0.05f, "the host's menu opens, and its world runs on at real time");
                host.Clock.ClosePauseMenu();

                // The gamemode's server controller freezes the shared world; the player follows within a tenth of a second.
                var round = host.Server.Freeze("round");
                Assert(round.Succeeded && host.Clock.Scale == 0f, "the server controller freezes the host's world (" + round.ErrorMessage + ")");
                Assert(TimeRunUntil(games, clock, () => player.Clock.Scale == 0f, 8), "the player's world freezes with the host's");
                Assert(player.Participant.IsFrozen && player.Participant.Mode == TimeMode.Paused && player.Time.Current.Mode == InternalTimeMode.Paused,
                    "and its Chronos reports the host's freeze");

                // Nothing else may change the shared world's time: the host's participant controller, its world and package
                // code, and every part of a joined player, its participant controller first.
                foreach (var refused in new[] { host.Participant, host.World, host.Package })
                {
                    ExpectRefused(refused, "only the gamemode's server controller can freeze, slow or step it, with the time control it resolves from its session's Context", "on the host");
                }

                foreach (var refused in new[] { player.Participant, player.World, player.Server, player.Package })
                {
                    ExpectRefused(refused, "follows the host's time", "on a joined player");
                }

                Assert(host.Clock.Scale == 0f && player.Clock.Scale == 0f && host.Time.Published.IsFrozen, "and the refusals changed nothing");
                var run = player.Participant.ExemptPlayer("run");
                Assert(run.Succeeded, "exempting a player's own player changes no one's time, so it is never refused (" + run.ErrorMessage + ")");
                run.Value!.Release();

                // The server controller steps the frozen world a beat: one frame of the host's own runs, then it is frozen again.
                var step = host.Server.Step(0.05f);
                Assert(step.Succeeded && step.Value && host.Clock.Scale == 1f, "the server controller steps the frozen world (" + step.ErrorMessage + ")");
                TimeFrames(games, clock, 1);
                Assert(host.Clock.Scale == 0f && host.Time.Published.IsFrozen, "for one frame, and the session's time stays frozen");

                // A slow replaces the freeze, and the player follows it.
                round.Value!.Release();
                var slow = host.Server.Slow("slow", 0.5f);
                Assert(slow.Succeeded && host.Clock.Scale == 0.5f, "the server controller slows the world (" + slow.ErrorMessage + ")");
                Assert(TimeRunUntil(games, clock, () => player.Clock.Scale == 0.5f, 8), "the player follows the slow");

                // The host's menu over a slow: the shared world runs on at the session's slow, here and on the player.
                worldTime = host.Clock.Time;
                host.Clock.OpenPauseMenu();
                TimeFrames(games, clock, 10);
                Assert(host.Clock.Scale == 0.5f && player.Clock.Scale == 0.5f && host.Time.Current.Scale == 0.5f && host.Clock.Time - worldTime > 0.05f,
                    "behind the host's menu the world runs on at the session's slow, here and on the player (" + host.Clock.Scale + ")");
                host.Clock.ClosePauseMenu();
                TimeFrames(games, clock, 1);
                Assert(host.Clock.Scale == 0.5f, "and closing the menu changes nothing");

                // The player's menu: its world keeps the host's time, even as that changes while the menu is open, and the
                // stale scale the menu kept is corrected as it closes.
                worldTime = player.Clock.Time;
                player.Clock.OpenPauseMenu();
                TimeFrames(games, clock, 1);
                Assert(player.Clock.Scale == 0.5f, "a joined player's menu leaves its world running at the host's time");
                var slower = host.Server.Slow("slower", 0.5f);
                Assert(TimeRunUntil(games, clock, () => player.Clock.Scale == 0.25f, 8),
                    "the host slows further while the player's menu is open, and the player's world follows (" + player.Clock.Scale + ")");
                TimeFrames(games, clock, 5);
                Assert(player.Clock.Scale == 0.25f && player.Clock.Time - worldTime > 0.02f,
                    "and runs on behind the menu (" + (player.Clock.Time - worldTime) + " s of world time)");
                player.Clock.ClosePauseMenu();
                Assert(player.Clock.Scale == 0.5f, "closing, the player's menu puts back the stale 0.5 it kept");
                TimeFrames(games, clock, 1);
                Assert(player.Clock.Scale == 0.25f, "and the session's 0.25 is back that frame");

                // A late joiner runs at the host's time from its first frame in the session, though the host changed nothing.
                var sent = host.Time.Sent;
                Assert(late.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "a late player joins");
                TimeFrames(games, clock, 1);
                Assert(late.Runtime.Multiplayer.Realm == NetRealm.Client && late.Clock.Scale == 0.25f && late.Participant.WorldScale == 0.25f,
                    "a late joiner runs at the host's time from its first frame in the session (" + late.Clock.Scale + ")");
                Assert(host.Time.Sent == sent + 2, "from the time the host sent each of its players as it joined (" + (host.Time.Sent - sent) + " messages)");

                // The player leaves with its menu open: single player's pause comes back, then the player's own time.
                player.Clock.OpenPauseMenu();
                TimeFrames(games, clock, 1);
                Assert(player.Clock.Scale == 0.25f, "the player's menu is open over the session's slow");
                Assert(player.Runtime.Multiplayer.Leave().Succeeded, "the player leaves with its menu open");
                TimeFrames(games, clock, 1);
                Assert(player.Runtime.Multiplayer.Realm == NetRealm.Offline && player.Clock.Scale == 0f,
                    "back in single player, its menu freezes its world, as single player's does (" + player.Clock.Scale + ")");
                TimeFrames(games, clock, 5);
                Assert(player.Clock.Scale == 0f, "and keeps it frozen while it stays open");
                player.Clock.ClosePauseMenu();
                TimeFrames(games, clock, 1);
                Assert(player.Clock.Scale == 1f, "closing it, the player runs at its own time again, not the session's slow the menu kept");
                var solo = player.Participant.Freeze("solo");
                Assert(solo.Succeeded && player.Clock.Scale == 0f, "in single player its participant controller may freeze time again (" + solo.ErrorMessage + ")");
                solo.Value!.Release();
                Assert(player.Clock.Scale == 1f, "and real time comes back as it lets go");
                player.Clock.ResetCounts();
                player.Clock.OpenPauseMenu();
                TimeFrames(games, clock, 3);
                Assert(player.Clock.Scale == 0f && player.Clock.Touches == 0, "its own menu freezes its world again, and nothing of multiplayer's touches the clock");
                player.Clock.ClosePauseMenu();

                // The host stops hosting with its menu open while its server holds the slows: single player's pause first,
                // then the gamemode's own slow, which it keeps offline. The late joiner is back in single player at real time.
                host.Clock.OpenPauseMenu();
                TimeFrames(games, clock, 1);
                Assert(host.Clock.Scale == 0.25f, "the host's menu is open over the session's slow");
                Assert(host.Runtime.Multiplayer.Leave().Succeeded, "the host stops hosting with its menu open");
                TimeFrames(games, clock, 1);
                Assert(host.Runtime.Multiplayer.Realm == NetRealm.Offline && host.Clock.Scale == 0f, "in single player the host's menu freezes its world");
                Assert(late.Runtime.Multiplayer.Realm == NetRealm.Offline && late.Clock.Scale == 1f, "and the late joiner plays on alone at real time");
                TimeFrames(games, clock, 5);
                Assert(host.Clock.Scale == 0f, "the world stays frozen while the menu stays open");
                host.Clock.ClosePauseMenu();
                TimeFrames(games, clock, 1);
                Assert(host.Clock.Scale == 0.25f && slow.Value!.IsActive && slower.Value!.IsActive, "closing it, the host runs at its gamemode's own slow");
                slow.Value!.Release();
                slower.Value!.Release();
                Assert(host.Clock.Scale == 1f, "and real time comes back as its server lets go");
                foreach (var game in games)
                {
                    Assert(game.Logger.Errors.Count == 0, "no game logged an error (" + string.Join(" | ", game.Logger.Errors) + ")");
                }
            }
            finally
            {
                foreach (var game in games) game.Unload();
            }

            foreach (var game in games)
            {
                Assert(game.Logger.Errors.Count == 0 && game.Clock.Scale == 1f, "every game unloads cleanly at real time (" + string.Join(" | ", game.Logger.Errors) + ")");
            }
        }

        /// <summary>
        /// A pause menu that closes in the very frame its game leaves the session, after the session's time changed behind it:
        /// the menu puts back the freeze it kept as it opened, which no longer holds anything. The player, and then the host,
        /// each run at their own time from then on, as single player would after its own menu, never frozen with no menu open.
        /// </summary>
        private static void TestLeavingAsTheMenuCloses(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new TimeGame(Path.Combine(root, "time-close-host"), network.Host, clock);
            var player = new TimeGame(Path.Combine(root, "time-close-player"), network.AddPlayer("p1"), clock);
            var games = new[] { host, player };
            try
            {
                foreach (var game in games)
                {
                    game.Load();
                    game.Attach();
                }

                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Time", 4)).Succeeded, "the host opens its game");
                TimeFrames(games, clock, 2);
                Assert(player.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "a player joins");
                Assert(TimeRunUntil(games, clock, () => player.Runtime.Multiplayer.Realm == NetRealm.Client && player.Time.HasHostTime, 4),
                    "the player is in the session and hears the host's time");

                // The server controller freezes the shared world, and the player opens its menu over that freeze, which the menu
                // keeps to put back. The server lets go while the menu is open, and the player's world runs on behind it.
                var round = host.Server.Freeze("round").Value!;
                Assert(TimeRunUntil(games, clock, () => player.Clock.Scale == 0f, 8), "the player's world freezes with the host's");
                player.Clock.OpenPauseMenu();
                TimeFrames(games, clock, 1);
                round.Release();
                Assert(TimeRunUntil(games, clock, () => player.Clock.Scale == 1f, 8), "the server lets go, and behind its menu the player's world runs on");

                // The player leaves, and its menu closes in the frame the leave takes effect, putting back the freeze it kept.
                Assert(player.Runtime.Multiplayer.Leave().Succeeded, "the player leaves");
                player.Clock.ClosePauseMenu();
                Assert(player.Clock.Scale == 0f, "its menu closes, putting back the freeze it kept as it opened");
                TimeFrames(games, clock, 5);
                Assert(player.Runtime.Multiplayer.Realm == NetRealm.Offline && player.Clock.Scale == 1f && !player.Clock.MenuOpen,
                    "back in single player with no menu open, the player runs at its own time, not that stale freeze (" + player.Clock.Scale + ")");

                // The host's own menu, the same way: opened over the server's freeze, which the server lets go of while it is open,
                // then closed in the frame the host stops hosting.
                round = host.Server.Freeze("round again").Value!;
                TimeFrames(games, clock, 1);
                host.Clock.OpenPauseMenu();
                TimeFrames(games, clock, 1);
                round.Release();
                TimeFrames(games, clock, 1);
                Assert(host.Clock.Scale == 1f, "behind the host's menu the world runs on once its server lets go");
                Assert(host.Runtime.Multiplayer.Leave().Succeeded, "the host stops hosting");
                host.Clock.ClosePauseMenu();
                TimeFrames(games, clock, 5);
                Assert(host.Runtime.Multiplayer.Realm == NetRealm.Offline && host.Clock.Scale == 1f,
                    "and back in single player with no menu open, the host runs at its own time too (" + host.Clock.Scale + ")");
                foreach (var game in games)
                {
                    Assert(game.Logger.Errors.Count == 0, "no game logged an error (" + string.Join(" | ", game.Logger.Errors) + ")");
                }
            }
            finally
            {
                foreach (var game in games) game.Unload();
            }

            foreach (var game in games)
            {
                Assert(game.Logger.Errors.Count == 0 && game.Clock.Scale == 1f, "both games unload cleanly at real time (" + string.Join(" | ", game.Logger.Errors) + ")");
            }
        }

        /// <summary>
        /// With the real Chronos loaded, a frame costs nothing: zero bytes over 10,000 frames offline without a provider and
        /// with a solo one, where nothing of multiplayer touches the game's clock even behind the pause menu; and online at
        /// rest, a host and its player at real time, then holding a slow behind both pause menus. The network clock stands
        /// still meanwhile, so no other stage's periodic send runs.
        /// </summary>
        private static void TestTimeFramesCostNothing(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new TimeGame(Path.Combine(root, "time-frames-host"), network.Host, clock);
            var player = new TimeGame(Path.Combine(root, "time-frames-player"), network.AddPlayer("p1"), clock);
            var games = new[] { host, player };
            try
            {
                host.Load();
                player.Load();
                host.Clock.OpenPauseMenu();
                host.Clock.ResetCounts();
                var offline = MeasureTimeFrames(host);
                Assert(offline == 0 && host.Clock.Touches == 0 && host.Clock.Scale == 0f,
                    "offline, a frame allocates nothing and never touches the clock behind the menu (" + offline + " bytes, " + host.Clock.Touches + " touches)");
                host.Clock.ClosePauseMenu();

                host.Attach();
                player.Attach();
                host.Clock.OpenPauseMenu();
                host.Clock.ResetCounts();
                var solo = MeasureTimeFrames(host);
                Assert(solo == 0 && host.Clock.Touches == 0, "and so does a solo frame (" + solo + " bytes, " + host.Clock.Touches + " touches)");
                host.Clock.ClosePauseMenu();

                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Time", 4)).Succeeded, "the host opens its game");
                TimeFrames(games, clock, 2);
                Assert(player.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "a player joins");
                Assert(TimeRunUntil(games, clock, () => player.Time.HasHostTime, 4), "the player hears the host's time");
                var atRest = MeasureTimeFrames(host, player);
                Assert(atRest == 0, "online at rest, a host's frame and its player's allocate nothing (" + atRest + " bytes)");

                var slow = host.Server.Slow("slow", 0.5f).Value!;
                Assert(TimeRunUntil(games, clock, () => player.Clock.Scale == 0.5f, 8), "the player follows the host's slow");
                host.Clock.OpenPauseMenu();
                player.Clock.OpenPauseMenu();
                var held = MeasureTimeFrames(host, player);
                Assert(held == 0 && host.Clock.Scale == 0.5f && player.Clock.Scale == 0.5f,
                    "and so do frames holding a slow behind both menus, which keep the world running (" + held + " bytes)");
                Console.WriteLine("Pause and time frames through ModRuntime with Chronos loaded: " + offline + " bytes offline, " + solo + " solo, " + atRest +
                                  " online at rest and " + held + " online holding a slow behind both pause menus, over 10,000 frames each.");
                host.Clock.ClosePauseMenu();
                player.Clock.ClosePauseMenu();
                slow.Release();
                Assert(player.Runtime.Multiplayer.Leave().Succeeded && host.Runtime.Multiplayer.Leave().Succeeded, "both games go back to single player");
                TimeFrames(games, clock, 2);
            }
            finally
            {
                foreach (var game in games) game.Unload();
            }

            foreach (var game in games)
            {
                Assert(game.Logger.Errors.Count == 0 && game.Clock.Scale == 1f, "both games unload cleanly at real time (" + string.Join(" | ", game.Logger.Errors) + ")");
            }
        }

        /// <summary>Every way <paramref name="time"/> could change the shared world's time is refused, in the game's own words.</summary>
        private static void ExpectRefused(ITimeControlService time, string says, string where)
        {
            Refused(time.Freeze("mine"), "a freeze " + where);
            Refused(time.Slow("mine", 0.25f), "a slow " + where);
            Refused(time.SetDriver("mine", new HalfSpeed()), "a driver " + where);
            Refused(time.Step(0.1f), "a step " + where);
            Refused(time.StepFixed(1), "a fixed step " + where);
            Refused(time.BeginTurnBased("mine", new TurnSchedulerOptions()), "turn-based play " + where);

            void Refused<T>(OperationResult<T> result, string what) where T : notnull =>
                Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.NotAuthoritative && (result.ErrorMessage ?? "").Contains(says, StringComparison.Ordinal),
                    what + " is refused as not authoritative (" + result.ErrorCode + ": " + result.ErrorMessage + ")");
        }

        private static void TimeFrames(IEnumerable<TimeGame> games, WorldTestClock clock, int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                foreach (var game in games) game.Frame();
                clock.Now += 16_000;
            }
        }

        private static bool TimeRunUntil(IEnumerable<TimeGame> games, WorldTestClock clock, Func<bool> condition, int maximum)
        {
            for (var frame = 0; frame < maximum; frame++)
            {
                if (condition()) return true;
                TimeFrames(games, clock, 1);
            }

            return condition();
        }

        /// <summary>Counts what 10,000 frames of each game allocate, the fewest bytes of up to three passes after one to warm.</summary>
        private static long MeasureTimeFrames(params TimeGame[] games)
        {
            TimeFramePass(games, 1_000);
            var fewest = long.MaxValue;
            for (var pass = 0; pass < 3 && fewest != 0; pass++) fewest = Math.Min(fewest, TimeFramePass(games, 10_000));
            return fewest;
        }

        /// <summary>Runs frames with a zero frame time, which keeps the runtime's once-a-second capability refresh out.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long TimeFramePass(TimeGame[] games, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++)
            {
                for (var game = 0; game < games.Length; game++)
                {
                    games[game].Runtime.DispatchUpdate(0f);
                    games[game].Game.RaiseLateUpdate();
                }
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        private sealed class HalfSpeed : ITimeDriver
        {
            public float ComputeScale(in TimeSignal signal) => 0.5f;
        }
    }
}
