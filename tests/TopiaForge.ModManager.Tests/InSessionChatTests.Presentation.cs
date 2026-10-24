using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;
using static TopiaForge.ModManager.Tests.SessionsRig;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class InSessionChatTests
    {
        private static void LocalCommandsAndModeration()
        {
            That(ChatCommand.TryParse("/sample:echo \"two words\" tail", out var name, out var args) && name == "sample:echo" && args.SequenceEqual(new[] { "two words", "tail" }), "quoted mod arguments parse");
            That(!ChatCommand.TryParse("/echo \"unfinished", out _, out _) && !ChatCommand.TryParse("/", out _, out _), "malformed commands fail");
            using var rig = new PresenterRig(true);
            rig.Screen.Open();
            That(rig.Screen.Submit("/sample:echo \"two words\""), "registered command executes locally");
            That(rig.Executed == "sample:echo:two words" && rig.Channel.Sent.Count == 0, "command uses the local service and never chat transport");
            That(rig.Screen.Submit("/help") && rig.Screen.TryTakeLine(out _), "help produces local feedback");
            That(rig.Screen.Submit("/kick \"Lee\"") && rig.Rig.Prompt?.Kind == ScreenPromptKind.Destructive, "kick asks a destructive question");
            That(!rig.Rig.Control.Calls.Contains("Kick:p1"), "kick cannot run before confirmation");
            rig.Rig.Answer(ScreenPromptKind.Destructive, false);
            That(!rig.Rig.Control.Calls.Contains("Kick:p1"), "cancelling kicks nobody");
            rig.Screen.Open(); rig.Screen.Submit("/ban Lee");
            rig.Rig.Answer(ScreenPromptKind.Destructive, true);
            That(rig.Rig.Control.Calls.Contains("Ban:p1"), "confirmed ban reaches existing moderation");
            rig.Screen.Open(); rig.Screen.Submit("/mute Lee");
            That(rig.Rig.Control.Muted.Contains("p1"), "mute applies server moderation");
            rig.Frame(); rig.Screen.Open(); rig.Screen.Submit("/mute Lee");
            That(!rig.Rig.Control.Muted.Contains("p1"), "mute toggles off");
            rig.Screen.Open(); rig.Screen.Submit("/kick Lee");
            rig.Rig.Environment.SessionSnapshot = HostSnapshot(MultiplayerSessionState.Ready, "chat", Player("p1", "Someone else", "replacement"));
            rig.Rig.Answer(ScreenPromptKind.Destructive, true);
            That(!rig.Rig.Control.Calls.Contains("Kick:p1"), "confirmation rechecks the player's identity");
            using var client = new PresenterRig(false);
            foreach (var command in new[] { "/kick Lee", "/ban Lee", "/mute Lee" })
            { client.Screen.Open(); That(!client.Screen.Submit(command), "client moderation is refused"); }
            That(client.Rig.Prompt == null && client.Channel.Sent.Count == 0, "client commands never go to the host or a confirmation");
        }

        private static void FocusEndsWithSessionAndDisposal()
        {
            using var rig = new PresenterRig(true);
            rig.Screen.Open(); rig.Screen.SetTyping(true);
            That(rig.Screen.IsTyping, "focused composer publishes typing");
            rig.Screen.Close();
            That(!rig.Screen.IsTyping && !rig.Screen.IsOpen, "close releases typing");
            rig.Screen.Open(); rig.Screen.SetTyping(true);
            rig.Rig.Status(InternalSessionRole.Solo); rig.Frame();
            That(!rig.Screen.IsTyping && !rig.Screen.IsOpen, "leaving releases typing");
            rig.Rig.Status(InternalSessionRole.ListenHost); rig.Frame();
            rig.Screen.Open(); rig.Screen.SetTyping(true);
            rig.Rig.Environment.SessionSnapshot = HostSnapshot(MultiplayerSessionState.Ready, "replacement"); rig.Frame();
            That(!rig.Screen.IsTyping && !rig.Screen.IsOpen, "replaced session releases typing");
            rig.Screen.Open(); rig.Screen.SetTyping(true); rig.Screen.Dispose();
            That(!rig.Screen.IsTyping && !rig.Channel.IsOpen, "disposal releases typing and the channel");
        }

        private static void ConnectionGraceAndNotices()
        {
            using var rig = new PresenterRig(false);
            rig.Rig.Status(InternalSessionRole.Client, InternalJoinPhase.Reconnecting); rig.Frame(1.49f);
            That(!rig.Screen.IsReconnecting, "short link losses show no banner");
            rig.Frame(0.02f);
            That(rig.Screen.IsReconnecting && rig.Screen.ConnectionText == "Reconnecting...", "banner appears after 1.5 seconds");
            rig.Rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession); rig.Frame();
            That(!rig.Screen.IsReconnecting, "recovery hides banner immediately");
            var additions = Enumerable.Range(2, 12).Select(index => Player("p" + index, "Guest " + index, "key" + index)).ToArray();
            rig.Rig.Environment.SessionSnapshot = ClientSnapshot(MultiplayerSessionState.Ready, "chat", "p1", additions);
            rig.Frame();
            That(rig.Rig.Notices.Count(notice => notice.Text.EndsWith("joined the session.", StringComparison.Ordinal)) == 3, "membership burst emits at most three individual toasts");
            rig.Now = 2_000_000; rig.Frame();
            That(rig.Rig.Notices.Any(notice => notice.Text.Contains("joined,", StringComparison.Ordinal)), "overflow is coalesced into a bounded summary");
            rig.Rig.Environment.SessionSnapshot = ClientSnapshot(MultiplayerSessionState.Ready, "chat", "p1");
            rig.Frame(); rig.Now = 4_000_000; rig.Frame();
            That(rig.Rig.Notices.Any(notice => notice.Text.Contains("left the session", StringComparison.Ordinal)), "departures surface with the same rate limit");
        }

        private static void PauseActionsFollowTheirLifetime()
        {
            using var rig = new PresenterRig(true);
            var service = new PauseService();
            var opened = 0;
            using var entries = new InSessionPauseEntries(rig.Rig.Presenter.Session, () => opened++);
            var world = new object();
            entries.Tick(service, world, true);
            That(service.Actions.Select(action => action.Label).SequenceEqual(new[] { "INVITE", "PLAYERS", "END SESSION" }), "host pause entries");
            service.Actions[0].Callback(); service.Actions[1].Callback();
            That(opened == 2, "invite and players route to session");
            service.Actions[2].Callback();
            That(rig.Rig.Prompt?.Kind == ScreenPromptKind.Destructive, "end session confirms through the destructive screen");
            rig.Rig.Answer(ScreenPromptKind.Destructive, false);
            var stale = service.Actions[2];
            entries.Tick(service, new object(), false);
            stale.Callback();
            That(service.Actions.Count == 0 && rig.Rig.Prompt == null, "old world callbacks are inert and leases are released");
            rig.Rig.Status(InternalSessionRole.Client, InternalJoinPhase.InSession); rig.Frame();
            entries.Tick(service, world, true);
            That(service.Actions[2].Label == "DISCONNECT", "client pause entry");
            service.Actions[2].Callback();
            rig.Rig.Answer(ScreenPromptKind.Destructive, true);
            That(rig.Rig.Control.Calls.Contains("Leave"), "disconnect uses the realm's existing leave flow");
        }

        private static void IdleFramesAllocateNothing()
        {
            using var rig = new PresenterRig(true);
            rig.Screen.Open(); rig.Screen.SetTyping(true);
            for (var index = 0; index < 200; index++) rig.Screen.Tick(0.016f);
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var index = 0; index < 10_000; index++)
            {
                rig.Screen.Tick(0.016f);
                _ = rig.Screen.ConnectionText; _ = rig.Screen.IsReconnecting; _ = rig.Screen.IsOpen;
                rig.Screen.SetTyping(true);
            }
            That(GC.GetAllocatedBytesForCurrentThread() == before, "open idle chat and shown connection state allocate zero bytes over 10,000 frames");
        }

        private sealed class PresenterRig : IDisposable
        {
            internal readonly SessionsRig Rig = new SessionsRig();
            internal readonly ChatChannel Channel = new ChatChannel();
            internal readonly InSessionPresenter Screen;
            internal long Now;
            internal string Executed = string.Empty;
            internal PresenterRig(bool host)
            {
                Rig.Control.ChatChannel = Channel;
                Rig.Status(host ? InternalSessionRole.ListenHost : InternalSessionRole.Client, host ? InternalJoinPhase.None : InternalJoinPhase.InSession);
                Rig.Environment.SessionSnapshot = host ? HostSnapshot(MultiplayerSessionState.Ready, "chat", Player("p1", "Lee", "one"))
                    : ClientSnapshot(MultiplayerSessionState.Ready, "chat", "p1");
                Rig.Frame();
                Screen = new InSessionPresenter(Rig.Presenter.Context, Rig.Presenter.Session, Execute, new SessionChat { Clock = () => Now });
                Frame();
            }
            private bool Execute(string name, IReadOnlyList<string> args, out OperationResult<string>? result)
            { Executed = name + ":" + string.Join(" ", args); result = OperationResult<string>.Success("Done."); return true; }
            internal void Frame(float dt = 0.05f)
            {
                Rig.Frame(deltaTime: dt); Screen.Tick(dt);
                while (Rig.Presenter.TryTakeNotice(out var notice)) Rig.Notices.Add(notice);
            }
            public void Dispose() => Screen.Dispose();
        }

        private sealed class PauseService : IWorldPauseMenuService
        {
            internal List<WorldPauseAction> Actions { get; } = new List<WorldPauseAction>();
            public bool IsAvailable => true;
            public OperationResult<IDisposable> RegisterAction(WorldPauseAction action)
            { Actions.Add(action); return OperationResult<IDisposable>.Success(new Lease(() => Actions.Remove(action))); }
            public OperationResult<IDisposable> InterceptExit(Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor) => throw new NotSupportedException();
        }
        private sealed class Lease : IDisposable
        {
            private readonly Action dispose;
            internal Lease(Action dispose) { this.dispose = dispose; }
            public void Dispose() => dispose();
        }
    }
}
