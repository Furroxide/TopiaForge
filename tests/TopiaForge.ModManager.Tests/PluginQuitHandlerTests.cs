using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The game runs its own quit steps, the shutdown feedback page among them, inside its own quit handler. Those
    /// steps must run while every mod, and every patch a mod placed on them, is still loaded. Mods unload only once
    /// every handler subscribed before the first quit request has let the quit go ahead, and the quit then waits for
    /// the teardown that records the clean exit, so no patch outlives its mod. The plugin itself is a MonoBehaviour
    /// this harness cannot run, so the wiring the model relies on is checked in the plugin's source, read as syntax.
    /// </summary>
    internal static class PluginQuitHandlerTests
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            var cases = new (string Name, Action Test)[]
            {
                (nameof(FirstRequestMovesBehindOtherHandlersAndAsksAgainOnce), FirstRequestMovesBehindOtherHandlersAndAsksAgainOnce),
                (nameof(RequestThatReachesTheHandlerAgainIsNotRepeated), RequestThatReachesTheHandlerAgainIsNotRepeated),
                (nameof(HandlerThatCannotMoveHoldsTheQuitAtOnce), HandlerThatCannotMoveHoldsTheQuitAtOnce),
                (nameof(FailingReportStillHoldsTheQuit), FailingReportStillHoldsTheQuit),
                (nameof(TeardownThatCannotStartLetsTheQuitGoAheadOnTheNextRequest), TeardownThatCannotStartLetsTheQuitGoAheadOnTheNextRequest),
                (nameof(TeardownWithoutAQuitRequestNeverStartsAQuit), TeardownWithoutAQuitRequestNeverStartsAQuit),
                (nameof(InvalidArgumentsAreRejected), InvalidArgumentsAreRejected),
                ("QuitPressedWhileTopiaForgeIsAskedFirst", () => GameQuitStepsKeepEveryPatch(root, topiaForgeFirst: true, windowClosed: false)),
                ("WindowClosedWhileTopiaForgeIsAskedFirst", () => GameQuitStepsKeepEveryPatch(root, topiaForgeFirst: true, windowClosed: true)),
                ("QuitPressedWhileTheGameIsAskedFirst", () => GameQuitStepsKeepEveryPatch(root, topiaForgeFirst: false, windowClosed: false)),
                (nameof(QuitWithNoOtherHandlerRecordsACleanExit), () => QuitWithNoOtherHandlerRecordsACleanExit(root)),
                (nameof(QuitRefusedForGoodKeepsEveryModLoaded), () => QuitRefusedForGoodKeepsEveryModLoaded(root)),
                (nameof(HandlerSubscribedAfterTheFirstRequestIsAskedOnceModsUnload), () => HandlerSubscribedAfterTheFirstRequestIsAskedOnceModsUnload(root)),
                (nameof(StuckTeardownAfterTheGameStepsQuitsAtTheDeadline), () => StuckTeardownAfterTheGameStepsQuitsAtTheDeadline(root)),
            };
            var wiring = new (string Name, Action Test)[]
            {
                (nameof(UpdateAsksForTheHeldQuitAheadOfItsReadyGuard), UpdateAsksForTheHeldQuitAheadOfItsReadyGuard),
                (nameof(OnDestroyTakesItsHandlerOffWantsToQuit), OnDestroyTakesItsHandlerOffWantsToQuit),
            };
            foreach (var (name, test) in cases.Concat(wiring))
            {
                try { test(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
            }

            if (failures.Count != 0) throw new AggregateException("Plugin quit ordering regressions failed.", failures);
            Console.WriteLine("Plugin quit ordering: " + cases.Length + " behavioral checks passed.");
            Console.WriteLine("Plugin quit wiring: " + wiring.Length + " source checks passed.");
        }

        private static void FirstRequestMovesBehindOtherHandlersAndAsksAgainOnce()
        {
            var plugin = new Unit();
            Check(!plugin.Handler.Answer(), "The first quit request must be refused while the handler moves behind the others.");
            Check(plugin.Moves == 1 && plugin.TeardownStarts == 0, "The first request must move the handler and start nothing.");
            Check(plugin.Handler.ShouldRequestQuit(), "The refused request must be asked for again.");
            Check(!plugin.Handler.ShouldRequestQuit(), "It must be asked for again only once.");
            Check(!plugin.Handler.Answer(), "The request that comes back must be held while the teardown runs.");
            Check(plugin.Moves == 1 && plugin.TeardownStarts == 1, "The request that comes back must start the teardown, without moving again.");
            plugin.Finished = true;
            Check(plugin.Handler.ShouldRequestQuit(), "A finished teardown must ask for the held quit again.");
            Check(!plugin.Handler.ShouldRequestQuit(), "The held quit must be asked for again only once.");
            Check(plugin.Handler.Answer(), "The quit must go ahead once the teardown has finished.");
        }

        private static void RequestThatReachesTheHandlerAgainIsNotRepeated()
        {
            var plugin = new Unit();
            Check(!plugin.Handler.Answer(), "The first quit request must be refused once.");
            Check(!plugin.Handler.Answer() && plugin.TeardownStarts == 1,
                "A request that reaches the handler again must start the teardown at once.");
            Check(!plugin.Handler.ShouldRequestQuit(), "A request that already came back must not be asked for a second time.");
        }

        private static void HandlerThatCannotMoveHoldsTheQuitAtOnce()
        {
            var reported = new List<Exception>();
            var plugin = new Unit(moveFails: true, report: reported.Add);
            Check(!plugin.Handler.Answer(), "A handler that cannot move must still hold the quit for the teardown.");
            Check(plugin.TeardownStarts == 1, "A handler that cannot move must start the teardown at once, so the clean exit is kept.");
            Check(reported.Count == 1 && reported[0].Message == "cannot move", "The failed move must be reported once.");
            Check(!plugin.Handler.ShouldRequestQuit(), "Nothing must be asked for again before the teardown finishes.");
            plugin.Finished = true;
            Check(plugin.Handler.ShouldRequestQuit() && plugin.Handler.Answer(), "The held quit must go ahead after the teardown.");
            Check(plugin.Moves == 1 && reported.Count == 1, "The move must be attempted only once.");
        }

        private static void FailingReportStillHoldsTheQuit()
        {
            var plugin = new Unit(moveFails: true, report: _ => throw new IOException("log closed"));
            Check(!plugin.Handler.Answer() && plugin.TeardownStarts == 1, "A closed log must not cost the clean exit.");
        }

        // #234: a teardown that cannot start never holds the quit. Behind the move it costs one request, no more.
        private static void TeardownThatCannotStartLetsTheQuitGoAheadOnTheNextRequest()
        {
            var failures = new List<Exception>();
            var plugin = new Unit(startFails: true, teardownReport: failures.Add);
            Check(!plugin.Handler.Answer(), "The first request must be refused once while the handler moves.");
            Check(plugin.Handler.ShouldRequestQuit(), "The refused request must be asked for again.");
            Check(plugin.Handler.Answer(), "A teardown that cannot start must not hold the quit.");
            Check(plugin.TeardownStarts == 1 && failures.Count == 1, "The teardown must be tried once and its failure reported once.");
            Check(!plugin.Handler.ShouldRequestQuit(), "A quit that already went ahead must not be asked for again.");
        }

        private static void TeardownWithoutAQuitRequestNeverStartsAQuit()
        {
            var plugin = new Unit { Finished = true };
            Check(!plugin.Handler.ShouldRequestQuit(), "Teardown without a quit request (OnDestroy) must never start a quit.");
            Check(plugin.Moves == 0, "Without a quit request the handler must stay where it is.");
        }

        private static void InvalidArgumentsAreRejected()
        {
            var coordinator = new PluginQuitCoordinator(() => { }, () => false, () => TimeSpan.Zero, Limit, _ => { });
            Throws<ArgumentNullException>(() => new PluginQuitHandler(null!, () => { }, _ => { }));
            Throws<ArgumentNullException>(() => new PluginQuitHandler(coordinator, null!, _ => { }));
            Throws<ArgumentNullException>(() => new PluginQuitHandler(coordinator, () => { }, null!));
        }

        // TopiaForge subscribes while the game starts, ahead of the game's own handler; the reverse order is covered too.
        private static void GameQuitStepsKeepEveryPatch(string root, bool topiaForgeFirst, bool windowClosed)
        {
            var name = "order-" + (topiaForgeFirst ? "first" : "second") + (windowClosed ? "-close" : "-quit");
            var scenario = new QuitScenario(root, name, topiaForgeFirst, GameHandler.HoldsForFrames(4));
            if (windowClosed) scenario.CloseWindow();
            else scenario.PressQuit();
            scenario.RunUntilQuit(maxFrames: 60);

            Check(scenario.Game.StepsRan, "The game's own quit steps must run.");
            Check(scenario.Game.PatchInPlaceDuringSteps,
                "The game's own quit steps must run while the mod's patch is still in place, so the feedback page stays suppressed.");
            Check(scenario.Plugin.TeardownStartFrame > scenario.Game.StepsFrame, "Mods must unload only after the game's own quit steps.");
            Check(scenario.QuitsWentAhead == 1, "The quit must go ahead exactly once.");
            Check(scenario.Plugin.TeardownFinished && !scenario.Plugin.PatchInPlace,
                "The quit must wait for the teardown, which removes the patch: no patch outlives its mod.");
            scenario.ExpectNextStart(safeMode: false, "A normal quit must let the next start load every mod.");
        }

        private static void QuitWithNoOtherHandlerRecordsACleanExit(string root)
        {
            var scenario = new QuitScenario(root, "alone", topiaForgeFirst: true, game: null);
            scenario.PressQuit();
            scenario.RunUntilQuit(maxFrames: 10);
            Check(scenario.Plugin.TeardownFinished, "Without other handlers the quit must still wait for the teardown.");
            Check(scenario.Frame <= 6, "Without other handlers the quit must cost only one frame more than the teardown itself.");
            scenario.ExpectNextStart(safeMode: false, "A quit with no other handler must record the clean exit.");
        }

        private static void QuitRefusedForGoodKeepsEveryModLoaded(string root)
        {
            var scenario = new QuitScenario(root, "refused", topiaForgeFirst: true, GameHandler.RefusesForGood());
            scenario.PressQuit();
            scenario.RunFrames(120);
            Check(scenario.QuitsWentAhead == 0, "A quit another handler refuses must not go ahead.");
            Check(scenario.Plugin.TeardownStarts == 0 && scenario.Plugin.PatchInPlace,
                "Mods must stay loaded, with every patch, while another handler refuses the quit: the teardown cannot be undone.");
        }

        // The handler moves once, on the first request, so a handler subscribed after that sits behind it and is asked
        // only once mods have started to unload. UnityInterop.md says so, and tells authors to subscribe in OnLoad.
        private static void HandlerSubscribedAfterTheFirstRequestIsAskedOnceModsUnload(string root)
        {
            var scenario = new QuitScenario(root, "late", topiaForgeFirst: true, GameHandler.HoldsForFrames(4));
            var late = GameHandler.HoldsForFrames(2);
            scenario.PressQuit();
            scenario.RunFrames(1);
            Check(scenario.Plugin.TeardownStarts == 0, "The first request must only move the handler.");
            scenario.SubscribeLate(late);
            scenario.RunUntilQuit(maxFrames: 60);
            Check(scenario.Game.PatchInPlaceDuringSteps, "A handler subscribed before the first request must run its steps with every patch in place.");
            Check(late.StepsRan && !late.PatchInPlaceDuringSteps,
                "A handler subscribed after the first request is asked only once mods have unloaded, as the docs say.");
            scenario.ExpectNextStart(safeMode: false, "The quit must still record the clean exit.");
        }

        private static void StuckTeardownAfterTheGameStepsQuitsAtTheDeadline(string root)
        {
            var scenario = new QuitScenario(root, "stuck", topiaForgeFirst: true, GameHandler.HoldsForFrames(4), teardownFrames: null);
            scenario.PressQuit();
            scenario.RunUntilQuit(maxFrames: 2000);
            Check(scenario.Game.PatchInPlaceDuringSteps, "The game's own quit steps must still run first.");
            Check(scenario.Plugin.Coordinator.DeadlinePassed, "A stuck teardown must let the quit go ahead at the deadline, and say so.");
            scenario.ExpectNextStart(safeMode: true, "Unfinished cleanup must still open the next start in safe mode.");
        }

        // The model's plugin asks for the held quit every frame. The real one asks in Update, and the teardown the quit
        // starts clears ready, so Update must ask before anything reads ready: behind the ready guard the request never
        // comes, and the game stays open with every mod unloaded. The source is read as syntax, so a change of layout
        // cannot fool the check, and a guard written another way still counts as one.
        private static void UpdateAsksForTheHeldQuitAheadOfItsReadyGuard()
        {
            var plugin = PluginSourceSyntax.Read();
            Check(plugin.Statements("BeginPluginTeardown").Any(statement => PluginSourceSyntax.AssignsFalse(statement, "ready")),
                "The teardown clears ready, which is why Update asks for the held quit ahead of its ready guard; if that changes, revisit this check.");
            var update = plugin.Statements("Update");
            var requests = update.Where(statement => statement is IfStatementSyntax request
                && PluginSourceSyntax.CallsOn(request.Condition, "quitHandler", "ShouldRequestQuit")).ToArray();
            Check(requests.Length == 1 && PluginSourceSyntax.CallsApplicationQuit(((IfStatementSyntax)requests[0]).Statement),
                "Update asks Unity to quit whenever the quit handler asks for the held quit again, in an if statement of its own.");
            var at = update.IndexOf(requests[0]);
            Check(!update.Take(at + 1).Any(statement => PluginSourceSyntax.Mentions(statement, "ready")),
                "Update asks for the held quit before anything reads ready: the teardown clears ready, so a request behind the"
                + " ready guard never comes and the game stays open with every mod unloaded.");
            Check(update.Skip(at + 1).Any(statement => statement is IfStatementSyntax guard
                    && PluginSourceSyntax.Mentions(guard.Condition, "ready") && PluginSourceSyntax.CanLeave(guard.Statement)),
                "Update keeps its ready guard after the request.");
        }

        // A plugin destroyed without a quit must take its handler off wantsToQuit: a stale handler would refuse the next
        // quit once, and a destroyed plugin runs no Update to ask for that quit again.
        private static void OnDestroyTakesItsHandlerOffWantsToQuit()
        {
            var destroy = PluginSourceSyntax.Read().Statements("OnDestroy");
            var unsubscribes = destroy.Where(statement => PluginSourceSyntax.Unsubscribes(statement, "OnWantsToQuit")).ToArray();
            var teardowns = destroy.Where(statement => PluginSourceSyntax.Calls(statement, "BeginPluginTeardown")).ToArray();
            Check(unsubscribes.Length == 1 && teardowns.Length == 1,
                "OnDestroy takes OnWantsToQuit off Application.wantsToQuit, and starts the teardown, each in a statement of its own.");
            var at = destroy.IndexOf(unsubscribes[0]);
            Check(at < destroy.IndexOf(teardowns[0]) && !destroy.Take(at).Any(PluginSourceSyntax.CanLeave),
                "OnDestroy takes its handler off wantsToQuit before the teardown, and nothing ahead of that can leave OnDestroy,"
                + " so the handler goes whenever the plugin is destroyed, even when the teardown fails.");
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Throws<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ".");
        }

        /// <summary>The handler alone, with a coordinator whose teardown the test finishes by hand.</summary>
        private sealed class Unit
        {
            internal Unit(bool moveFails = false, Action<Exception>? report = null, bool startFails = false,
                Action<Exception>? teardownReport = null)
            {
                var coordinator = new PluginQuitCoordinator(() =>
                    {
                        TeardownStarts++;
                        if (startFails) throw new InvalidOperationException("cannot start");
                    }, () => Finished, () => TimeSpan.Zero, Limit,
                    teardownReport ?? (error => throw new InvalidOperationException("Unexpected teardown failure.", error)));
                Handler = new PluginQuitHandler(coordinator, () =>
                {
                    Moves++;
                    if (moveFails) throw new InvalidOperationException("cannot move");
                }, report ?? (error => throw new InvalidOperationException("Unexpected report.", error)));
            }

            internal PluginQuitHandler Handler { get; }
            internal bool Finished { get; set; }
            internal int Moves { get; private set; }
            internal int TeardownStarts { get; private set; }
        }

        /// <summary>
        /// A quit as Unity runs it. Handlers are asked in the order they subscribed, from a snapshot taken for each
        /// request, and the first refusal ends the request; a handler that throws counts as consent. Application.Quit
        /// is answered at the end of the frame, and the window's close button at once. A request that every handler
        /// allows makes the quit go ahead.
        /// </summary>
        private sealed class QuitScenario
        {
            private readonly List<Func<bool>> handlers = new List<Func<bool>>();
            private readonly List<GameHandler> lateHandlers = new List<GameHandler>();
            private readonly string journalPath;
            private bool quitRequested;

            internal QuitScenario(string root, string name, bool topiaForgeFirst, GameHandler? game, int? teardownFrames = 3)
            {
                journalPath = Path.Combine(root, "quit-order", name + ".json");
                var journal = StartupJournal.Begin(journalPath, out _);
                journal.MarkStartupComplete();
                Game = game ?? GameHandler.HoldsForFrames(0);
                Game.Attach(this);
                Plugin = new PluginModel(this, journal, teardownFrames);
                if (topiaForgeFirst) Subscribe(Plugin.Answer);
                if (game != null) Subscribe(Game.Answer);
                if (!topiaForgeFirst) Subscribe(Plugin.Answer);
            }

            internal GameHandler Game { get; }
            internal PluginModel Plugin { get; }
            internal int Frame { get; private set; }
            internal int QuitsWentAhead { get; private set; }
            internal TimeSpan Now { get; private set; } = TimeSpan.FromMinutes(2);

            internal void Subscribe(Func<bool> handler) => handlers.Add(handler);

            // A handler that subscribes while the quit is under way, after the plugin's handler has moved.
            internal void SubscribeLate(GameHandler handler)
            {
                handler.Attach(this);
                lateHandlers.Add(handler);
                Subscribe(handler.Answer);
            }

            internal void Unsubscribe(Func<bool> handler)
            {
                var index = handlers.LastIndexOf(handler);
                if (index >= 0) handlers.RemoveAt(index);
            }

            internal void RequestQuit() => quitRequested = true;

            internal void PressQuit() => RequestQuit();

            internal void CloseWindow() => Ask();

            internal void RunUntilQuit(int maxFrames)
            {
                while (QuitsWentAhead == 0 && Frame < maxFrames) RunFrame();
                Check(QuitsWentAhead == 1, "The quit must go ahead, once, within " + maxFrames + " frames.");
            }

            internal void RunFrames(int count)
            {
                for (var index = 0; index < count && QuitsWentAhead == 0; index++) RunFrame();
            }

            internal void ExpectNextStart(bool safeMode, string message)
            {
                StartupJournal.Begin(journalPath, out var recovery);
                Check(recovery.SafeMode == safeMode, message);
            }

            private void RunFrame()
            {
                Frame++;
                Now += TimeSpan.FromMilliseconds(16);
                Plugin.Update();
                Game.Update();
                foreach (var handler in lateHandlers) handler.Update();
                Plugin.DrainTeardown();
                if (!quitRequested) return;
                quitRequested = false;
                Ask();
            }

            private void Ask()
            {
                foreach (var handler in handlers.ToArray())
                {
                    try
                    {
                        if (!handler()) return;
                    }
                    catch
                    {
                        // Unity logs the exception and asks the next handler.
                    }
                }

                QuitsWentAhead++;
                Check(Plugin.TeardownFinished || Plugin.Coordinator.DeadlinePassed, "The quit went ahead before the teardown had finished.");
            }
        }

        /// <summary>The plugin, wired as the real one is: its handler answers Unity, and its Update asks again.</summary>
        private sealed class PluginModel
        {
            private readonly QuitScenario scenario;
            private readonly StartupJournal journal;
            private readonly int? teardownFrames;
            private int framesLeft;

            internal PluginModel(QuitScenario scenario, StartupJournal journal, int? teardownFrames)
            {
                this.scenario = scenario;
                this.journal = journal;
                this.teardownFrames = teardownFrames;
                Coordinator = new PluginQuitCoordinator(BeginTeardown, () => TeardownFinished, () => scenario.Now, Limit,
                    error => throw new InvalidOperationException("Unexpected teardown failure.", error));
                Handler = new PluginQuitHandler(Coordinator, MoveLast,
                    error => throw new InvalidOperationException("Unexpected handler failure.", error));
                Answer = Handler.Answer;
            }

            internal Func<bool> Answer { get; }
            internal PluginQuitCoordinator Coordinator { get; }
            internal PluginQuitHandler Handler { get; }
            internal bool PatchInPlace { get; private set; } = true;
            internal int TeardownStarts { get; private set; }
            internal int TeardownStartFrame { get; private set; } = -1;
            internal bool TeardownFinished { get; private set; }

            internal void Update()
            {
                if (Handler.ShouldRequestQuit()) scenario.RequestQuit();
            }

            // Mods unload over the frames after the teardown starts; its last step removes their patches and records
            // the clean exit, as the runtime's teardown does. A teardown without a frame count never finishes.
            internal void DrainTeardown()
            {
                if (TeardownStarts == 0 || TeardownFinished || teardownFrames == null) return;
                if (--framesLeft > 0) return;
                PatchInPlace = false;
                journal.MarkCleanExit();
                TeardownFinished = true;
            }

            private void BeginTeardown()
            {
                TeardownStarts++;
                TeardownStartFrame = scenario.Frame;
                framesLeft = teardownFrames ?? 0;
            }

            // The plugin subscribes again, which puts its handler last.
            private void MoveLast()
            {
                scenario.Unsubscribe(Answer);
                scenario.Subscribe(Answer);
            }
        }

        /// <summary>
        /// The game's own quit handler, as it behaves in the game: its first request starts its quit steps and is
        /// refused; once the steps have finished it lets the quit go ahead and asks for it again.
        /// </summary>
        private sealed class GameHandler
        {
            private readonly int holdFrames;
            private readonly bool refusesForGood;
            private QuitScenario? scenario;
            private int framesLeft;
            private bool started;
            private bool finished;

            private GameHandler(int holdFrames, bool refusesForGood)
            {
                this.holdFrames = holdFrames;
                this.refusesForGood = refusesForGood;
                Answer = AnswerQuit;
            }

            internal Func<bool> Answer { get; }
            internal bool StepsRan { get; private set; }
            internal int StepsFrame { get; private set; } = -1;
            internal bool PatchInPlaceDuringSteps { get; private set; }

            internal static GameHandler HoldsForFrames(int frames) => new GameHandler(frames, refusesForGood: false);

            internal static GameHandler RefusesForGood() => new GameHandler(0, refusesForGood: true);

            internal void Attach(QuitScenario owner) => scenario = owner;

            internal void Update()
            {
                if (!started || finished || refusesForGood) return;
                if (--framesLeft > 0) return;
                finished = true;
                Owner.RequestQuit();
            }

            private QuitScenario Owner => scenario ?? throw new InvalidOperationException("The game handler is not attached.");

            private bool AnswerQuit()
            {
                if (refusesForGood) return false;
                if (started) return finished;
                started = true;
                framesLeft = holdFrames;
                StepsRan = true;
                StepsFrame = Owner.Frame;
                PatchInPlaceDuringSteps = Owner.Plugin.PatchInPlace;
                return false;
            }
        }
    }
}
