namespace TopiaForge.ModManager.Tests
{
    /// <summary>Runs every test of the Unity-free replication model in TopiaForge.ModManager.Core.Multiplayer.</summary>
    internal static class MultiplayerReplicationModelTests
    {
        public static void Run()
        {
            MultiplayerRealmRulesTests.Run();
            MultiplayerQuantizationTests.Run();
            MultiplayerEntityTableTests.Run();
            MultiplayerInterpolationTests.Run();
            MultiplayerInterpolationLagTests.Run();
            MultiplayerRelevancyTests.Run();
            MultiplayerSchedulerTests.Run();
            MultiplayerOwnershipTests.Run();
            MultiplayerOwnershipLimitsTests.Run();
            MultiplayerOwnershipClaimTests.Run();
            MultiplayerSceneKeyTests.Run();
            MultiplayerSpawnSlotTests.Run();
            MultiplayerPermissionTests.Run();
            MultiplayerPresentationTests.Run();
            MultiplayerReplicationGoldenTests.Run();
            MultiplayerMotionGoldenTests.Run();
        }
    }
}
