using System.Diagnostics;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class LaunchedSessionTests
{
    private static void JournalWorkAdmissionRegressions()
    {
        AJournalWorkerIsAdmittedBeforeVirtualTimeAdvances();
        AJournalWriteFailureStillFails();
        AnUnchangedPreferenceIsDistinguishedFromAWriteFailure();
    }

    private static void AJournalWorkerIsAdmittedBeforeVirtualTimeAdvances()
    {
        using var rig = new ControlRig(7_815, Sandbox);
        var work = new ScreensBackgroundWork();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var screens = PeerScreens.Of(rig.Host, RequestId, Digest, scheduler: work, beforeCommit: () =>
        {
            started.Set();
            if (!release.Wait(rig.BackgroundWorkBound)) throw new JournalGateTimeout();
        });
        try
        {
            That(work.Run(rig, screens, () => screens.Presenter.Preferences.IsLoaded), "the regression preferences load off the frame");
            screens.Presenter.SetDisplayName("Sammy");
            That(started.Wait(rig.BackgroundWorkBound), "the real journal worker reaches the commit gate");

            // The worker cannot finish until released: no timing/sleep assumption is needed to expose the old gap.
            var beforeUntracked = rig.Network.Now;
            That(!rig.Run(() => Frame(screens, () => !screens.Presenter.Preferences.IsSaving && screens.Store.ReadPreferencesJournal() != null)),
                "the provider-only wait does not admit the independent gated screen worker");
            That(rig.Network.Now - beforeUntracked == 10_000_000 && screens.Presenter.Preferences.IsSaving && screens.Store.ReadPreferencesJournal() == null,
                "the original virtual deadline expires while the real screen write is still pending");

            var beforeOwned = rig.Network.Now;
            work.OnWaiting = () =>
            {
                That(rig.Network.Now == beforeOwned && screens.Presenter.Preferences.IsSaving,
                    "owned admission reaches the pending worker without advancing virtual time");
                release.Set();
            };
            var journal = RequireJournalChange(rig, screens, work);
            That(rig.Network.Now == beforeOwned, "worker completion is pumped before virtual time advances");
            var change = journal.Changes.Single();
            That(change.Kind == "displayName" && change.NewDisplayName == "Sammy" && rig.Host.Runtime.PreferredDisplayName == "Sammy",
                "the delayed journal retains the original content and immediate runtime change");
            That(screens.Channels.Calls.Any(call => call.Call == nameof(ILauncherChannels.RecordPreferenceChange)), "the delayed change uses the channel");
            Clean(screens, rig.Host.Log.Lines);
        }
        finally
        {
            release.Set();
            work.OnWaiting = null;
            work.Join(rig.BackgroundWorkBound);
        }
    }

    private static void AJournalWriteFailureStillFails()
    {
        using var rig = new ControlRig(7_816, Sandbox);
        var work = new ScreensBackgroundWork();
        using var screens = PeerScreens.Of(rig.Host, RequestId, Digest, scheduler: work, beforeCommit: () => throw new JournalWriteFailure());
        try
        {
            That(work.Run(rig, screens, () => screens.Presenter.Preferences.IsLoaded), "the failure regression preferences load");
            screens.Presenter.SetDisplayName("Sammy");
            JournalAssertionFails(() => RequireJournalChange(rig, screens, work), "record-failed notice");
            That(!screens.Presenter.Preferences.IsSaving && screens.Store.ReadPreferencesJournal() == null && rig.Host.Runtime.PreferredDisplayName == "Sammy",
                "a failed write is idle and unreadable while the original runtime change remains in effect");
            Clean(screens, rig.Host.Log.Lines);
        }
        finally { work.Join(rig.BackgroundWorkBound); }
    }

    private static void AnUnchangedPreferenceIsDistinguishedFromAWriteFailure()
    {
        using var rig = new ControlRig(7_817, Sandbox);
        var work = new ScreensBackgroundWork();
        using var screens = PeerScreens.Of(rig.Host, RequestId, Digest, scheduler: work);
        try
        {
            That(work.Run(rig, screens, () => screens.Presenter.Preferences.IsLoaded), "the unchanged regression preferences load");
            var unchanged = screens.Presenter.Preferences.Current.DisplayName;
            screens.Presenter.SetDisplayName(unchanged);
            JournalAssertionFails(() => RequireJournalChange(rig, screens, work, unchanged: true), "unchanged preference");
            That(!screens.Channels.Calls.Any(call => call.Call == nameof(ILauncherChannels.RecordPreferenceChange))
                && !screens.Notices.Contains(MultiplayerPreferencesService.RecordFailedText) && rig.Host.Runtime.PreferredDisplayName == unchanged,
                "the explicit unchanged-input seam queues no write and invents no failure notice");
            Clean(screens, rig.Host.Log.Lines);
        }
        finally { work.Join(rig.BackgroundWorkBound); }
    }

    private static MultiplayerPreferencesJournal RequireJournalChange(ControlRig rig, PeerScreens screens, ScreensBackgroundWork work, bool unchanged = false)
    {
        MultiplayerPreferencesJournal? journal = null;
        var settled = work.Run(rig, screens, () =>
        {
            if (screens.Presenter.Preferences.IsSaving) return false;
            journal = screens.Store.ReadPreferencesJournal();
            return journal != null || screens.Notices.Contains(MultiplayerPreferencesService.RecordFailedText) || unchanged;
        });
        var failed = screens.Notices.Contains(MultiplayerPreferencesService.RecordFailedText);
        var state = failed ? "record-failed notice" : screens.Presenter.Preferences.IsSaving ? "still pending" :
            unchanged && !screens.Channels.Calls.Any(call => call.Call == nameof(ILauncherChannels.RecordPreferenceChange)) ? "unchanged preference" :
            journal == null ? "completed without readable journal" : "readable journal";
        That(settled && journal != null && !failed && state != "unchanged preference", "the change is recorded for the launcher (" + state + ")");
        return journal!;
    }

    private static void JournalAssertionFails(Func<MultiplayerPreferencesJournal> assertion, string state)
    {
        string? failure = null;
        try { assertion(); }
        catch (InvalidOperationException error) { failure = error.Message; }
        That(failure == "Assertion failed: the change is recorded for the launcher (" + state + ")",
            "journal failure preserves the failing assertion and its fixed diagnostic classification");
    }

    private sealed class JournalGateTimeout : Exception { }
    private sealed class JournalWriteFailure : Exception { }

    /// <summary>Admits only this screen queue's real Background drains before the rig moves virtual time.</summary>
    private sealed class ScreensBackgroundWork : IMultiplayerWorkScheduler
    {
        private readonly object gate = new();
        private readonly List<Task> scheduled = new();
        internal Action? OnWaiting;

        public void Schedule(Action work)
        {
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate) scheduled.Add(completed.Task);
            try
            {
                MultiplayerWorkSchedulers.Background.Schedule(() =>
                {
                    try { work(); }
                    finally { completed.TrySetResult(); }
                });
            }
            catch
            {
                completed.TrySetResult();
                throw;
            }
        }

        internal bool Run(ControlRig rig, PeerScreens screens, Func<bool> condition)
        {
            var waiting = new Stopwatch();
            // The rig's virtual deadline and provider-work admission are unchanged.
            return rig.Run(() =>
            {
                while (true)
                {
                    screens.Frame();
                    var pending = Pending();
                    if (pending.Count == 0)
                    {
                        // A drain may have queued its completion between the first Pump and this snapshot.
                        screens.Frame();
                        if (Pending().Count == 0) return condition();
                        continue;
                    }
                    waiting.Start();
                    try
                    {
                        var onWaiting = OnWaiting;
                        OnWaiting = null;
                        onWaiting?.Invoke();
                        pending.Wait(waiting, rig.BackgroundWorkBound);
                    }
                    finally { waiting.Stop(); }
                }
            });
        }

        internal void Join(TimeSpan bound)
        {
            var waiting = Stopwatch.StartNew();
            while (true)
            {
                var pending = Pending();
                if (pending.Count == 0) return;
                pending.Wait(waiting, bound);
            }
        }

        private BackgroundWorkWait Pending()
        {
            var pending = new BackgroundWorkWait();
            lock (gate) pending.Add("screens", scheduled.Where(task => !task.IsCompleted).Select(task => ("file work", task)));
            return pending;
        }
    }
}
