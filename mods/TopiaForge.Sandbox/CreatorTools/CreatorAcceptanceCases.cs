using System.Collections.Generic;
using System.Globalization;

namespace TopiaForge.CreatorTools.Shared
{
    /// <summary>
    /// One real workbench transition the recorder can witness.
    /// </summary>
    /// <remarks>
    /// Every member corresponds to a state change the workbench performed and
    /// confirmed, never to an intent or a script assertion. A case only passes
    /// when all of its required observations were witnessed in one run.
    /// </remarks>
    internal enum CreatorObservation
    {
        // creator.f5-routing-and-session
        OpenedDuringStandaloneGameplay,
        HiddenWithSessionPreserved,
        ReopenedSameSession,
        BlockedOutsideStandaloneGameplay,

        // creator.catalog-items-ugc-and-robots
        SpawnedCuratedItem,
        SpawnedUgcProp,
        SpawnedRobotKitRobot,
        CatalogSearchNarrowed,
        CatalogKindFilterApplied,
        DuplicatedInstance,
        TransformedInstance,
        RemovedInstance,
        UndoRestoredInstance,

        // creator.personality-and-location-restore
        PreviewedRobotPersonality,
        PreviewedRobotLocation,
        RestoredRobotStateOnEnd,

        // creator.custom-character-and-vehicle-sources
        SpawnedCustomCharacter,
        SpawnedValidatedVehicle,
        UnloadedCustomFactoriesSafely,
        NativeVehicleSourceReportedEmpty,

        // creator.persistence-isolation
        MutationBlockedWithoutLease,
        AcknowledgedGlobalMutationOnce,
        AcquiredPersistenceIsolationLease,
        RestoredAfterIsolationRevoked,

        // creator.event-graph-and-rollback
        LoadedLocalEventProject,
        RanBoundedGraphBranches,
        StoppedGraphAndRolledBack,
        PreservedUnrelatedManualSpawns,

        // creator.scene-transition-cleanup
        SceneReplacementClosedWorkbench,
        WorldsTransitionClosedWorkbench,
        RemoteMultiplayerClosedWorkbench,
        ModUnloadClosedWorkbench,

        // creator.source-unload-cleanup
        UnloadedContentSource,
        RemovedSourceCatalogEntries,
        RemovedSourceInstancesLifoOnce,
        PrunedStaleRosterWithoutCrash,
    }

    /// <summary>Declarative gate for one canonical Creator acceptance case.</summary>
    internal sealed class CreatorAcceptanceCase
    {
        private readonly CreatorObservation[] required;
        private readonly int minimumCycles;

        public CreatorAcceptanceCase(
            string id,
            CreatorObservation[] required,
            int minimumCycles = 0)
        {
            Id = id;
            this.required = required;
            this.minimumCycles = minimumCycles;
        }

        public string Id { get; }

        public bool IsSatisfied(
            ICollection<CreatorObservation> observed,
            int completedCycles)
        {
            if (completedCycles < minimumCycles) return false;
            foreach (var observation in required)
            {
                if (!observed.Contains(observation)) return false;
            }
            return true;
        }

        public string Describe(int completedCycles) =>
            minimumCycles > 0
                ? "observed " + required.Length.ToString(
                    CultureInfo.InvariantCulture) + " transitions over "
                    + completedCycles.ToString(CultureInfo.InvariantCulture)
                    + " cycles"
                : "observed " + required.Length.ToString(
                    CultureInfo.InvariantCulture) + " transitions";
    }

    /// <summary>
    /// The nine canonical creator cases from tests/live-game-acceptance.json.
    /// </summary>
    internal static class CreatorAcceptanceCases
    {
        /// <summary>Required cycles for the ten-lifecycle-cycles case.</summary>
        public const int RequiredLifecycleCycles = 10;

        public static readonly CreatorAcceptanceCase[] All =
        {
            new CreatorAcceptanceCase(
                "creator.f5-routing-and-session",
                new[]
                {
                    CreatorObservation.OpenedDuringStandaloneGameplay,
                    CreatorObservation.HiddenWithSessionPreserved,
                    CreatorObservation.ReopenedSameSession,
                    CreatorObservation.BlockedOutsideStandaloneGameplay,
                }),
            new CreatorAcceptanceCase(
                "creator.catalog-items-ugc-and-robots",
                new[]
                {
                    CreatorObservation.SpawnedCuratedItem,
                    CreatorObservation.SpawnedUgcProp,
                    CreatorObservation.SpawnedRobotKitRobot,
                    CreatorObservation.CatalogSearchNarrowed,
                    CreatorObservation.CatalogKindFilterApplied,
                    CreatorObservation.DuplicatedInstance,
                    CreatorObservation.TransformedInstance,
                    CreatorObservation.RemovedInstance,
                    CreatorObservation.UndoRestoredInstance,
                }),
            new CreatorAcceptanceCase(
                "creator.personality-and-location-restore",
                new[]
                {
                    CreatorObservation.PreviewedRobotPersonality,
                    CreatorObservation.PreviewedRobotLocation,
                    CreatorObservation.RestoredRobotStateOnEnd,
                }),
            new CreatorAcceptanceCase(
                "creator.custom-character-and-vehicle-sources",
                new[]
                {
                    CreatorObservation.SpawnedCustomCharacter,
                    CreatorObservation.SpawnedValidatedVehicle,
                    CreatorObservation.UnloadedCustomFactoriesSafely,
                    CreatorObservation.NativeVehicleSourceReportedEmpty,
                }),
            new CreatorAcceptanceCase(
                "creator.persistence-isolation",
                new[]
                {
                    CreatorObservation.MutationBlockedWithoutLease,
                    CreatorObservation.AcknowledgedGlobalMutationOnce,
                    CreatorObservation.AcquiredPersistenceIsolationLease,
                    CreatorObservation.RestoredAfterIsolationRevoked,
                }),
            new CreatorAcceptanceCase(
                "creator.event-graph-and-rollback",
                new[]
                {
                    CreatorObservation.LoadedLocalEventProject,
                    CreatorObservation.RanBoundedGraphBranches,
                    CreatorObservation.StoppedGraphAndRolledBack,
                    CreatorObservation.PreservedUnrelatedManualSpawns,
                }),
            new CreatorAcceptanceCase(
                "creator.scene-transition-cleanup",
                new[]
                {
                    CreatorObservation.SceneReplacementClosedWorkbench,
                    CreatorObservation.WorldsTransitionClosedWorkbench,
                    CreatorObservation.RemoteMultiplayerClosedWorkbench,
                    CreatorObservation.ModUnloadClosedWorkbench,
                }),
            new CreatorAcceptanceCase(
                "creator.source-unload-cleanup",
                new[]
                {
                    CreatorObservation.UnloadedContentSource,
                    CreatorObservation.RemovedSourceCatalogEntries,
                    CreatorObservation.RemovedSourceInstancesLifoOnce,
                    CreatorObservation.PrunedStaleRosterWithoutCrash,
                }),
            // The persistence half of this case is proven on the harness side
            // by byte-comparing the real save document across End Session; the
            // recorder proves the ten clean cycles actually completed in-game.
            new CreatorAcceptanceCase(
                "creator.ten-lifecycle-cycles",
                new[]
                {
                    CreatorObservation.OpenedDuringStandaloneGameplay,
                    CreatorObservation.HiddenWithSessionPreserved,
                    CreatorObservation.ReopenedSameSession,
                    CreatorObservation.StoppedGraphAndRolledBack,
                },
                RequiredLifecycleCycles),
        };
    }
}
