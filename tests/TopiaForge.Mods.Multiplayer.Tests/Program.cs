namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class Program
{
    private static int Main()
    {
        try
        {
            MultiplayerIdentityTests.SnapshotsRejectInvalidOrAmbiguousIdentityData();
            MultiplayerRigTests.RolesExposeCorrectSides();
            MultiplayerRigTests.ServerAndTwoClientsConverge();
            MultiplayerRigTests.RejectedPredictionRollsBack();
            MultiplayerRigTests.ReorderedInputRejectsStaleAndReplays();
            MultiplayerRigTests.OwnerPredictionAndOwnershipTransfer();
            MultiplayerRigTests.ObjectInputRejectsStaleAfterReordering();
            MultiplayerRigTests.ReorderedObjectConfirmationsDoNotDoubleApplyPrediction();
            MultiplayerRigTests.ObjectTombstonesDominateDelayedSpawnAndChangeSnapshots();
            MultiplayerRigTests.LateJoinAndReconnectSnapshotPrecedesReady();
            MultiplayerRigTests.PacketFaultsConvergeWithoutDoubleExecution();
            MultiplayerRigTests.ListenHostExecutesCanonicalHandlerOnce();
            MultiplayerRigTests.ObjectDiscoveryChangeAndDespawnAreTyped();
            MultiplayerRigTests.PresentationEventsRoundTripBoundedBytes();
            MultiplayerRigTests.FailingHandlersAndCodecsRollbackTransactionally();
            MultiplayerRigTests.ObjectInputsTransactionallyIncludeReplicatedState();
            MultiplayerRigTests.PartialStateDeltasReplayAllPendingPredictions();
            MultiplayerRigTests.PresentationDeliveryIsReadyBoundAtMostOnceAndConnectionScoped();
            MultiplayerRigTests.DisconnectReconnectRefreshesParticipantsOwnershipAndObjects();
            MultiplayerRigTests.SequentialSessionsReuseFacadeAndResetSessionState();
            MultiplayerRigTests.SequentialSessionResetObserversSeeCoherentMetadata();
            MultiplayerRigTests.CommandAndObjectRateLimitsUseVirtualTime();
            MultiplayerRigTests.MutableValuesAreIsolatedAcrossPeersAndConfirmations();
            MultiplayerRigTests.GeneratedCounterSampleConverges();
            MultiplayerRigTests.GeneratedDroneSamplePredicts();
            LoopbackProviderTests.RateLimitsEachAuthenticatedSenderPerCommand();
            LoopbackProviderTests.PreAdmissionRejectionsDoNotAdvanceCanonicalTick();
            LoopbackProviderTests.SessionObserversRunAfterSettlementAndCannotReenterCommands();
            LoopbackProviderTests.RejectedAndThrowingCommandsRollbackAllStateAndPresentation();
            LoopbackProviderTests.ReplicatedObjectsEnforceInputAndStateCodecBounds();
            LoopbackProviderTests.CommandTransactionsRejectReentrantGraphMutation();
            LoopbackProviderTests.OwnerFacadesIsolateConsumerLifetimes();
            LoopbackProviderTests.MutableValuesNeverEscapeCanonicalStorage();
            Console.WriteLine("All TopiaForge multiplayer test-rig tests passed.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
}
