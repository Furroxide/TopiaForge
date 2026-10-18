using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Questions that outlive the game they were asked about, and a provider that throws while hosting or a join starts.
    /// Prompts persist across scene loads, so a question whose game ended meanwhile would otherwise still show over the next
    /// one, and answering it would act on a game the player never asked about.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static void PromptsCloseWhenTheirGameChanges()
        {
            // HOST in a running game: the first host's disclosure asks about opening this game. The game ends while it shows.
            var host = new SessionsRig(acknowledged: false);
            host.Environment.Phase = SessionPhase.Running;
            ReadyHost(host);
            host.Presenter.Host.Host();
            host.Frame();
            var asked = host.Prompt;
            That(asked?.Kind == ScreenPromptKind.Disclosure, "opening a running game for the first time asks the disclosure");
            host.Frame(5);
            That(ReferenceEquals(host.Prompt, asked), "the prompt stays while the game it asks about runs");
            host.Environment.Phase = SessionPhase.Idle;
            host.Frame();
            That(host.Prompt == null && asked!.IsAnswered, "once that game ended, the prompt closes by itself");
            asked!.Answer(true);
            host.Frame(3);
            That(host.Environment.PendingLaunch == null && !host.Control.Calls.Contains("StartHosting") && host.Store.Stored!.AcknowledgedDisclosure == 0,
                "and a late yes neither starts a game the player never chose, nor opens anything, nor counts as acknowledged");
            That(host.Noticed(MultiplayerSessionsPresenter.PromptWithdrawn), "the player is told nothing was done");

            // The same, for a game that begins to stop.
            var disclosure = new SessionsRig(acknowledged: false);
            disclosure.Environment.Phase = SessionPhase.Running;
            ReadyHost(disclosure);
            disclosure.Presenter.Host.Host();
            disclosure.Frame();
            That(disclosure.Prompt?.Kind == ScreenPromptKind.Disclosure, "the first host shows the disclosure");
            disclosure.Environment.Phase = SessionPhase.Stopping;
            disclosure.Frame();
            That(disclosure.Prompt == null && disclosure.Store.Stored!.AcknowledgedDisclosure == 0 && disclosure.Environment.PendingLaunch == null,
                "it closes when the game stops, acknowledging nothing and starting nothing");

            // "Leave current game?" for a game that ended by itself.
            var leave = BrowsingRig(ready: true);
            leave.Environment.Phase = SessionPhase.Running;
            leave.Presenter.Browse.Select(new string('1', 64));
            leave.Presenter.Browse.Join();
            leave.Frame();
            That(leave.Prompt?.Title == ConnectingPresenter.LeaveTitle, "joining from a running game asks to leave it");
            leave.Environment.Phase = SessionPhase.Idle;
            leave.Frame();
            That(leave.Prompt == null && leave.Environment.PendingMenu == null && !Joined(leave) && !leave.Presenter.Connecting.IsVisible,
                "a game that ended by itself closes the question, and nothing is left or joined");

            // SESSION's questions belong to the session they were asked in.
            var joined = new SessionsRig();
            joined.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            joined.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-q", "p1");
            joined.Frame();
            joined.Presenter.Session.Leave();
            joined.Frame();
            var leaveServer = joined.Prompt;
            That(leaveServer?.Title == "Leave server?", "a joined player is asked before leaving");
            joined.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.HostEnded);
            joined.Environment.SessionSnapshot = null;
            joined.Frame();
            leaveServer!.Answer(true);
            joined.Frame();
            That(joined.Prompt == null && !joined.Control.Calls.Contains("Leave") && !joined.Noticed("You left the session."),
                "a session that ended closes \"Leave server?\", and a late yes leaves nothing and says nothing was left");

            var ending = HostingWithAda();
            ending.Presenter.Session.EndForEveryone();
            ending.Frame();
            That(ending.Prompt?.Title == HostPresenter.EndTitle, "a host is asked before ending the session");
            ending.Status(InternalSessionRole.Solo, host: InternalHostPhase.Failed);
            ending.Frame();
            That(ending.Prompt == null && !ending.Control.Calls.Contains("StopHosting"), "hosting that stopped closes the question");

            var kick = HostingWithAda();
            kick.Presenter.Session.Select("p1");
            kick.Presenter.Session.Kick();
            kick.Frame();
            That(kick.Prompt?.Title == "Remove Ada?", "a host is asked before removing a player");
            kick.Status(InternalSessionRole.Solo, host: InternalHostPhase.Idle);
            kick.Frame();
            That(kick.Prompt == null && !kick.Control.Calls.Any(call => call.StartsWith("Kick", StringComparison.Ordinal)),
                "and that question closes once hosting stopped");

            // A question that still holds is never withdrawn: Hide my IP's disclosure belongs to no game.
            var setting = new SessionsRig();
            setting.Environment.Phase = SessionPhase.Running;
            setting.Presenter.SetHideMyIp(false);
            setting.Frame();
            setting.Environment.Phase = SessionPhase.Idle;
            setting.Frame(5);
            That(setting.Prompt?.Kind == ScreenPromptKind.Disclosure && !setting.Noticed(MultiplayerSessionsPresenter.PromptWithdrawn),
                "turning Hide my IP off asks until the player answers, whatever the game does");
        }

        /// <summary>
        /// A provider that throws while a waited-for game is opened, or while a join that waited starts, ends that attempt
        /// once, in this game's words: it is not tried again every frame, and nothing escapes the presenter's frame.
        /// </summary>
        private static void AThrowingProviderEndsTheAttemptOnce()
        {
            var failure = new NotSupportedException("the socket on 203.0.113.7:27250 was refused");
            var host = new SessionsRig();
            ReadyHost(host);
            host.Control.ThrowOnStartHosting = failure;
            host.Presenter.Host.Host();
            host.Frame();
            host.Environment.Phase = SessionPhase.Running;
            host.Environment.PendingLaunch!(true, "Launch target reached Running.");
            host.Frame(10);
            That(host.Control.Calls.Count(call => call == "StartHosting") == 1, "opening the started game is tried once");
            That(host.Presenter.Host.ProgressTone == ScreenTone.Danger && host.Presenter.Host.FormEnabled && !Shows(host.Presenter.Host.ProgressText) &&
                 !host.Notices.Any(notice => Shows(notice.Text)), "and fails in this game's words, with no address, the form usable again");
            That(host.Presenter.Host.ProgressText.IndexOf("socket", StringComparison.Ordinal) < 0 &&
                 !host.Notices.Any(notice => notice.Text.IndexOf("socket", StringComparison.Ordinal) >= 0),
                "and none of the provider's own words reach the screen or a toast (" + host.Presenter.Host.ProgressText + ")");

            var waited = BrowsingRig(ready: true);
            waited.Control.ThrowOnJoin = failure;
            waited.Environment.Phase = SessionPhase.LoadingWorld;
            waited.Presenter.Browse.Select(new string('1', 64));
            waited.Presenter.Browse.Join();
            waited.Frame();
            waited.Environment.Phase = SessionPhase.Idle;
            waited.Frame(10);
            That(waited.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal)) == 1, "a join that waited is tried once");
            That(waited.Presenter.Connecting.HasFailed && !Shows(waited.Presenter.Connecting.FailureMessage) && waited.Presenter.Connecting.CanRetry,
                "and its failure view shows, with no address and a retry");
            That(waited.Presenter.Connecting.FailureMessage.IndexOf("socket", StringComparison.Ordinal) < 0,
                "and in this game's words, not the provider's (" + waited.Presenter.Connecting.FailureMessage + ")");

            var clicked = BrowsingRig(ready: true);
            clicked.Control.ThrowOnJoin = failure;
            clicked.Presenter.Browse.Select(new string('1', 64));
            clicked.Presenter.Browse.Join();
            clicked.Frame(3);
            That(clicked.Presenter.Connecting.HasFailed && clicked.Control.Calls.Count(call => call.StartsWith("Join", StringComparison.Ordinal)) == 1,
                "a join the player starts at once fails the same way");
        }

        /// <summary>
        /// The view's side of the prompts: one dialog at a time, the next once the first is answered, a withdrawn prompt's
        /// dialog closed, and a dialog that cannot be shown counting as no so the queue moves on.
        /// </summary>
        private static void PromptsShowOneAtATimeAndCloseWhenWithdrawn()
        {
            var rig = new SessionsRig();
            var dialogs = new FakePromptDialogs();
            var presentation = new PromptPresentation(dialogs.Show);
            var first = PrivacyPrompts.Destructive("First?", "One.", "YES", _ => { });
            var secondHolds = true;
            var second = PrivacyPrompts.Destructive("Second?", "Two.", "YES", _ => { }, () => secondHolds);
            rig.Presenter.Context.Ask(first);
            rig.Presenter.Context.Ask(second);
            presentation.Tick(rig.Presenter);
            presentation.Tick(rig.Presenter);
            That(dialogs.Shown.Count == 1 && ReferenceEquals(dialogs.Shown[0], first) && ReferenceEquals(presentation.Showing, first), "one question shows at a time");

            first.Answer(true);
            presentation.Tick(rig.Presenter);
            That(dialogs.Shown.Count == 2 && ReferenceEquals(dialogs.Shown[1], second) && dialogs.Closed.Contains(first), "the next shows once the first is answered");

            secondHolds = false;
            rig.Frame();
            presentation.Tick(rig.Presenter);
            That(second.IsAnswered && second.IsWithdrawn && dialogs.Closed.Contains(second) && presentation.Showing == null && dialogs.Shown.Count == 2,
                "a question withdrawn because its game changed has its dialog closed, and nothing else shows");
            That(rig.Noticed(MultiplayerSessionsPresenter.PromptWithdrawn) && rig.Notices.Count(notice => notice.Text == MultiplayerSessionsPresenter.PromptWithdrawn) == 1,
                "and the player is told once that nothing was done");

            var answers = new System.Collections.Generic.List<bool>();
            var broken = PrivacyPrompts.Destructive("Broken?", "Three.", "YES", answers.Add);
            var after = PrivacyPrompts.Destructive("After?", "Four.", "YES", _ => { });
            rig.Presenter.Context.Ask(broken);
            rig.Presenter.Context.Ask(after);
            dialogs.FailNext = true;
            Throws<InvalidOperationException>(() => presentation.Tick(rig.Presenter), "a dialog that cannot be shown reports its failure to the frame");
            That(broken.IsAnswered && answers.SequenceEqual(new[] { false }) && presentation.Showing == null, "and its question counts as no");
            presentation.Tick(rig.Presenter);
            That(ReferenceEquals(presentation.Showing, after), "so the next question can show");

            var late = PrivacyPrompts.Destructive("Late?", "Five.", "YES", answers.Add, () => false);
            late.Answer(true);
            That(late.IsWithdrawn && answers.Last() == false, "a yes that lands after its game changed counts as no");

            // A question whose premise cannot be read counts as one that stopped applying: nothing acts on a guess.
            var unreadable = PrivacyPrompts.Destructive("Unreadable?", "Six.", "YES", answers.Add, () => throw new InvalidOperationException("the game could not say"));
            var asked = new SessionsRig();
            asked.Presenter.Context.Ask(unreadable);
            asked.Frame();
            That(unreadable.IsWithdrawn && answers.Last() == false && asked.Prompt == null && asked.Noticed(MultiplayerSessionsPresenter.PromptWithdrawn),
                "a question whose premise cannot be read is withdrawn as no, and the player is told");

            // Questions wait in a bounded queue: past it, another one answers no at once, so no action is left half done.
            var full = new SessionsRig();
            var waiting = Enumerable.Range(0, MultiplayerSessionsPresenter.MaximumPrompts)
                .Select(index => PrivacyPrompts.Destructive("Question " + index + "?", "Body.", "YES", _ => { })).ToArray();
            foreach (var question in waiting) full.Presenter.Context.Ask(question);
            var refused = new System.Collections.Generic.List<bool>();
            var extra = PrivacyPrompts.Destructive("One more?", "Body.", "YES", refused.Add);
            full.Presenter.Context.Ask(extra);
            That(extra.IsAnswered && refused.SequenceEqual(new[] { false }) && waiting.All(question => !question.IsAnswered) && ReferenceEquals(full.Prompt, waiting[0]),
                "past " + MultiplayerSessionsPresenter.MaximumPrompts + " waiting questions another answers no at once, and the rest keep their order");
        }

        /// <summary>Dialogs as the view would open them, recorded; one can be made to fail as it opens.</summary>
        private sealed class FakePromptDialogs
        {
            internal System.Collections.Generic.List<ScreenPrompt> Shown { get; } = new System.Collections.Generic.List<ScreenPrompt>();

            internal System.Collections.Generic.List<ScreenPrompt> Closed { get; } = new System.Collections.Generic.List<ScreenPrompt>();

            internal bool FailNext { get; set; }

            internal IPromptDialog Show(ScreenPrompt prompt)
            {
                if (FailNext)
                {
                    FailNext = false;
                    throw new InvalidOperationException("the dialog could not be built");
                }

                Shown.Add(prompt);
                return new Dialog(this, prompt);
            }

            private sealed class Dialog : IPromptDialog
            {
                private readonly FakePromptDialogs owner;
                private readonly ScreenPrompt prompt;

                internal Dialog(FakePromptDialogs owner, ScreenPrompt prompt)
                {
                    this.owner = owner;
                    this.prompt = prompt;
                }

                public void Close() => owner.Closed.Add(prompt);
            }
        }
    }
}
