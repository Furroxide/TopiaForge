using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Sandbox;

namespace TopiaForge.ModManager.Tests
{
    internal static class GamemodeConsumerRegressionTests
    {
        public static void Run()
        {
            SandboxDoesNotCloseAnotherCreatorHost();
            ZombiesControllerTests.RunCleanupRegression();
            SandboxGamemodeTests.Run();
        }

        internal static void SandboxDoesNotCloseAnotherCreatorHost()
        {
            using var context = new FakeModContext();
            using var content = new FakeCreatorContentService(context.Lifetime);
            var robots = new FakeRobotKit(context.Lifetime);
            var router = new ForeignHostRouter();
            using var controller = new SandboxController(context, new SandboxConfig(), robots.Agents,
                content, router, "example.world");
            var result = controller.EndSession();
            if (!result.Succeeded || router.CloseCalls != 0)
                throw new InvalidOperationException("Sandbox workbench cleanup must not close another package's active creator host.");
        }

        private sealed class ForeignHostRouter : ICreatorToolHostService
        {
            public int CloseCalls { get; private set; }
            public CreatorToolHostDescriptor ActiveHost { get; } = new CreatorToolHostDescriptor(
                "another.package:tools", "another.package", "tools", "Other tools", 300);
            public OperationResult<ICreatorToolHostRegistration> RegisterHost(CreatorToolHostRegistrationRequest request) =>
                OperationResult<ICreatorToolHostRegistration>.Failure(ModErrorCode.Unavailable, "Not used.");
            public OperationResult<bool> Toggle() => OperationResult<bool>.Success(false);
            public OperationResult<bool> CloseActive(CreatorToolCloseReason reason = CreatorToolCloseReason.Requested)
            { CloseCalls++; return OperationResult<bool>.Success(true); }
        }
    }
}
