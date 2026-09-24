using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ObjectiveRunnerTests
    {
        private static IRobotObjectiveService Facade(RobotObjectiveService service, OwnerModLifetime lifetime) =>
            (IRobotObjectiveService)((IOwnerBoundExtensionFactory)service).CreateOwnerFacade(
                typeof(IRobotObjectiveService), "tests.same-package", lifetime);

        private static void TestStoppedOwnerCannotSetObjectives()
        {
            var (service, _) = NewService();
            using var oldLifetime = new OwnerModLifetime();
            using var newLifetime = new OwnerModLifetime();
            var oldFacade = Facade(service, oldLifetime);
            var current = Facade(service, newLifetime);
            var agent = new FakeRobotAgent();
            var objective = current.SetObjective(agent, RobotObjective.Idle()).Value!;
            oldLifetime.BeginStop();
            var rejected = oldFacade.SetObjective(agent, RobotObjective.GoTo(new Vec3(10, 0, 0)));
            Assert(!rejected.Succeeded && rejected.ErrorCode == ModErrorCode.InvalidState && objective.IsActive,
                "stopped SetObjective must not cancel or overwrite a newer same-package objective");
        }

        private static void TestStoppedOwnerCannotClearObjectives()
        {
            var (service, _) = NewService();
            using var oldLifetime = new OwnerModLifetime();
            using var newLifetime = new OwnerModLifetime();
            var oldFacade = Facade(service, oldLifetime);
            var current = Facade(service, newLifetime);
            var agent = new FakeRobotAgent();
            var objective = current.SetObjective(agent, RobotObjective.Idle()).Value!;
            oldLifetime.BeginStop();
            var rejected = oldFacade.ClearObjective(agent);
            Assert(!rejected.Succeeded && rejected.ErrorCode == ModErrorCode.InvalidState && objective.IsActive,
                "stopped ClearObjective must leave the current same-package objective intact");
        }

        private static void TestStoppedOwnerCannotPublishTargets()
        {
            var (service, _) = NewService();
            using var oldLifetime = new OwnerModLifetime();
            using var newLifetime = new OwnerModLifetime();
            var oldFacade = Facade(service, oldLifetime);
            var current = Facade(service, newLifetime);
            var registered = current.RegisterTarget("target", RobotTargetKind.Marker,
                () => new RobotTargetSnapshot(new Vec3(1, 0, 0))).Value!;
            oldLifetime.BeginStop();
            var rejected = oldFacade.RegisterTarget("target", RobotTargetKind.Marker,
                () => new RobotTargetSnapshot(new Vec3(2, 0, 0)));
            Assert(!rejected.Succeeded && rejected.ErrorCode == ModErrorCode.InvalidState && registered.IsActive,
                "stopped RegisterTarget must refuse before entering the shared target registry");
        }

        private static void TestStoppedOwnerDoesNotReceiveDelivery()
        {
            var recipient = new FakeRobotAgent { Position = new Vec3(10, 0, 0) };
            var (service, _, _) = NewRunnerService(entity => ReferenceEquals(entity, recipient) ? recipient : null);
            using var oldLifetime = new OwnerModLifetime();
            using var newLifetime = new OwnerModLifetime();
            var oldFacade = Facade(service, oldLifetime);
            var current = Facade(service, newLifetime);
            var oldDeliveries = 0;
            var newDeliveries = 0;
            oldFacade.ProgramDelivered += _ => oldDeliveries++;
            current.ProgramDelivered += _ => newDeliveries++;
            current.RegisterTarget("recipient", RobotTargetKind.Robot,
                () => new RobotTargetSnapshot(recipient.Position, recipient));
            var courier = new FakeRobotAgent();
            current.SetObjective(courier, RobotObjective.Reprogram("recipient", RobotObjective.Idle()));
            service.Tick(0.016f);
            oldLifetime.BeginStop();
            courier.HasReachedTarget = true;
            service.Tick(0.016f);
            Assert(oldDeliveries == 0 && newDeliveries == 1,
                "cancelled subscription stays silent while sibling delivery remains active before deferred disposal");
        }
    }
}
