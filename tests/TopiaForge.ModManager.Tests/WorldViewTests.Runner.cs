using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldViewTests
    {
        private static void Runner()
        {
            var factory = new ViewFactory();
            using var runner = new GamemodeSessionRunner(factory).WithView(new FakeWorldView(new[] { "day", "night" }, "day"));
            Check(runner.StartAsync().GetAwaiter().GetResult().Succeeded, "runner supplies view automatically");
            Check(runner.View.EffectiveFog.HasValue && factory.Lease!.IsActive, "session.View uses server lifetime");
            Check(factory.ParticipantCode == ModErrorCode.NotAuthoritative, "runner participant cannot use the server view");
            using var map = new FakeAuthoredMap().AddEntity("crate", "Crate");
            using var hidden = Lease(runner.Session!.View().Hide(map.Entity("crate")));
            Check(runner.View.HiddenEntities.Count == 1, "authored entity overload delegates to live target");
            runner.Stop();
            Check(!factory.Lease!.IsActive && !hidden.IsActive && !runner.View.IsAvailable, "runner stop clears view leases");
        }
        private sealed class ViewFactory : IGamemodeFactory, IGamemodeParticipantFactory
        {
            internal IViewLease? Lease;
            internal ModErrorCode ParticipantCode;
            public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken)
            {
                Lease = WorldViewTests.Lease(session.View().Fog(VisionFog.Blackout));
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new Controller()));
            }
            public Task<OperationResult<IGamemodeParticipantController>> StartParticipantAsync(IGamemodeParticipantSession session, CancellationToken cancellationToken)
            {
                ParticipantCode = session.Context.RequireExtension<IWorldView>().Fog(VisionFog.Blackout).ErrorCode;
                return Task.FromResult(OperationResult<IGamemodeParticipantController>.Success(new Controller()));
            }
        }
        private sealed class Controller : IGamemodeController, IGamemodeParticipantController { public void Dispose() { } }
    }
}
