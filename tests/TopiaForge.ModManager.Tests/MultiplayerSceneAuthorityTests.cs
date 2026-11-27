using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class MultiplayerSceneAuthorityTests
    {
        public static void Run()
        {
            var request = new SceneTransitionRequest("tests.mode", "World", SceneTransitionPriority.UserInitiated);
            var absent = new MultiplayerSceneTransitionAuthorityPolicy(() => null);
            Assert(absent.Evaluate(request).Allowed, "No multiplayer provider preserves standalone scene access.");

            foreach (var sides in new[] { MultiplayerExecutionSide.Server,
                MultiplayerExecutionSide.Server | MultiplayerExecutionSide.Client })
            {
                var authoritative = new MultiplayerSceneTransitionAuthorityPolicy(() => Snapshot(MultiplayerSessionState.Ready, sides));
                Assert(authoritative.Evaluate(request).Allowed, "A ready canonical server may change the world.");
            }

            MultiplayerSessionSnapshot snapshot = Snapshot(MultiplayerSessionState.Ready, MultiplayerExecutionSide.Client);
            var policy = new MultiplayerSceneTransitionAuthorityPolicy(() => snapshot);
            var coordinator = new SceneCoordinator(authorityPolicy: policy);
            var rejected = coordinator.RequestTransition(request);
            Assert(!rejected.Approved && rejected.ErrorCode == ModErrorCode.NotAuthoritative
                && rejected.Claim == null && !coordinator.IsSceneBusy,
                "A client refusal happens before ownership or native effects.");

            foreach (var state in new[] { MultiplayerSessionState.Connecting,
                MultiplayerSessionState.Synchronizing, MultiplayerSessionState.Ended })
            {
                snapshot = Snapshot(state, MultiplayerExecutionSide.Server);
                Assert(!policy.Evaluate(request).Allowed, "Unready or ended sessions cannot submit native world work.");
            }
            snapshot = Snapshot(MultiplayerSessionState.Ready, MultiplayerExecutionSide.Server);
            Assert(policy.Evaluate(request).Allowed, "Each admission reads fresh authority after a session change.");
            snapshot = Snapshot(MultiplayerSessionState.Ready, MultiplayerExecutionSide.Client);
            Assert(!policy.Evaluate(request).Allowed, "An old authoritative snapshot is never cached.");

            var broken = new MultiplayerSceneTransitionAuthorityPolicy(() => throw new InvalidOperationException("provider fault"));
            Assert(!broken.Evaluate(request).Allowed, "A failed authority provider cannot become standalone permission.");

            using var rig = MultiplayerTestRig.CreateStandalone();
            var registry = new ModServiceRegistry();
            var registered = registry.RegisterExtension<IMultiplayerSession>(MultiplayerModule.Id,
                rig.Server.Session, ExtensionCardinality.Singleton);
            Assert(registered.Succeeded, "The reserved production provider is registered through the real registry.");
            var production = new MultiplayerSceneTransitionAuthorityPolicy(registry);
            Assert(production.Evaluate(request).Allowed, "The production adapter reads the registered standalone server.");
            registered.Value!.Dispose();
            Assert(production.Evaluate(request).Allowed, "Removing the provider restores explicit standalone operation.");
            Console.WriteLine("Multiplayer scene authority tests passed.");
        }

        private static MultiplayerSessionSnapshot Snapshot(MultiplayerSessionState state, MultiplayerExecutionSide sides) =>
            new MultiplayerSessionSnapshot(new MultiplayerSessionId("tests.authority"), state,
                MultiplayerProcessKind.Interactive, sides, null, Array.Empty<MultiplayerParticipant>(),
                new NetworkTick(0), new SessionSeed(1));

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
