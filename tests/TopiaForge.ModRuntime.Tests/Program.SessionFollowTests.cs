using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Sessions;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static readonly string[] FollowCases = { "follow" };

        /// <summary>Session follow across two runtimes runs in a fresh process, as the binding cases do: the installed package's probe is static.</summary>
        private static void TestSessionFollowInFreshProcesses()
        {
            foreach (var name in FollowCases)
            {
                var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
                if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(typeof(Program).Assembly.Location);
                start.ArgumentList.Add("--follow-case");
                start.ArgumentList.Add(name);
                using var child = Process.Start(start) ?? throw new InvalidOperationException("Could not start the isolated session follow harness.");
                var output = child.StandardOutput.ReadToEndAsync();
                var error = child.StandardError.ReadToEndAsync();
                if (!child.WaitForExit(120000)) { child.Kill(true); throw new InvalidOperationException("Session follow child timed out: " + name); }
                Assert(child.ExitCode == 0, "session follow case " + name + ": " + output.Result + error.Result);
                Console.Write(output.Result);
            }
        }

        private static int RunFollowCase(string name)
        {
            if (!FollowCases.Contains(name, StringComparer.Ordinal)) return 2;
            var root = Directory.CreateTempSubdirectory("TopiaForgeFollowTests-").FullName;
            try
            {
                TestSessionFollowAcrossRuntimes(root);
                Console.WriteLine("Session follow case passed: " + name);
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(exception);
                return 1;
            }
            finally
            {
                Environment.SetEnvironmentVariable("TOPIAFORGE_RUNTIME_TEST_TRACE", null);
                TryDelete(root);
            }
        }

        /// <summary>
        /// Session follow (WS3.8) across two real runtimes over the loopback network, each loading the same installed package,
        /// whose gamemode gives every player a participant controller of their own. The host opens its game to other players
        /// and starts the package's target; a player joins from its menu and follows it there: it runs the host's world under
        /// the host's session id, the host's world arrives through the network world, the player's participant starts, and the
        /// gamemode's server controller never runs on it. A join started in game publishes nothing to the launcher. Online,
        /// both games keep running in the background. The host starts its world again and the player follows it into the new
        /// one; at rest neither game allocates; and the player leaves, its main menu loads, and its game's own background
        /// setting comes back exactly.
        /// </summary>
        private static void TestSessionFollowAcrossRuntimes(string root)
        {
            var fixture = NewFixture(root, "follow", "TopiaForge.BindingTestMod.BindingMod", BindingAssembly);
            var manifest = fixture.Manifest;
            manifest.SchemaVersion = 6;
            manifest.Contributions = BindingContributions(manifest.Id);
            manifest.Contributions.Gamemodes[0].Implementation!.Type = "TopiaForge.BindingTestMod.ParticipantFactory";
            WriteBindingReceipt(fixture);
            var profile = new EffectiveProfile("follow-profile", 1, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            var target = new LaunchRequest(manifest.Id + ".target");
            var gamemode = manifest.Contributions.Gamemodes[0].Id;

            // Both games load the one installed package, so its assembly, and the probe in it, load once.
            var guestFixture = new Fixture(new ManagerPaths(Path.Combine(root, "follow-guest")), manifest, fixture.Package, fixture.TracePath,
                new CapturedRuntimeLogger(), new CapturedLoadObserver(), new FakeGameplayHost());
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new WorldGame(fixture, network.Host, clock);
            var guestControl = network.AddPlayer("guest");
            var guest = new WorldGame(guestFixture, guestControl, clock);
            var games = new[] { host, guest };
            var menus = new Dictionary<WorldGame, int> { [host] = 0, [guest] = 0 };
            foreach (var game in games)
            {
                var owner = game;
                game.Runtime.ActivateSessionRuntime(profile, (_, _) =>
                {
                    menus[owner]++;
                    return Task.FromResult(OperationResult<bool>.Success(true));
                });
            }

            try
            {
                foreach (var game in games) game.Start();
                var probe = Assembly.LoadFrom(Path.Combine(fixture.Package.PackagePath, BindingAssembly)).GetType("TopiaForge.BindingTestMod.BindingProbe")!;
                var events = (List<string>)probe.GetField("Events")!.GetValue(null)!;
                probe.GetField("ActiveScopes")!.SetValue(null, (Func<int>)(() =>
                    games.Sum(game => game.Runtime.SessionBindings!.Capture().Contexts.Values.Sum(context => context.ActiveChildScopeCount))));
                foreach (var game in games) game.Game.ScopeCreated = lifetime => probe.GetField("ConstructorLifetime")!.SetValue(null, lifetime);
                var hostSessions = host.Runtime.Sessions;
                var guestSessions = guest.Runtime.Sessions;
                var follower = guest.Runtime.SessionFollow!.Follower;
                var guestProgress = new List<LaunchProgress>();
                var guestOutcomes = new List<LaunchOutcome>();
                guestSessions.Progress += guestProgress.Add;
                guestSessions.Outcome += guestOutcomes.Add;
                Assert(!host.Game.RunInBackground && !guest.Game.RunInBackground, "in single player each game keeps its own setting: not in the background");

                // The host opens its game to other players, then starts the package's target.
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Follow", 4)).Succeeded, "the host opens its game to other players");
                Assert(FollowRunUntil(games, clock, () => host.Runtime.Multiplayer.Realm == NetRealm.ListenHost, 20), "the host hosts");
                var launched = host.Runtime.LaunchTargetAsync(target);
                Assert(FollowRunUntil(games, clock, () => launched.IsCompleted, 200) && launched.Result.Succeeded,
                    "the host's world starts: " + (launched.IsCompleted ? launched.Result.ErrorMessage : "still starting"));
                var first = hostSessions.Current.Identity!.SessionId;
                Assert(host.Game.RunInBackground, "a host keeps running in the background");

                // A player joins from its menu, and follows the host into its world.
                Assert(guest.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "the player joins");
                Assert(FollowRunUntil(games, clock, () => follower.Status.Stage == FollowStage.Running, 600),
                    "the player follows the host's world (" + follower.Status.Stage + ": " + follower.Status.Detail + ")");
                Assert(guestSessions.Current.Phase == SessionPhase.Running && guestSessions.Current.Identity!.SessionId == first && guestSessions.FollowedSessionId == first,
                    "the player runs the host's world under the host's session id");
                Assert(guest.World.Progress.Phase == NetworkWorldPhase.Synchronized && guestControl.Holds == 0 && guestControl.HoldsTaken >= 2,
                    "the host's world arrived through the network world, and both the world's hold and the follow's released (" + guest.World.Progress + ")");
                Assert(events.Count(item => item == "factory:start:" + gamemode) == 1 && events.Count(item => item == "participant:start:" + gamemode) == 2,
                    "the player's participant starts too, and the server controller runs once, on the host: " + string.Join(", ", events));
                var participant = (IModContext)probe.GetField("ParticipantContext")!.GetValue(null)!;
                var seam = ((IInternalReplicationContext)participant).Replication;
                Assert(seam.ExecutionContext == InternalExecutionContext.Participant && seam.Realm == InternalNetRealm.Client,
                    "the player's participant runs as a joined player's");
                Assert(guest.Game.RunInBackground, "while online the player's game keeps running in the background");

                // The host starts its world again: a new world session, which the player follows.
                var again = host.Runtime.LaunchTargetAsync(target);
                Assert(FollowRunUntil(games, clock, () => again.IsCompleted, 200) && again.Result.Succeeded, "the host starts its world again");
                var second = hostSessions.Current.Identity!.SessionId;
                Assert(second != first, "under a new world session");
                Assert(FollowRunUntil(games, clock, () => guestSessions.FollowedSessionId == second && follower.Status.Stage == FollowStage.Running, 600),
                    "the player follows the host's new world (" + follower.Status.Stage + ": " + follower.Status.Detail + ")");
                Assert(follower.Status.IsRefollow && events.Count(item => item == "participant:start:" + gamemode) == 4 && events.Count(item => item == "factory:start:" + gamemode) == 2,
                    "as a follow after the join, its participant starting again, and still no server controller on the player");
                Assert(guestProgress.Count == 0 && guestOutcomes.Count == 0, "a join started in game publishes nothing to the launcher");

                FollowRunUntil(games, clock, () => false, 30);
                var resting = MeasureFollowFrames(games);
                Assert(resting == 0, "at rest, a host and the player following it allocate nothing (" + resting + " bytes over 10,000 frames)");

                // The player leaves: its follow ends, its main menu loads, and its game's own background setting comes back.
                Assert(guest.Runtime.Multiplayer.Leave().Succeeded, "the player leaves");
                Assert(FollowRunUntil(games, clock, () => menus[guest] == 1 && guestSessions.Current.Phase == SessionPhase.Idle && guestSessions.RealmPhase == SessionPhase.Idle, 200),
                    "the player's main menu loads once its follow has ended");
                Assert(!follower.IsFollowing && !guestSessions.RunsFollowedSession && guest.Runtime.Multiplayer.Realm == NetRealm.Offline,
                    "and nothing of the host's world is left on it");
                Assert(!guest.Game.RunInBackground && !guest.Runtime.Multiplayer.Host.HoldsRunInBackground, "its game's own background setting is back, exactly");
                Assert(events.Count(item => item == "participant:dispose") == 3 && events.Count(item => item == "controller:dispose") == 1,
                    "every participant the worlds before started is disposed, the player's included, and only the host's first server controller ever stopped");
                Assert(menus[host] == 0 && hostSessions.Current.Phase == SessionPhase.Running && hostSessions.Current.Identity!.SessionId == second,
                    "the host plays on in its world");
                foreach (var game in games)
                    Assert(game.Fixture.Logger.Errors.Count == 0, "no game logged an error (" + string.Join(" | ", game.Fixture.Logger.Errors) + ")");
            }
            finally
            {
                foreach (var game in games) game.Runtime.UnloadAll();
            }
        }

        /// <summary>One frame of every game, with what each one's dispatcher holds run, as the game's own loop runs it; the clock moves 16 ms.</summary>
        private static bool FollowRunUntil(IReadOnlyList<WorldGame> games, WorldTestClock clock, Func<bool> condition, int maximum)
        {
            for (var frame = 0; frame < maximum; frame++)
            {
                if (condition()) return true;
                foreach (var game in games)
                {
                    game.Frame();
                    game.Runtime.NativeDispatcher.Drain();
                }

                clock.Now += 16_000;
            }

            return condition();
        }

        /// <summary>Counts what 10,000 frames of every game allocate, the fewest bytes of up to three passes after one to warm, as the other runtime checks do.</summary>
        private static long MeasureFollowFrames(IReadOnlyList<WorldGame> games)
        {
            FollowFramePass(games, 10_000);
            var allocated = long.MaxValue;
            for (var pass = 0; pass < 3 && allocated != 0; pass++) allocated = Math.Min(allocated, FollowFramePass(games, 10_000));
            return allocated;
        }

        /// <summary>Runs frames with a zero frame time, which keeps the runtime's once-a-second capability refresh out.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long FollowFramePass(IReadOnlyList<WorldGame> games, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++)
            {
                for (var game = 0; game < games.Count; game++)
                {
                    games[game].Runtime.DispatchUpdate(0f);
                    games[game].Game.RaiseLateUpdate();
                    games[game].Runtime.NativeDispatcher.Drain();
                }
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }
}
