using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The phase the realm rules read from a real session orchestrator. An idle session that is still changing counts as
    /// stopping: a return to the main menu until the menu has loaded, a native scene change, and shutdown. So a join, which
    /// replaces the local session, waits exactly when the orchestrator would refuse to start a session.
    /// </summary>
    internal static class MultiplayerRealmPhaseTests
    {
        internal static void Run(string root)
        {
            Directory.CreateDirectory(root);
            AJoinWaitsUntilTheMainMenuHasLoaded(Path.Combine(root, "menu"));
            IdleChangesAreBusyToo(Path.Combine(root, "idle"));
        }

        private static void AJoinWaitsUntilTheMainMenuHasLoaded(string root)
        {
            using var fixture = new SessionFixture(root);
            var (runtime, control, _) = Runtime(() => fixture.Hosted.RealmPhase);
            runtime.Connect();
            That(fixture.Hosted.RealmPhase == SessionPhase.Idle && runtime.EvaluateRealmChange(NetRealm.Client).IsAllowed,
                "a settled idle session lets a join through");
            fixture.Launch();
            That(fixture.Hosted.RealmPhase == SessionPhase.Running &&
                 runtime.EvaluateRealmChange(NetRealm.Client).Verdict == NetRealmTransitionVerdict.RequiresLeave,
                "a running session must be left first");

            // The player answers "Leave current game?", and the game returns to the main menu, which takes a while to load.
            var menuLoad = new TaskCompletionSource<OperationResult<bool>>();
            fixture.Menu = _ => menuLoad.Task;
            var menu = SessionFixture.ModeSession!.ReturnToMainMenuAsync();
            fixture.Until(() => fixture.MenuLoads == 1);
            That(fixture.Hosted.Current.Phase == SessionPhase.Idle && fixture.Hosted.RealmPhase == SessionPhase.Stopping,
                "the session has ended but the menu still loads, so the local session is still changing");
            var join = runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            var host = runtime.OpenToMultiplayer(new InternalHostSettings("Early", 4));
            That(join.Verdict == NetRealmTransitionVerdict.Busy && join.Result.ErrorCode == ModErrorCode.Conflict &&
                 join.Result.ErrorMessage == MultiplayerRuntime.SessionBusy && host.Verdict == NetRealmTransitionVerdict.Busy &&
                 control.RealmCalls.Count == 0, "a join or hosting asked for meanwhile waits, and the provider hears of neither (" + join + ", " + host + ")");
            var launch = fixture.Hosted.StartAsync(fixture.Plan.Descriptor, "during-menu");
            fixture.Wait(launch);
            That(launch.Result.ErrorCode == ModErrorCode.Conflict, "exactly as the orchestrator refuses a launch then (" + launch.Result.ErrorMessage + ")");

            menuLoad.SetResult(OperationResult<bool>.Success(true));
            fixture.Wait(menu);
            That(menu.Result.Succeeded && fixture.Hosted.RealmPhase == SessionPhase.Idle, "once the menu has loaded, the session has settled");
            var joined = runtime.Join(new FakeJoinTarget(), new InternalJoinSettings());
            That(joined.Succeeded && joined.Effect == NetRealmTransitionEffect.ReplaceLocalSession && control.RealmCalls.SequenceEqual(new[] { "join" }),
                "and the join goes through from the menu (" + joined + ")");
        }

        private static void IdleChangesAreBusyToo(string root)
        {
            using var fixture = new SessionFixture(root);

            // The main menu loading again from the menu.
            var menuLoad = new TaskCompletionSource<OperationResult<bool>>();
            fixture.Menu = _ => menuLoad.Task;
            var menu = fixture.Hosted.ReturnToMainMenuAsync();
            fixture.Until(() => fixture.MenuLoads == 1);
            That(fixture.Hosted.Current.Phase == SessionPhase.Idle && fixture.Hosted.RealmPhase == SessionPhase.Stopping,
                "the menu loading from the menu is a change too");
            menuLoad.SetResult(OperationResult<bool>.Success(true));
            fixture.Wait(menu);
            That(fixture.Hosted.RealmPhase == SessionPhase.Idle, "until it has loaded");

            // A native scene change that holds the scene without a session operation.
            var reservation = fixture.Native.TryReserve(new NativeTransitionOwner("io.example.native", "native:realm-phase"), "realm-phase").Value!;
            That(fixture.Native.IsSceneBusy && fixture.Hosted.RealmPhase == SessionPhase.Stopping, "a native scene change is a change");
            fixture.Wait(reservation.CloseAsync());
            That(fixture.Hosted.RealmPhase == SessionPhase.Idle, "until it ends");

            fixture.Wait(fixture.Hosted.ShutdownAsync());
            That(fixture.Hosted.Current.Phase == SessionPhase.Idle && fixture.Hosted.RealmPhase == SessionPhase.Stopping,
                "and a runtime that shuts down never settles again");

            // The orchestrator's own operation is enough, even where the native layer reports no scene change.
            using var quiet = new SessionFixture(root + "-quiet", decorateNative: native => new QuietScenes(native));
            var quietLoad = new TaskCompletionSource<OperationResult<bool>>();
            quiet.Menu = _ => quietLoad.Task;
            var quietMenu = quiet.Hosted.ReturnToMainMenuAsync();
            quiet.Until(() => quiet.MenuLoads == 1);
            That(quiet.Hosted.Current.Phase == SessionPhase.Idle && quiet.Hosted.RealmPhase == SessionPhase.Stopping,
                "an operation that holds an idle session is a change");
            quietLoad.SetResult(OperationResult<bool>.Success(true));
            quiet.Wait(quietMenu);
            That(quiet.Hosted.RealmPhase == SessionPhase.Idle, "until it ends");
        }

        /// <summary>A native layer that never reports its scene as busy.</summary>
        private sealed class QuietScenes : INativeTransitionExecutor
        {
            private readonly INativeTransitionExecutor inner;

            internal QuietScenes(INativeTransitionExecutor inner)
            {
                this.inner = inner;
            }

            public bool IsSceneBusy => false;

            public void SetSessionAdmissionGate(System.Func<bool> isBusy) => inner.SetSessionAdmissionGate(isBusy);

            public Task<NativeDrainResult> WaitForIdleAsync() => inner.WaitForIdleAsync();

            public OperationResult<INativeTransitionReservation> TryReserve(NativeTransitionOwner owner, string operationId) =>
                inner.TryReserve(owner, operationId);
        }
    }
}
