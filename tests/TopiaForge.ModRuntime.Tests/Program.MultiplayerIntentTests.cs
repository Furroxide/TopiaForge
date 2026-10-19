using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using RuntimeUnderTest = TopiaForge.ModManager.ModRuntime;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private const string IntentMod = "tests.intent";
        private const string NeighbourMod = "tests.intent-neighbour";

        /// <summary>
        /// Intent tracking over the real runtime (WS3.4): a mod's input, UI callback, command and interaction each act on the
        /// player's intent, as do a callback its input schedules and its UI callback's continuation; a scripted timer in the
        /// same mod, and another mod's code beside and inside it in the same frame, see none; offline nothing does; and the
        /// frames cost nothing offline, online at rest, or in the steady state of held input.
        /// </summary>
        private static void TestMultiplayerIntent(string root)
        {
            TestEachSourceCarriesThePlayersIntent(root);
            TestIntentFramesCostNothing(root);
            Console.WriteLine("ModRuntime multiplayer intent tests passed.");
        }

        private static void TestEachSourceCarriesThePlayersIntent(string root)
        {
            var neighbour = NewFixture(root, "intent-neighbour", "TopiaForge.SecondTestMod.IntentNeighbourMod", "TopiaForge.SecondTestMod.dll");
            var fixture = NewFixture(root, "intent", "TopiaForge.ValidTestMod.RuntimeIntentMod");
            fixture.Manifest.Dependencies.Add(neighbour.Manifest.Id, ">=1.0.0 <2.0.0");
            using var pump = new MainThreadPump();
            var host = new IntentRuntimeHost { Pump = pump };
            var runtime = CreateIntentRuntime(fixture, host);
            try
            {
                runtime.Load(new[] { neighbour.Package, fixture.Package });
                Assert(runtime.IsLoaded(IntentMod) && runtime.IsLoaded(NeighbourMod), "both mods load (" + string.Join(" | ", fixture.Logger.Errors) + ")");
                var control = new FakeMultiplayerControl { Role = InternalSessionRole.Client };
                Assert(runtime.Multiplayer.Attach(control), "a provider attaches");
                IntentFrame(runtime, host);
                Assert(runtime.Multiplayer.Intents.IsRecording, "a joined player records intent");
                host.Spawns.Clear();

                // Input: the frame the player presses fire, and the frames it stays held long enough for the scripted timer to run.
                var fire = host.Action(IntentMod, "fire");
                fire.Hold(true);
                for (var index = 0; index < 6; index++) IntentFrame(runtime, host);
                fire.Hold(false);
                IntentFrame(runtime, host);
                ExpectSpawn(host, IntentMod, "input", "InputActiveFrame/local", "a mod's input handler acts on the player's input");
                ExpectSpawn(host, IntentMod, "input-late", "InputActiveFrame/local", "and so does its LateUpdate in that frame");
                ExpectSpawn(host, IntentMod, "input-next-frame", "InputActiveFrame/local", "and a callback the input scheduled, a frame later");
                ExpectSpawn(host, IntentMod, "input-fixed", "none", "FixedUpdate never carries the frame's input");
                ExpectSpawn(host, IntentMod, "timer", "none", "a scripted timer in the same mod sees none, though its input is held");
                ExpectSpawn(host, NeighbourMod, "neighbour-callback-input", "none", "another mod's code run from inside the input frame sees none");
                ExpectSpawn(host, NeighbourMod, "neighbour-update", "none", "nor does another mod's Update in the same frames");
                Assert(host.Spawns.Count(spawn => spawn.Name == "input") == 1, "a held action raises the handler's edge once");
                Assert(host.Spawns.Any(spawn => spawn.Name == "timer"), "the timer ran while the input was held");

                // A UI callback: the player confirms the mod's modal through the kit's hook, as a kit button does.
                host.Spawns.Clear();
                ConfirmAsThePlayer(host, IntentMod, true);
                IntentFrame(runtime, host);
                ExpectSpawn(host, IntentMod, "ui", "UiCallback/local", "a mod's UI callback acts on the player's click");
                ExpectSpawn(host, NeighbourMod, "neighbour-callback-ui", "none", "another mod's callback run from inside it sees none");
                ExpectSpawn(host, NeighbourMod, "neighbour-command", "OwnerCommand/local", "a command it runs acts for the same player, as its own mod");
                ExpectSpawn(host, IntentMod, "ui-continuation", "UiCallback/local", "and the callback's continuation, a frame later, acts on it still");

                // A command the player runs, as the chat overlay will, and an interaction.
                host.Spawns.Clear();
                Assert(runtime.TryExecutePlayerCommand(IntentMod + ":boost", Array.Empty<string>(), out var boosted) && boosted!.Succeeded, "the player's command runs");
                host.Interact(IntentMod);
                ExpectSpawn(host, IntentMod, "command", "OwnerCommand/local", "a mod's command acts on the player's intent");
                ExpectSpawn(host, IntentMod, "interaction", "InteractionHandler/local", "a mod's interaction handler acts on the player's interaction");
                Assert(runtime.Multiplayer.Intents.Scope.Flowing == null, "and nothing flows between them");

                // Offline every route is canonical whatever the intent, so nothing records.
                control.Role = InternalSessionRole.Solo;
                IntentFrame(runtime, host);
                host.Spawns.Clear();
                fire.Hold(true);
                IntentFrame(runtime, host);
                fire.Hold(false);
                ConfirmAsThePlayer(host, IntentMod, true);
                runtime.TryExecutePlayerCommand(IntentMod + ":boost", Array.Empty<string>(), out _);
                host.Interact(IntentMod);
                IntentFrame(runtime, host);
                Assert(host.Spawns.Count > 5 && host.Spawns.All(spawn => spawn.Intent == "none"),
                    "offline the same input, click, command and interaction record nothing (" + Describe(host) + ")");
            }
            finally
            {
                runtime.UnloadAll();
            }

            Assert(!IntentHookInstalled(), "unloading disconnects the UI kit from intent tracking");
        }

        private static void TestIntentFramesCostNothing(string root)
        {
            var fixture = NewFixture(root, "intent-frames", "TopiaForge.ValidTestMod.RuntimeIntentFrameMod");
            var host = new IntentRuntimeHost { TickTimers = false };
            var runtime = CreateIntentRuntime(fixture, host);
            try
            {
                runtime.Load(new[] { fixture.Package });
                var hold = host.Action("tests.intent-frames", "hold");
                hold.Hold(true);
                var offline = MeasureIntentFrames(runtime, host);
                var offlineTime = TimeIntentFrames(runtime, host);
                Assert(offline == 0, "offline, a frame with held input allocates nothing (" + offline + " bytes)");
                var control = new FakeMultiplayerControl { Role = InternalSessionRole.Client };
                Assert(runtime.Multiplayer.Attach(control), "a provider attaches");
                hold.Hold(false);
                var atRest = MeasureIntentFrames(runtime, host);
                var atRestTime = TimeIntentFrames(runtime, host);
                Assert(atRest == 0 && runtime.Multiplayer.Intents.IsRecording, "online at rest, a frame allocates nothing (" + atRest + " bytes)");
                hold.Hold(true);
                var held = MeasureIntentFrames(runtime, host);
                var heldTime = TimeIntentFrames(runtime, host);
                Assert(held == 0, "online, the scope each input-active frame opens allocates nothing in the steady state (" + held + " bytes)");
                Console.WriteLine("Intent frames through ModRuntime on .NET: " + offline + " bytes offline, " + atRest + " online at rest, " + held +
                                  " online with input held, over 10,000 frames each; a whole frame took " + offlineTime + ", " + atRestTime +
                                  " and " + heldTime + " microseconds, the fewest of five 10,000-frame runs.");
            }
            finally
            {
                runtime.UnloadAll();
            }
        }

        private static RuntimeUnderTest CreateIntentRuntime(Fixture fixture, IntentRuntimeHost host)
        {
            var runtime = new RuntimeUnderTest(
                fixture.Paths,
                fixture.Logger,
                new ManifestValidationContext("0.0.2309", "1.0.0", "1.0.0", platform: "windows", architecture: "x64",
                    contentTargets: new[] { "code", "standalonewindows64" }, enforceRuntimeCompatibility: true),
                fixture.Observer,
                host);
            host.Intents = runtime.Multiplayer.Intents;
            Assert(IntentHookInstalled(), "the runtime connects the UI kit to its intent tracking");
            return runtime;
        }

        /// <summary>One frame in Unity's order: FixedUpdate, then Update (the host's sample, timers and posted work first), then LateUpdate.</summary>
        private static void IntentFrame(RuntimeUnderTest runtime, IntentRuntimeHost host)
        {
            host.RaiseFixedUpdate();
            runtime.DispatchUpdate(0.25f);
            host.RaiseLateUpdate();
        }

        private static long MeasureIntentFrames(RuntimeUnderTest runtime, IntentRuntimeHost host)
        {
            IntentFrames(runtime, host, 1_000);
            var fewest = long.MaxValue;
            for (var pass = 0; pass < 3 && fewest != 0; pass++) fewest = Math.Min(fewest, IntentFrames(runtime, host, 10_000));
            return fewest;
        }

        /// <summary>How long one frame takes, in microseconds to two decimals: reported, never asserted, since it depends on the machine.</summary>
        private static string TimeIntentFrames(RuntimeUnderTest runtime, IntentRuntimeHost host)
        {
            var fewest = double.MaxValue;
            for (var pass = 0; pass < 5; pass++)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                IntentFrames(runtime, host, 10_000);
                fewest = Math.Min(fewest, clock.Elapsed.TotalMilliseconds * 1000.0 / 10_000);
            }

            return fewest.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static long IntentFrames(RuntimeUnderTest runtime, IntentRuntimeHost host, int count)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < count; index++)
            {
                runtime.DispatchUpdate(0f);
                host.RaiseLateUpdate();
            }

            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        /// <summary>
        /// The player confirms <paramref name="owner"/>'s modal: the kit's button opens the player's action through the hook
        /// the runtime installed, and the SDK UI service's completion runs inside it.
        /// </summary>
        private static void ConfirmAsThePlayer(IntentRuntimeHost host, string owner, bool confirmed)
        {
            var completed = host.Modals.Single(modal => modal.Owner == owner).Completed;
            var playerAction = KitIntent().GetMethod("PlayerAction", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("The kit has no player-action scope.");
            var scope = (IDisposable)playerAction.Invoke(null, new object?[] { owner })!;
            try
            {
                completed(confirmed);
            }
            finally
            {
                scope.Dispose();
            }
        }

        private static bool IntentHookInstalled() =>
            (bool)(KitIntent().GetProperty("IsInstalled", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                   ?? throw new InvalidOperationException("The kit cannot say whether a hook is installed."));

        private static Type KitIntent() => Type.GetType("TopiaForge.Mods.UnityUi.TopiaForgeIntent, TopiaForge.Mods.UnityUi", throwOnError: true)!;

        private static void ExpectSpawn(IntentRuntimeHost host, string owner, string name, string intent, string message)
        {
            var matches = host.Spawns.Where(spawn => spawn.Owner == owner && spawn.Name == name).ToArray();
            Assert(matches.Length > 0 && matches.All(spawn => spawn.Intent == intent),
                message + ": " + name + " expected " + intent + " (" + Describe(host) + ")");
        }

        private static string Describe(IntentRuntimeHost host) =>
            string.Join(", ", host.Spawns.Select(spawn => spawn.Name + "=" + spawn.Intent));
    }
}
