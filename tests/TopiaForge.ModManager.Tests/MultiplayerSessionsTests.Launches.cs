using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The session this game was launched to host or join. A host opens once its own game runs, or at the menu, after its
    /// password is typed in game; a join goes through the realm and the connecting overlay under the launch's own privacy,
    /// and leaves a running game only when the player says so; a LAN session is listened for first; one launch starts at a
    /// time. None asks this game's own disclosure again: the launcher told the player before it planned the session.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        private const string OwnerRequest = "a1b2c3d4e5f60718293a4b5c6d7e8f90";
        private const string OwnerDigest = "aa1fa8782182a9af";
        private const string OwnerService = "https://mp.topiaforge.dev";
        private const string LaunchIntent = "intent-7d3e1f20";
        private const string LaunchedServer = "c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3";
        private const string LaunchedDescriptor = "918ec546e59e643a2dc3c6b6791d2c1c66945cd872f29b8e109ad13ed6a5ceae";
        private const string LaunchedPackages = "bce9911e3089c75307efb68e910ea9721a339eabeb490eae505cf2f0168d0d82";
        private static readonly string LanServer = new string('1', 64);

        private static void LaunchedSessionsStart()
        {
            ALaunchedHostOpensOnceItsGameRuns();
            ALaunchedHostAsksForItsPasswordInGame();
            ALaunchedJoinKeepsItsOwnPrivacy();
            ALaunchedJoinLeavesARunningGameOnlyWhenTold();
            ALaunchedLanJoinListensFirst();
            OneLaunchStartsAtATime();
        }

        private static void ALaunchedHostOpensOnceItsGameRuns()
        {
            var rig = Owned(acknowledged: false);
            var sink = Launch(rig, HostSession(password: false), startsGame: true, game: LaunchedGame.Starting);
            rig.Frame(3);
            That(!rig.Control.Calls.Contains("StartHosting") && rig.Presenter.Launches.IsActive && sink.Blocks.Count == 0,
                "while the launch's own game starts, nothing opens");
            sink.Game = LaunchedGame.Ready;
            rig.Environment.Phase = SessionPhase.Running;
            rig.Environment.CurrentGame = new HostTargetChoice("Sandbox", new LaunchRequest("io.example.sandbox"), "Sandbox", "open-sandbox");
            rig.Frame();
            That(rig.Control.Calls.Count(call => call == "StartHosting") == 1 && ReferenceEquals(rig.Control.LastHost, sink.Plan.Host) &&
                 rig.Control.LastHost!.World == "open-sandbox" && rig.Environment.DisplayName == "Sam" && rig.Prompt == null,
                "once it runs, the launch opens it in place under its own name and settings, naming the game, and asks nothing, the disclosure included");
            rig.Frame();
            rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            rig.Frame();
            That(sink.Outcome?.Status == "succeeded" && sink.States == "starting, hosting" && rig.Noticed(LaunchSessionPresenter.Opened),
                "the provider's hosting is the launch's success, and the player hears of it once (" + sink.States + ")");
            rig.Status(InternalSessionRole.Solo);
            rig.Frame();
            That(sink.End?.EndReason == "host-ended" && !rig.Presenter.Launches.IsActive, "once hosting stops, the launch's session has ended");

            var menu = Owned();
            var atMenu = Launch(menu, HostSession(password: false), startsGame: false);
            menu.Frame();
            That(menu.Control.Calls.Contains("StartHosting") && atMenu.Outcome == null, "a launch to host at the menu opens there");

            var stopped = Owned();
            var gone = Launch(stopped, HostSession(password: false), startsGame: true);
            stopped.Frame();
            That(gone.Outcome?.Error?.Message == LaunchSessionPresenter.GameStopped && !stopped.Control.Calls.Contains("StartHosting") &&
                 !stopped.Presenter.Launches.IsActive, "a launch whose own game stopped before it opened opens nothing at the menu");

            var missing = Owned(withControl: false);
            var none = Launch(missing, HostSession(password: false));
            missing.Frame();
            That(none.Outcome?.Error?.Code == "unavailable" && none.Outcome.Error.Message == SessionLaunch.ProviderMissing,
                "without the multiplayer provider the launch fails at once, as unavailable");
        }

        private static void ALaunchedHostAsksForItsPasswordInGame()
        {
            var rig = Owned();
            var sink = Launch(rig, HostSession(password: true));
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.Password && rig.Prompt.Title == LaunchSessionPresenter.PasswordTitle && !rig.Control.Calls.Contains("StartHosting"),
                "a session that needs a password asks for it in game before it opens; the launcher never stages one");
            rig.Prompt!.Answer(true, "line\nbreak");
            rig.Frame();
            That(rig.Noticed(ConnectingPresenter.PasswordRule) && rig.Prompt?.Kind == ScreenPromptKind.Password && !rig.Control.Calls.Contains("StartHosting"),
                "a password that breaks the rule is asked for again");
            rig.Prompt!.Answer(true, "open sesame");
            rig.Frame();
            That(rig.Control.LastHostPassword == "open sesame" && rig.Control.LastHost!.Password == null && rig.Prompt == null && sink.Outcome == null,
                "the password reaches the session once, and this game keeps none of it");

            var cancelled = Owned();
            var refused = Launch(cancelled, HostSession(password: true));
            cancelled.Frame();
            cancelled.Prompt!.Answer(false, string.Empty);
            cancelled.Frame();
            That(refused.Outcome?.Status == "cancelled" && !cancelled.Control.Calls.Contains("StartHosting") && cancelled.Prompt == null,
                "turning the password down cancels the launch and opens nothing");
        }

        private static void ALaunchedJoinKeepsItsOwnPrivacy()
        {
            var rig = Owned(acknowledged: false);
            var sink = Launch(rig, JoinSession(Typed("192.168.1.20")));
            rig.Frame();
            That(rig.Control.Calls.Contains("Join:Direct:relay") && rig.Control.LastJoin!.DisplayName == "Sam" && rig.Presenter.Connecting.IsVisible && rig.Prompt == null,
                "a launched join goes through the realm and the overlay, relay-only as planned, under its own name, and asks nothing");
            rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.RelayUnavailable);
            rig.Control.Status.Internet.HostAllowsDirect = true;
            rig.Frame();
            That(rig.Prompt == null && rig.Presenter.Connecting.HasFailed && sink.Outcome?.Multiplayer.Failure == "relay-unavailable" &&
                 rig.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal)) == 1,
                "with the relay down a launched join never offers to go direct: only the launcher plans that");

            var direct = Owned();
            var allowed = Launch(direct, JoinSession(Typed("203.0.113.40"), allowDirect: true));
            direct.Frame();
            That(direct.Control.Calls.Contains("Join:Direct:direct"), "a launch the player allowed direct play for joins directly");
            direct.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            direct.Frame();
            That(allowed.Outcome?.Status == "succeeded" && allowed.States.EndsWith("in-session", StringComparison.Ordinal),
                "and succeeds once the provider is in session (" + allowed.States + ")");
        }

        private static void ALaunchedJoinLeavesARunningGameOnlyWhenTold()
        {
            var rig = Owned();
            rig.Environment.Phase = SessionPhase.Running;
            var sink = Launch(rig, JoinSession(Typed("192.168.1.20")));
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.Destructive && rig.Prompt.Title == ConnectingPresenter.LeaveTitle && !Joined(rig),
                "a launched join asks \"Leave current game?\" while a game runs");
            rig.Answer(ScreenPromptKind.Destructive, false);
            That(sink.Outcome?.Status == "cancelled" && !Joined(rig) && rig.Environment.PendingMenu == null && !rig.Presenter.Launches.IsActive,
                "turning it down keeps the game: the launch is cancelled and nothing is joined");

            var leaving = Owned();
            leaving.Environment.Phase = SessionPhase.Running;
            var left = Launch(leaving, JoinSession(Typed("192.168.1.20")));
            leaving.Frame();
            leaving.Answer(ScreenPromptKind.Destructive, true);
            That(leaving.Environment.PendingMenu != null && !Joined(leaving), "confirming returns to the menu first");
            leaving.Environment.PendingMenu!(true, "Returned to the main menu.");
            leaving.Environment.Phase = SessionPhase.Idle;
            leaving.Frame();
            That(Joined(leaving) && left.Outcome == null, "then joins");
        }

        private static void ALaunchedLanJoinListensFirst()
        {
            var rig = Owned();
            var sink = Launch(rig, JoinSession(InternalLaunchTarget.Listing(LanServer, LaunchedDescriptor, lan: true)));
            rig.Frame();
            That(rig.Control.Calls.Contains("StartBrowsing") && !Joined(rig), "a LAN session is listened for on the local network before the join");
            rig.Frame(5, deltaTime: 1f);
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            rig.Frame();
            That(rig.Control.Calls.Contains("Join:Lan:relay"), "once heard, it is joined");
            rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            rig.Frame();
            That(sink.Outcome?.Status == "succeeded" && rig.Control.Calls.Contains("StopBrowsing"), "and the listening stops once the launch settles");

            var silent = Owned();
            var unheard = Launch(silent, JoinSession(InternalLaunchTarget.Listing(LanServer, LaunchedDescriptor, lan: true)));
            silent.Frame(16, deltaTime: 1f);
            That(unheard.Outcome?.Multiplayer.Failure == "timeout" && unheard.Outcome.Multiplayer.Detail == LaunchSessionPresenter.NotHeard &&
                 silent.Control.Calls.Contains("StopBrowsing") && !Joined(silent), "a session never heard fails as a timeout, and the listening stops");
        }

        private static void OneLaunchStartsAtATime()
        {
            var rig = Owned();
            rig.Environment.Phase = SessionPhase.Running;
            var first = Launch(rig, JoinSession(Typed("192.168.1.20")));
            rig.Frame();
            That(rig.Control.PlanLaunchSession(JoinSession(Typed("192.168.1.21"))).TryGetValue(out var plan) && plan != null, "a second launch is planned");
            var second = new LaunchSink(plan!);
            rig.Presenter.Launches.Begin(new LaunchedSession(new LaunchSessionIdentity(OwnerRequest, "join", "intent-2"), plan!, second, () => second.Game));
            That(second.Outcome?.Error?.Code == "conflict" && second.Outcome.Error.Message == LaunchSessionPresenter.AlreadyStarting && first.Outcome == null &&
                 rig.Prompt?.Title == ConnectingPresenter.LeaveTitle, "while one launch starts another is refused, and the first goes on");
        }

        /// <summary>A game the TopiaForge launcher started: its channels name its request; the test polls for live joins itself.</summary>
        private static SessionsRig Owned(bool acknowledged = true, bool withControl = true)
        {
            var rig = new SessionsRig(acknowledged: acknowledged, withControl: withControl, launcher: new MemoryLauncherChannels(OwnerRequest, OwnerDigest, OwnerService));
            rig.Presenter.Intake!.Dispose();
            return rig;
        }

        private static InternalLaunchSession HostSession(bool password) =>
            new InternalLaunchSession(LaunchIntent, "Sam", false, new InternalLaunchHost("Sam's Sandbox", 8, InternalSessionVisibility.Lan, false, password));

        private static InternalLaunchSession JoinSession(InternalLaunchTarget target, bool allowDirect = false) =>
            new InternalLaunchSession(LaunchIntent, "Sam", allowDirect, target: target);

        private static InternalLaunchTarget Typed(string host) => InternalLaunchTarget.Direct(host, 27250, LaunchedServer, LaunchedDescriptor);

        /// <summary>Plans <paramref name="session"/> through the rig's control, as the runtime does, and hands the launch to the screens.</summary>
        private static LaunchSink Launch(SessionsRig rig, InternalLaunchSession session, bool startsGame = false, LaunchedGame game = LaunchedGame.Ready)
        {
            That(rig.Control.PlanLaunchSession(session).TryGetValue(out var plan) && plan != null, "the control plans the launch");
            var sink = new LaunchSink(plan!) { Game = game };
            var role = session.Host != null ? "host" : "join";
            rig.Environment.Launched = new LaunchedSession(new LaunchSessionIdentity(OwnerRequest, role, session.IntentId), plan!, sink, () => sink.Game,
                startsGame: startsGame, handoff: role == "join" ? LauncherHandoff.Current() : null);
            return sink;
        }

        /// <summary>A launch's records as the runtime's publisher takes them, and the launch's own game as a test sets it.</summary>
        private sealed class LaunchSink : ISessionLaunchSink
        {
            internal LaunchSink(IInternalLaunchPlan plan)
            {
                Plan = plan;
            }

            internal IInternalLaunchPlan Plan { get; }

            internal LaunchedGame Game { get; set; } = LaunchedGame.Ready;

            internal List<LaunchProgressSession> Blocks { get; } = new List<LaunchProgressSession>();

            internal SessionLaunchResult? Outcome { get; private set; }

            internal LaunchOutcomeMultiplayer? End { get; private set; }

            internal string States => string.Join(", ", Blocks.Select(block => block.State));

            public bool HasResult => Outcome != null;

            public void Progress(LaunchProgressSession block, string? phase) => Blocks.Add(block);

            public void Result(SessionLaunchResult result, string? phase) => Outcome ??= result;

            public void Ended(LaunchOutcomeMultiplayer block, bool clean) => End ??= block;
        }
    }
}
