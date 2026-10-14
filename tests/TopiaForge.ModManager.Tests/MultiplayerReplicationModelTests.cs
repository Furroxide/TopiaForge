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
            MultiplayerRelevancyTests.Run();
            MultiplayerSchedulerTests.Run();
            MultiplayerOwnershipTests.Run();
            MultiplayerSceneKeyTests.Run();
            MultiplayerSpawnSlotTests.Run();
            MultiplayerPermissionTests.Run();
            MultiplayerPresentationTests.Run();
            MultiplayerReplicationGoldenTests.Run();
        }
    }
}
