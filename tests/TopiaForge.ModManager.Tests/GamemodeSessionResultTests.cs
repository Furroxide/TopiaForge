using System;
using System.Linq;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Tests
{
    internal static class GamemodeSessionResultTests
    {
        internal static void Run(string root)
        {
            foreach (var code in new[] { (ModErrorCode)999, (ModErrorCode)(-1) })
            {
                TestStartupFailure(root + "-world-" + (int)code, code, world: true);
                TestStartupFailure(root + "-mode-" + (int)code, code, world: false);
                TestMenuFailure(root + "-menu-" + (int)code, code);
            }
            var rejectedNone = false;
            try { OperationResult<bool>.Failure(ModErrorCode.None, "invalid success code"); }
            catch (ArgumentOutOfRangeException) { rejectedNone = true; }
            Assert(rejectedNone, "the SDK must not construct a failed result with the success-only None code");
            Console.WriteLine("GamemodeSessionResultTests passed.");
        }

        private static void TestStartupFailure(string root, ModErrorCode code, bool world)
        {
            var test = new SessionFixture(root);
            var disposed = 0;
            void Allocate(IModLifetime lifetime)
            {
                lifetime.Defer(() => disposed++);
                lifetime.Defer(() => { disposed++; throw new InvalidOperationException("independent cleanup failure"); });
            }
            if (world)
                SessionFixture.Load = (context, _) =>
                {
                    Allocate(context.Context.Lifetime);
                    return Task.FromResult(OperationResult<IWorldInstance>.Failure(code, "invalid author result"));
                };
            else
                SessionFixture.Start = (session, _) =>
                {
                    Allocate(session.Lifetime);
                    return Task.FromResult(OperationResult<IGamemodeController>.Failure(code, "invalid author result"));
                };
            var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "invalid-result");
            test.Wait(launch);
            test.Until(() => !test.Host.HasPendingWork);
            Assert(test.Hosted.Current.Phase == SessionPhase.Idle && test.Parent.ActiveChildScopeCount == 0
                && !test.Native.IsSceneBusy && disposed == 2,
                "an undefined author error code must not bypass attempted cleanup, terminal Idle, or native release");
            Assert(!launch.Result.Succeeded && launch.Result.ErrorCode == ModErrorCode.External,
                "undefined author error codes must become a supported external operation failure");
            var command = test.Outcomes.Single(value => value.Kind == "launch");
            var terminal = test.Outcomes.Single(value => value.Kind == "session");
            Assert(command.Status == "failed" && command.Error?.Code == "external"
                && terminal.Status == "failed" && terminal.Error!.Message.Contains("independent cleanup failure"),
                "invalid results still publish exactly one launch and terminal outcome with all cleanup failures");
            test.Dispose();
        }

        private static void TestMenuFailure(string root, ModErrorCode code)
        {
            var test = new SessionFixture(root);
            test.Launch();
            test.Menu = _ => Task.FromResult(OperationResult<bool>.Failure(code, "invalid menu result"));
            var menu = SessionFixture.ModeSession!.ReturnToMainMenuAsync();
            test.Wait(menu);
            test.Until(() => !test.Host.HasPendingWork);
            Assert(!menu.Result.Succeeded && menu.Result.ErrorCode == ModErrorCode.External,
                "main-menu results must use the same supported operation error normalization");
            Assert(test.Outcomes.Count(value => value.Kind == "launch" && value.Command == "main-menu"
                && value.Status == "failed" && value.Error?.Code == "external") == 1
                && test.Outcomes.Count(value => value.Kind == "session") == 1,
                "an invalid main-menu result must not omit its command outcome or duplicate the session outcome");
            Assert(test.Hosted.Current.Phase == SessionPhase.Idle && !test.Native.IsSceneBusy,
                "invalid main-menu results release native and lifecycle admission");
            test.Dispose();
        }
        private static void Assert(bool condition, string message) => GamemodeSessionOrchestratorTests.Assert(condition, message);
    }
}
