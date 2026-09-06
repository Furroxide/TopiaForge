using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static class WorldsActivationTests
    {
        internal static async Task RunAsync()
        {
            await FreePlayNeedsOnlyItsPreparedSession();
            GenericNativeOverrideTableRestoresExactValues();
            CleanupAttemptsAllAndKeepsCapturedOwner();
            await PauseExitUsesOneBoundOperation();
            await PauseBlockAndNonRunningRefuse();
            await ThrowingInterceptorStillUsesBoundExit();
            Console.WriteLine("Worlds activation ownership tests passed (6 cases).");
        }
        private static async Task PauseExitUsesOneBoundOperation()
        {
            var session = new Session();
            var exit = new PauseExitOperation();
            var pending = exit.RunAsync(Running(session), null, _ => { });
            Assert(session.MenuCalls == 1 && !pending.IsCompleted, "Pause exit must await exactly one bound menu operation.");
            var competing = await exit.RunAsync(Running(session), null, _ => { });
            Assert(competing.ErrorCode == ModErrorCode.Conflict && session.MenuCalls == 1, "Repeated pause clicks must be Busy.");
            session.Menu.SetResult(OperationResult<bool>.Failure(ModErrorCode.External, "menu failed"));
            Assert((await pending).ErrorMessage == "menu failed", "Native menu failure must reach the caller.");
        }
        private static async Task PauseBlockAndNonRunningRefuse()
        {
            var session = new Session(); var exit = new PauseExitOperation();
            var blocked = await exit.RunAsync(Running(session), _ => WorldPauseExitDecision.Block, _ => { });
            var stopping = await exit.RunAsync(new WorldSessionSnapshot(WorldSessionPhase.Stopping, session, null, 5), null, _ => { });
            Assert(blocked.Succeeded && !blocked.Value && stopping.ErrorCode == ModErrorCode.Conflict && session.MenuCalls == 0,
                "A veto or stopping session cannot dispatch a native menu transition.");
        }
        private static async Task ThrowingInterceptorStillUsesBoundExit()
        {
            var session = new Session(); var errors = 0;
            var pending = new PauseExitOperation().RunAsync(Running(session), _ => throw new InvalidOperationException("hook"), _ => errors++);
            Assert(errors == 1 && session.MenuCalls == 1, "A throwing hook must be diagnosed and still use the bound exit.");
            session.Menu.SetResult(OperationResult<bool>.Success(true));
            Assert((await pending).Succeeded, "Successful menu return must be reported.");
        }
        private static void CleanupAttemptsAllAndKeepsCapturedOwner()
        {
            var calls = new List<string>(); var captured = "old"; var newer = "new";
            var lease = new LocalImportOwnership(() => { calls.Add(captured); throw new InvalidOperationException("discard"); },
                () => { calls.Add("overrides"); throw new InvalidOperationException("restore"); }, () => calls.Add("selection"));
            try { lease.Dispose(); throw new Exception("Throwing native cleanup was hidden."); }
            catch (AggregateException error) { Assert(error.Flatten().InnerExceptions.Count == 2, "All cleanup faults must be retained."); }
            lease.Dispose();
            Assert(calls.Count == 3 && calls[0] == "old" && !calls.Contains(newer), "Cleanup must be exactly once and keep its captured native owner.");
        }
        private static async Task FreePlayNeedsOnlyItsPreparedSession()
        {
            using var context = new FakeModContext(new ModIdentity(WorldsModule.Id, "Worlds", SemanticVersion.Parse("0.1.0-rc.1")));
            var session = new FakeGamemodeSession(context,
                new WorldReadiness(new WorldSceneIdentity(7, "native-world"), TransformState.Identity),
                gamemodeId: WellKnownWorldIds.FreePlayGamemode, worldId: WellKnownWorldIds.OpenSandboxWorld);
            var factory = new FreePlayGamemode();
            var started = await factory.StartAsync(session, CancellationToken.None);
            Assert(started.TryGetValue(out var controller), "Free Play must start with a prepared Worlds session and no Sandbox package.");
            var actual = controller!; actual.Dispose(); actual.Dispose();
            using var stopped = new CancellationTokenSource(); stopped.Cancel();
            Assert((await factory.StartAsync(session, stopped.Token)).ErrorCode == ModErrorCode.Cancelled,
                "Free Play must refuse startup after cancellation.");
        }
        private static void GenericNativeOverrideTableRestoresExactValues()
        {
            var original = new object();
            var table = new Dictionary<string, object> { ["native"] = original };
            var captured = new NativeImportOverrides(table);
            table["native"] = new object(); table["temporary"] = new object();
            captured.Restore();
            Assert(table.Count == 1 && ReferenceEquals(table["native"], original),
                "The actual generic native override dictionary must restore exact values and absence.");
        }
        private static WorldSessionSnapshot Running(Session session) => new WorldSessionSnapshot(WorldSessionPhase.Running, session,
            new WorldReadiness(new WorldSceneIdentity(7, "world"), new TransformState(new Vec3(1, 2, 3), Quat.Identity, new Vec3(1, 1, 1))), 4);
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        private sealed class Session : IWorldSession
        {
            internal int MenuCalls;
            internal readonly TaskCompletionSource<OperationResult<bool>> Menu = new TaskCompletionSource<OperationResult<bool>>();
            public string SessionId => "session-old"; public string TargetId => "example.mode.menu";
            public string GamemodeId => "example.mode.play"; public string WorldId => "example.world.map"; public string? WorldFamilyId => null;
            public Task<OperationResult<bool>> StopAsync(CancellationToken token = default) => throw new NotSupportedException();
            public Task<OperationResult<bool>> RestartAsync(CancellationToken token = default) => throw new NotSupportedException();
            public Task<OperationResult<bool>> ReturnToMainMenuAsync(CancellationToken token = default) { MenuCalls++; return Menu.Task; }
        }
    }
}
