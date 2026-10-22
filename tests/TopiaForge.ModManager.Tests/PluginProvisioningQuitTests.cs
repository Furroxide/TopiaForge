using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A provisioning run observes once, asks Unity to quit once at its first Update, and owns nothing else: no manager
    /// storage, no startup journal, no teardown. The quit handler that holds a normal quit for the teardown must stay out
    /// of such a run: it would refuse that quit, ask for it again and, with no teardown to wait for, hold it until its
    /// 10-second deadline. The plugin is a MonoBehaviour this harness cannot run, so the wiring checks read its source,
    /// as the quit clock check does. The run checks drive the real lifetime, quit coordinator and quit handler through a
    /// quit as Unity runs it, wired as the source checks hold the plugin to.
    /// </summary>
    internal static class PluginProvisioningQuitTests
    {
        private const string FirstUpdate = "First Update reached; requesting Unity quit once.";
        private const string QuitCallReturned = "Unity quit call returned; original process exit remains unconfirmed.";
        private const string ApplicationQuitReached = "Unity OnApplicationQuit reached; original process exit remains unconfirmed.";
        private const string DestroyReached = "Unity OnDestroy reached; original process exit remains unconfirmed.";
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

        internal static void Run(string root)
        {
            var failures = new List<Exception>();
            var cases = new (string Name, Action Test)[]
            {
                (nameof(ProvisioningLetsTheQuitThroughAtOnce), () => ProvisioningLetsTheQuitThroughAtOnce(root)),
                (nameof(ProvisioningRecordsNoCleanExit), () => ProvisioningRecordsNoCleanExit(root)),
                (nameof(ProvisioningQuitIsRequestedOnceAtTheFirstUpdate), () => ProvisioningQuitIsRequestedOnceAtTheFirstUpdate(root)),
                (nameof(ProvisioningPhaseLinesKeepTheirTextAndOrder), () => ProvisioningPhaseLinesKeepTheirTextAndOrder(root)),
                (nameof(OneOnApplicationQuitAndOneOnDestroy), OneOnApplicationQuitAndOneOnDestroy),
                (nameof(NormalQuitStillWaitsForTheTeardown), () => NormalQuitStillWaitsForTheTeardown(root)),
            };
            foreach (var (name, test) in cases)
            {
                try { test(); }
                catch (Exception error) { failures.Add(new InvalidOperationException(name, error)); }
            }

            if (failures.Count != 0) throw new AggregateException("Plugin provisioning quit regressions failed.", failures);
            Console.WriteLine("Plugin provisioning quit: " + cases.Length + " checks passed.");
        }

        // Awake returns for a provisioning run before the quit handler exists, so only the game's own handlers answer.
        private static void ProvisioningLetsTheQuitThroughAtOnce(string root)
        {
            var plugin = PluginSource.Read();
            var awake = plugin.Body("private void Awake()");
            Check(awake.StartsWith("if (ObserveProvisioningOnly()) return;", StringComparison.Ordinal),
                "Awake's first statement must return for a provisioning run, before the quit handler can be wired.");
            Check(plugin.Count("new PluginQuitCoordinator(") == 1
                && awake.Contains("quitCoordinator = new PluginQuitCoordinator(", StringComparison.Ordinal)
                && plugin.Count("new PluginQuitHandler(") == 1
                && awake.Contains("quitHandler = new PluginQuitHandler(quitCoordinator, MoveQuitHandlerLast,", StringComparison.Ordinal),
                "The quit coordinator and the quit handler are made only in Awake, after the provisioning return.");
            Check(plugin.Count("Application.wantsToQuit +=") == 2
                && awake.Contains("Application.wantsToQuit += OnWantsToQuit;", StringComparison.Ordinal)
                && plugin.Body("private void MoveQuitHandlerLast()").Contains("Application.wantsToQuit += OnWantsToQuit;", StringComparison.Ordinal)
                && plugin.Count("MoveQuitHandlerLast") == 2,
                "wantsToQuit is subscribed only in Awake, after the provisioning return, and again only by the quit handler's own move.");

            var run = new PluginRun(root, "through", provisioning: true);
            run.RunUntilQuit();
            Check(run.TopiaForgeAnswers == 0, "No TopiaForge handler may answer a provisioning quit.");
            Check(run.Requests == 2 && run.QuitFrame == run.GameAskedAgainFrame,
                "The provisioning quit must go ahead as soon as the game's own handler lets it: TopiaForge holds no request.");
        }

        // A provisioning run never opens the startup journal and owns no teardown, so the previous run's record stays.
        private static void ProvisioningRecordsNoCleanExit(string root)
        {
            var plugin = PluginSource.Read();
            var awake = plugin.Body("private void Awake()");
            var provisioningReturn = awake.IndexOf("if (ObserveProvisioningOnly()) return;", StringComparison.Ordinal);
            Check(provisioningReturn >= 0 && plugin.Count("TryBeginStartupJournal();") == 1
                && awake.IndexOf("TryBeginStartupJournal();", StringComparison.Ordinal) > provisioningReturn,
                "The startup journal opens only in Awake, after the provisioning return.");

            var run = new PluginRun(root, "journal", provisioning: true);
            run.RunUntilQuit();
            Check(run.TeardownStartFrame < 0, "A provisioning run owns no teardown, so nothing in it can record a clean exit.");
            Check(run.JournalBefore.SequenceEqual(File.ReadAllBytes(run.JournalPath)),
                "A provisioning run must leave the startup journal exactly as it found it.");
            run.ExpectNextStart(safeMode: true, "The previous run's unclean end must still reach the next normal start.");
        }

        // The provisioning quit is asked for once, at the first Update, though Update runs on while the game holds the quit.
        private static void ProvisioningQuitIsRequestedOnceAtTheFirstUpdate(string root)
        {
            var plugin = PluginSource.Read();
            var update = plugin.Body("private void Update()");
            Check(update.StartsWith("RequestProvisioningQuitOnUpdate();", StringComparison.Ordinal)
                && plugin.Count("RequestProvisioningQuitOnUpdate()") == 2,
                "The provisioning quit keeps its place: Update's first statement, and its only call.");
            var request = plugin.Body("private void RequestProvisioningQuitOnUpdate()");
            var claim = request.IndexOf("initializationLifetime.TryRequestProvisioningQuit(", StringComparison.Ordinal);
            Check(claim >= 0 && request.IndexOf("UnityEngine.Application.Quit();", StringComparison.Ordinal) > claim
                && Occurrences(request, "Application.Quit()") == 1,
                "The provisioning quit is asked for only inside the lifetime's single claim.");
            var handlerRequest = update.IndexOf("if (quitHandler?.ShouldRequestQuit() == true)", StringComparison.Ordinal);
            Check(plugin.Count("Application.Quit()") == 2 && handlerRequest > 0
                && update.IndexOf("Application.Quit();", StringComparison.Ordinal) > handlerRequest,
                "The plugin's only other quit request belongs to the quit handler, which a provisioning run never has.");

            var run = new PluginRun(root, "once", provisioning: true);
            run.RunUntilQuit();
            Check(run.ProvisioningQuitFrames.SequenceEqual(new[] { 1 }) && run.HandlerQuitRequests == 0,
                "The provisioning quit must be asked for once, at the first Update, and never again by TopiaForge: "
                + string.Join(", ", run.ProvisioningQuitFrames));
        }

        private static void ProvisioningPhaseLinesKeepTheirTextAndOrder(string root)
        {
            var plugin = PluginSource.Read();
            foreach (var phase in new[] { FirstUpdate, QuitCallReturned, ApplicationQuitReached, DestroyReached })
                Check(plugin.Count("LogProvisioningPhase(\"" + phase + "\");") == 1, "The phase line \"" + phase + "\" keeps its exact text.");
            Check(InOrder(plugin.Body("private void RequestProvisioningQuitOnUpdate()"), FirstUpdate, "UnityEngine.Application.Quit();", QuitCallReturned),
                "The first two phase lines stay on either side of the quit call.");
            Check(plugin.Body("private void OnApplicationQuit()").Contains(Gated(ApplicationQuitReached), StringComparison.Ordinal)
                && plugin.Body("private void LogProvisioningDestroyPhase()").Contains(Gated(DestroyReached), StringComparison.Ordinal)
                && plugin.Body("private void OnDestroy()").StartsWith("LogProvisioningDestroyPhase();", StringComparison.Ordinal),
                "OnApplicationQuit logs its phase line, and OnDestroy logs its own before anything else, each only in a provisioning"
                + " run that recorded its observation.");

            var run = new PluginRun(root, "phases", provisioning: true);
            run.RunUntilQuit();
            Check(run.Phases.SequenceEqual(new[] { FirstUpdate, QuitCallReturned, ApplicationQuitReached, ApplicationQuitReached, DestroyReached }),
                "A provisioning quit logs the first Update and the returned quit call, OnApplicationQuit for the request the game"
                + " refused and for the quit, then OnDestroy: " + string.Join(" | ", run.Phases));
        }

        // Unity calls both by name. The release branch keeps OnApplicationQuit in the provisioning partial.
        private static void OneOnApplicationQuitAndOneOnDestroy()
        {
            var plugin = PluginSource.Read();
            Check(plugin.Declarations("OnApplicationQuit") == 1
                && plugin.Partial("TopiaForgeModManagerPlugin.Provisioning.cs").Contains("private void OnApplicationQuit()", StringComparison.Ordinal),
                "The plugin has one OnApplicationQuit, in the provisioning partial.");
            Check(plugin.Declarations("OnDestroy") == 1, "The plugin has one OnDestroy.");
        }

        // The same run outside provisioning: the handler answers after the game and holds the quit for the teardown. Update
        // asks for the held quit again ahead of its ready guard, because the teardown clears ready.
        private static void NormalQuitStillWaitsForTheTeardown(string root)
        {
            var plugin = PluginSource.Read();
            var update = plugin.Body("private void Update()");
            var handlerRequest = update.IndexOf("if (quitHandler?.ShouldRequestQuit() == true)", StringComparison.Ordinal);
            Check(handlerRequest >= 0 && update.IndexOf("if (!ready)", StringComparison.Ordinal) > handlerRequest
                && plugin.Body("private void BeginPluginTeardown()").Contains("ready = false;", StringComparison.Ordinal),
                "Update asks for the held quit again ahead of its ready guard: the teardown clears ready, so a request behind the"
                + " guard never comes and the game stays open with every mod unloaded.");

            var run = new PluginRun(root, "normal", provisioning: false);
            run.PressQuit();
            run.RunUntilQuit();
            Check(run.TopiaForgeRefusals == 2 && run.HandlerQuitRequests == 2 && run.TeardownStartFrame > run.GameStepsFrame,
                "Outside provisioning the quit handler must answer after the game's own quit steps and hold the quit for the teardown.");
            Check(run.ProvisioningQuitFrames.Count == 0 && run.Phases.Count == 0,
                "Outside provisioning no provisioning quit or phase line may happen.");
            run.ExpectNextStart(safeMode: false, "Outside provisioning a normal quit must still record the clean exit.");
        }

        private static int Occurrences(string text, string value)
        {
            var count = 0;
            for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
                index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        // How the plugin logs a phase line that Unity's own quit steps reach: only in a provisioning run that recorded its
        // observation.
        private static string Gated(string phase) =>
            "if (initializationLifetime.ProvisioningObservationRecorded) LogProvisioningPhase(\"" + phase + "\");";

        private static bool InOrder(string text, params string[] values)
        {
            var from = 0;
            foreach (var value in values)
            {
                var index = text.IndexOf(value, from, StringComparison.Ordinal);
                if (index < 0) return false;
                from = index + value.Length;
            }

            return true;
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>
        /// One run of the plugin through a quit as Unity runs it. A quit request is answered at the end of the frame by
        /// each wantsToQuit handler in the order it subscribed, from a snapshot, until one refuses. Unity sends
        /// OnApplicationQuit after each refused request and once more as the quit goes ahead, then OnDestroy, as seen in
        /// the game for #237. The game subscribes its own handler after the plugin's Awake; it refuses its first request
        /// while its quit steps run, then asks for the quit again.
        /// </summary>
        private sealed class PluginRun
        {
            private const int GameStepFrames = 4;
            private const int TeardownFrames = 3;
            private readonly List<Func<bool>> handlers = new List<Func<bool>>();
            private readonly PluginInitializationLifetime lifetime = new PluginInitializationLifetime();
            private readonly Func<bool> topiaForgeHandler;
            private PluginQuitHandler? quitHandler;
            private StartupJournal? journal;
            private TimeSpan now = TimeSpan.FromMinutes(1);
            private int frame;
            private bool quitAtEndOfFrame;
            private bool startupCompleted;
            private bool teardownFinished;
            private int teardownFramesLeft;
            private int gameFramesLeft;

            internal PluginRun(string root, string name, bool provisioning)
            {
                JournalPath = Path.Combine(root, "provisioning-quit", name + ".json");
                // The previous run ended after its startup without a clean exit.
                StartupJournal.Begin(JournalPath, out _).MarkStartupComplete();
                JournalBefore = File.ReadAllBytes(JournalPath);
                topiaForgeHandler = OnWantsToQuit;
                Awake(provisioning);
                handlers.Add(GameHandler);
            }

            internal string JournalPath { get; }
            internal byte[] JournalBefore { get; }
            internal List<string> Phases { get; } = new List<string>();
            internal List<int> ProvisioningQuitFrames { get; } = new List<int>();
            internal int HandlerQuitRequests { get; private set; }
            internal int TopiaForgeAnswers { get; private set; }
            internal int TopiaForgeRefusals { get; private set; }
            internal int Requests { get; private set; }
            internal int GameStepsFrame { get; private set; } = -1;
            internal int GameAskedAgainFrame { get; private set; } = -1;
            internal int TeardownStartFrame { get; private set; } = -1;
            internal int QuitFrame { get; private set; } = -1;

            internal void PressQuit() => quitAtEndOfFrame = true;

            internal void RunUntilQuit()
            {
                while (QuitFrame < 0 && frame < 1000) RunFrame();
                Check(QuitFrame > 0, "The quit must go ahead.");
            }

            internal void ExpectNextStart(bool safeMode, string message)
            {
                StartupJournal.Begin(JournalPath, out var recovery);
                Check(recovery.SafeMode == safeMode, message);
            }

            private void RunFrame()
            {
                frame++;
                now += TimeSpan.FromMilliseconds(16);
                Update();
                GameUpdate();
                DrainTeardown();
                if (!quitAtEndOfFrame) return;
                quitAtEndOfFrame = false;
                Requests++;
                foreach (var handler in handlers.ToArray())
                {
                    if (handler()) continue;
                    OnApplicationQuit();
                    return;
                }

                OnApplicationQuit();
                QuitFrame = frame;
                OnDestroy();
            }

            // As the plugin's Awake: a provisioning run observes and returns before anything else.
            private void Awake(bool provisioning)
            {
                if (provisioning)
                {
                    lifetime.ObserveProvisioning(() => { });
                    return;
                }

                lifetime.Start(() => { }, () => { }, () => { });
                var coordinator = new PluginQuitCoordinator(BeginTeardown, () => teardownFinished, () => now, Limit,
                    error => throw new InvalidOperationException("Unexpected teardown failure.", error));
                quitHandler = new PluginQuitHandler(coordinator, MoveQuitHandlerLast,
                    error => throw new InvalidOperationException("Unexpected handler failure.", error));
                handlers.Add(topiaForgeHandler);
                journal = StartupJournal.Begin(JournalPath, out _);
                journal.MarkStartupComplete();
                startupCompleted = true;
            }

            // As the plugin's Update: the provisioning quit first, then the quit handler's request.
            private void Update()
            {
                if (lifetime.ProvisioningQuitPending)
                {
                    lifetime.TryRequestProvisioningQuit(() =>
                    {
                        Phases.Add(FirstUpdate);
                        ProvisioningQuitFrames.Add(frame);
                        quitAtEndOfFrame = true;
                        Phases.Add(QuitCallReturned);
                    });
                }

                if (quitHandler?.ShouldRequestQuit() != true) return;
                HandlerQuitRequests++;
                quitAtEndOfFrame = true;
            }

            private bool OnWantsToQuit()
            {
                TopiaForgeAnswers++;
                var proceed = quitHandler?.Answer() ?? true;
                if (!proceed) TopiaForgeRefusals++;
                return proceed;
            }

            private void MoveQuitHandlerLast()
            {
                handlers.Remove(topiaForgeHandler);
                handlers.Add(topiaForgeHandler);
            }

            private void OnApplicationQuit()
            {
                if (lifetime.ProvisioningObservationRecorded) Phases.Add(ApplicationQuitReached);
            }

            private void OnDestroy()
            {
                if (lifetime.ProvisioningObservationRecorded) Phases.Add(DestroyReached);
                handlers.Remove(topiaForgeHandler);
                BeginTeardown();
            }

            private void BeginTeardown()
            {
                if (!lifetime.TryBeginTeardown()) return;
                TeardownStartFrame = frame;
                teardownFramesLeft = TeardownFrames;
            }

            // Mods unload over later frames; the teardown ends by recording the clean exit once startup had completed.
            private void DrainTeardown()
            {
                if (TeardownStartFrame < 0 || teardownFinished || --teardownFramesLeft > 0) return;
                if (startupCompleted) journal?.MarkCleanExit();
                teardownFinished = true;
            }

            private bool GameHandler()
            {
                if (GameAskedAgainFrame > 0) return true;
                if (GameStepsFrame < 0)
                {
                    GameStepsFrame = frame;
                    gameFramesLeft = GameStepFrames;
                }

                return false;
            }

            private void GameUpdate()
            {
                if (GameStepsFrame < 0 || GameAskedAgainFrame > 0 || --gameFramesLeft > 0) return;
                GameAskedAgainFrame = frame;
                quitAtEndOfFrame = true;
            }
        }

        /// <summary>The plugin's partial class files, without comments and with whitespace outside literals collapsed.</summary>
        private sealed class PluginSource
        {
            private readonly Dictionary<string, string> partials = new Dictionary<string, string>(StringComparer.Ordinal);

            internal static PluginSource Read()
            {
                var source = new PluginSource();
                var folder = Path.Combine(Program.FindRepoRoot(), "src", "TopiaForge.ModManager");
                foreach (var file in Directory.GetFiles(folder, "TopiaForgeModManagerPlugin*.cs").OrderBy(file => file, StringComparer.Ordinal))
                    source.partials.Add(Path.GetFileName(file), Normalize(File.ReadAllText(file)));
                Check(source.partials.ContainsKey("TopiaForgeModManagerPlugin.cs")
                    && source.partials.ContainsKey("TopiaForgeModManagerPlugin.Provisioning.cs"),
                    "The plugin and its provisioning partial must be read.");
                return source;
            }

            internal string Partial(string name) => partials[name];

            internal int Count(string value) => partials.Values.Sum(code => Occurrences(code, value));

            internal int Declarations(string method) =>
                partials.Values.Sum(code => Regex.Matches(code, @"\bvoid " + Regex.Escape(method) + @" ?\(").Count);

            // The text between the braces of the one method declared this way.
            internal string Body(string declaration)
            {
                Check(Count(declaration) == 1, declaration + " must be declared once.");
                var code = partials.Values.First(text => text.Contains(declaration, StringComparison.Ordinal));
                var open = code.IndexOf(declaration, StringComparison.Ordinal) + declaration.Length;
                if (open < code.Length && code[open] == ' ') open++;
                Check(open < code.Length && code[open] == '{', declaration + " must have a block body.");
                var depth = 0;
                for (var index = open; index < code.Length;)
                {
                    if (IsLiteralStart(code, index))
                    {
                        index = LiteralEnd(code, index);
                        continue;
                    }

                    if (code[index] == '{') depth++;
                    else if (code[index] == '}' && --depth == 0) return code.Substring(open + 1, index - open - 1).Trim();
                    index++;
                }

                throw new InvalidOperationException(declaration + " has no closing brace.");
            }

            // Comments go and whitespace outside literals becomes one space; string and character literals stay exact.
            private static string Normalize(string source)
            {
                var code = new StringBuilder(source.Length);
                var index = 0;
                while (index < source.Length)
                {
                    var next = index + 1 < source.Length ? source[index + 1] : '\0';
                    if (source[index] == '/' && next == '/')
                    {
                        var end = source.IndexOf('\n', index);
                        index = end < 0 ? source.Length : end;
                    }
                    else if (source[index] == '/' && next == '*')
                    {
                        var end = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                        index = end < 0 ? source.Length : end + 2;
                        Space(code);
                    }
                    else if (char.IsWhiteSpace(source[index]))
                    {
                        Space(code);
                        index++;
                    }
                    else
                    {
                        var end = IsLiteralStart(source, index) ? LiteralEnd(source, index) : index + 1;
                        code.Append(source, index, end - index);
                        index = end;
                    }
                }

                return code.ToString().Trim();
            }

            private static void Space(StringBuilder code)
            {
                if (code.Length != 0 && code[code.Length - 1] != ' ') code.Append(' ');
            }

            // A $ or @ prefix starts a literal only when a quote follows it; @name is an identifier.
            private static bool IsLiteralStart(string text, int index)
            {
                if (text[index] == '"' || text[index] == '\'') return true;
                if (text[index] != '$' && text[index] != '@') return false;
                while (index < text.Length && (text[index] == '$' || text[index] == '@')) index++;
                return index < text.Length && text[index] == '"';
            }

            // The index just past the string or character literal that starts at index.
            private static int LiteralEnd(string text, int index)
            {
                var interpolated = false;
                var verbatim = false;
                for (; text[index] == '$' || text[index] == '@'; index++)
                {
                    if (text[index] == '$') interpolated = true;
                    else verbatim = true;
                }

                var quote = text[index];
                var run = 0;
                while (quote == '"' && index + run < text.Length && text[index + run] == '"') run++;
                if (run >= 3)
                {
                    var close = text.IndexOf(new string('"', run), index + run, StringComparison.Ordinal);
                    return close < 0 ? text.Length : close + run;
                }

                for (index++; index < text.Length; index++)
                {
                    var c = text[index];
                    if (c == '\\' && !verbatim) index++;
                    else if (c == quote && verbatim && index + 1 < text.Length && text[index + 1] == '"') index++;
                    else if (c == quote) return index + 1;
                    else if (interpolated && c == '{' && index + 1 < text.Length && text[index + 1] == '{') index++;
                    else if (interpolated && c == '{') index = HoleEnd(text, index + 1) - 1;
                }

                return text.Length;
            }

            // The index just past the brace that closes an interpolation hole whose code starts at index.
            private static int HoleEnd(string text, int index)
            {
                var depth = 0;
                while (index < text.Length)
                {
                    if (IsLiteralStart(text, index))
                    {
                        index = LiteralEnd(text, index);
                        continue;
                    }

                    if (text[index] == '{') depth++;
                    else if (text[index] == '}' && depth-- == 0) return index + 1;
                    index++;
                }

                return text.Length;
            }
        }
    }
}
