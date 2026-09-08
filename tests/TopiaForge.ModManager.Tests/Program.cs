using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class Program
    {
        private static int Main(string[] args)
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            if (args.Length == 1 && args[0] == "--world-marker-hierarchy") { WorldMarkerHierarchyTests.Run(FindRepoRoot()); return 0; }
            if (args.Length == 1 && args[0] == "--acceptance-isolation") { AcceptanceIsolationTests.Run(); AcceptanceIsolationStagingTests.Run(); AcceptancePluginLifecycleTests.Run(); return 0; }
            if (args.Length == 1 && args[0] == "--runtime-launch-command") { var owned = Directory.CreateTempSubdirectory("TopiaForgeLaunchCommand-"); try { RuntimeLaunchCommandTests.Run(owned.FullName); RuntimeLaunchPublicationTests.Run(Path.Combine(owned.FullName, "publication")); return 0; } finally { owned.Delete(true); } }
            if (args.Length == 1 && args[0] == "--profile-v4-policy") { ProfileLaunchV4PolicyTests.Run(); RuntimeStartupSelectionTests.Run(); ManagerLaunchSelectionTests.Run(); LegacyManagerSelectionTests.Run(); WorldLaunchArmingTests.Run(); LaunchTargetPreviewTests.Run(); return 0; }
            if (args.Length == 1 && args[0] == "--launch-staging") { LaunchStorageKeyTests.Run(FindRepoRoot()); LaunchStagingTests.Run(); return 0; }
            if (args.Length >= 1 && args.Length <= 2 &&
                string.Equals(args[0], "--print-sdk-api-baseline", StringComparison.Ordinal))
            {
                if (args.Length == 2)
                {
                    Console.Write(SdkPublicApiBaselineTests.CreateBaseline(args[1]));
                }
                else
                {
                    Console.Write(SdkPublicApiBaselineTests.CreateBaseline());
                }

                return 0;
            }

            if (args.Length == 2 &&
                string.Equals(args[0], "--update-sdk-api-baselines", StringComparison.Ordinal))
            {
                SdkPublicApiBaselineTests.UpdateBaselines(args[1]);
                return 0;
            }

            if (args.Length == 1 &&
                string.Equals(args[0], "--sdk-api-baselines", StringComparison.Ordinal))
            {
                SdkPublicApiBaselineTests.Run();
                return 0;
            }

            if (args.Length == 2 && string.Equals(args[0], "--content-admission-case", StringComparison.Ordinal))
            {
                var contentRoot = Directory.CreateTempSubdirectory("TopiaForgeContentTests-").FullName;
                try { ContentAdmissionTests.Run(contentRoot, args[1]); return 0; }
                finally { TryDelete(contentRoot); }
            }

            if (args.Length == 1 && string.Equals(args[0], "--worlds-activation", StringComparison.Ordinal))
            { WorldsActivationTests.RunAsync().GetAwaiter().GetResult(); return 0; }
            if (args.Length == 1 && string.Equals(args[0], "--acceptance-factory", StringComparison.Ordinal))
            { AcceptanceGamemodeFactoryTests.RunAsync().GetAwaiter().GetResult(); return 0; }

            if (args.Length == 1 && string.Equals(args[0], "--manifest-activation-serialization", StringComparison.Ordinal))
            { ManifestActivationSerializationTests.Run(); return 0; }
            if (args.Length == 1 && string.Equals(args[0], "--duplicate-selection", StringComparison.Ordinal))
            { DuplicateSelectionTests.Run(); return 0; }
            if (args.Length == 1 && string.Equals(args[0], "--world-readiness-contract", StringComparison.Ordinal))
            {
                var readinessRoot = Directory.CreateTempSubdirectory("TopiaForgeReadinessTests-").FullName;
                try { WorldReadinessContractTests.Run(readinessRoot); return 0; }
                finally { TryDelete(readinessRoot); }
            }

            if ((args.Length == 1 || args.Length == 2) && string.Equals(args[0], "--legacy-launch-discovery", StringComparison.Ordinal))
            { LegacyLaunchDiscoveryTests.RunAsync(FindRepoRoot(), args.Length == 2 ? args[1] : null).GetAwaiter().GetResult(); return 0; }

            if (args.Length == 1 && string.Equals(args[0], "--session-lifecycle", StringComparison.Ordinal))
            {
                var sessionRoot = Directory.CreateTempSubdirectory("TopiaForgeSessionTests-").FullName;
                try
                {
                    HostDispatcherTests.Run();
                    SessionLifecycleTests.Run(Path.Combine(sessionRoot, "state"));
                    GamemodeSessionOrchestratorTests.Run(sessionRoot);
                    SessionActivationTests.Run(sessionRoot);
                    ContentAdmissionTests.Run(Path.Combine(sessionRoot, "content-admission"));
                    WorldsActivationTests.RunAsync().GetAwaiter().GetResult();
                    AcceptanceGamemodeFactoryTests.RunAsync().GetAwaiter().GetResult();
                    LocalImportOperationTests.RunAsync().GetAwaiter().GetResult();
                    RuntimeSessionSelectionTests.Run();
                    NativeWorldReflectionTests.Run();
                    LegacyLaunchDiscoveryTests.RunAsync(FindRepoRoot()).GetAwaiter().GetResult();
                    BuiltinWorldProviderTests.Run();
                    WorldRuntimeReadinessTests.Run();
                    WorldReadinessContractTests.Run(Path.Combine(sessionRoot, "readiness-contract"));
                    DuplicateSelectionTests.Run();
                    AssetNativeDrainTests.Run();
                    OwnerNativeSceneLoadTests.Run();
                    NativeWorkLifecycleTests.Run(Path.Combine(sessionRoot, "native-work"));
                    return 0;
                }
                finally { TryDelete(sessionRoot); }
            }

            if (args.Length == 1 && string.Equals(args[0], "--sdk-lifecycle", StringComparison.Ordinal))
            {
                var lifecycleRoot = Path.Combine(
                    Path.GetTempPath(),
                    "TopiaForgeSdkLifecycleTests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(lifecycleRoot);
                try
                {
                    SdkLifecycleTests.Run(lifecycleRoot);
                    ScopedModContextTests.Run(lifecycleRoot);
                    ScopedAssetOwnershipTests.Run(lifecycleRoot);
                    AssetSpawnTransactionTests.Run();
                    UiHotkeyOwnershipTests.Run();
                    ContextBoundExtensionTests.Run();
                    ScopedExtensionFacadeTests.Run();
                    return 0;
                }
                finally
                {
                    TryDelete(lifecycleRoot);
                }
            }

            if (args.Length == 1 && string.Equals(args[0], "--gameplay-facades", StringComparison.Ordinal))
            {
                GameplayFacadeTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--testing-kit", StringComparison.Ordinal))
            {
                TestingKitTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--creator-workbench", StringComparison.Ordinal))
            {
                CreatorWorkbenchLifecycleTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--creator-event-graph", StringComparison.Ordinal))
            {
                CreatorEventGraphRunnerTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--robot-personality-bindings", StringComparison.Ordinal))
            {
                RobotPersonalityBindingSurfaceTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--scene-coordinator", StringComparison.Ordinal))
            {
                SceneCoordinatorTests.Run();
                NativeTransitionExecutorTests.Run();
                MultiplayerSceneAuthorityTests.Run();
                SceneTransitionTrackerTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--menu-entry-point", StringComparison.Ordinal))
            {
                MenuSurfaceCensusTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--gamemode-contract", StringComparison.Ordinal))
            {
                GamemodeContractConformanceTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--manifest-retirement", StringComparison.Ordinal))
            {
                ManifestRetirementTests.Run();
                return 0;
            }
            if (args.Length == 1 && string.Equals(args[0], "--manifest-v6", StringComparison.Ordinal))
            {
                var manifestRoot = Path.Combine(
                    Path.GetTempPath(),
                    "TopiaForgeManifestV6Tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(manifestRoot);
                try
                {
                    TestStrictManifestExtensions();
                    ManifestV6Tests.Run(manifestRoot);
                    MultiplayerContractLockBoundaryTests.Run(manifestRoot);
                    return 0;
                }
                finally
                {
                    TryDelete(manifestRoot);
                }
            }

            if (args.Length == 1 && string.Equals(args[0], "--zombies-controller", StringComparison.Ordinal))
            {
                ZombiesControllerTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--zombies-config", StringComparison.Ordinal))
            {
                ZombiesConfigTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--module-contracts", StringComparison.Ordinal))
            {
                SdkSurfaceTests.Run();
                ModuleContractSurfaceTests.Run();
                TestingKitTests.Run();
                PromptRegistryTests.Run();
                RobotDirectiveTests.Run();
                OverrideTests.Run();
                ConversationTests.Run();
                RobotPersonalityBindingSurfaceTests.Run();
                ObjectiveRunnerTests.Run();
                SandboxProgramDirectorTests.Run();
                ModServiceRegistryTests.Run();
                ChronosTests.Run();
                CreatorContentTests.Run();
                CreatorSceneAdapterTests.Run();
                CreatorEventGraphRunnerTests.Run();
                CreatorWorkbenchLifecycleTests.Run();
                WorldsSafetyTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--first-party-manifests", StringComparison.Ordinal))
            {
                FirstPartyManifestTests.Run();
                return 0;
            }

            if (args.Length == 1 &&
                string.Equals(args[0], "--installed-version-coexistence", StringComparison.Ordinal))
            {
                var coexistenceRoot = Path.Combine(
                    Path.GetTempPath(),
                    "TopiaForgeInstalledVersionTests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(coexistenceRoot);
                try
                {
                    InstalledVersionCoexistenceTests.Run(coexistenceRoot);
                    return 0;
                }
                finally
                {
                    TryDelete(coexistenceRoot);
                }
            }

            var root = Directory.CreateTempSubdirectory("TopiaForgeModManagerTests-").FullName;

            try
            {
                if (args.Contains("--world-providers"))
                {
                    WorldDiscoveryIdentityTests.Run();
                    WorldResourceScopeTests.Run();
                    WorldPlayerPlacementTests.Run();
                    GeneratedArenaGeometryTests.Run();
                    WorldProviderLoaderTests.Run();
                    Console.WriteLine("World provider tests passed.");
                    return 0;
                }

                TestInstallSuccess(root);
                TestLegacyPackageExtensionRejected(root);
                TestUpdatePreservesDisabledState(root);
                TestDevToolInstallsDisabledAndUpdatePreservesState(root);
                TestAppliedRestartRequirementsClear();
                RuntimePersistenceSecurityTests.Run(root);
                StartupJournalTests.Run(root);
                PackageInstallReceiptTests.Run(root);
                ManagedModAssemblyValidatorTests.Run(root);
                RuntimePayloadDependencyTests.Run();
                ServiceScaffoldRuntimeTests.Run(root);
                BoundedTextFileTests.Run(root);
                ExtractorFileIoTests.Run(root);
                ModContextConfigPersistenceTests.Run(root);
                RoboApiClientTests.Run(root);
                TestMissingManifestRejected(root);
                TestZipTraversalRejected(root);
                TestCaseChangedZipTraversalRejected(root);
                TestArchiveManifestLimitRejected(root);
                TestDuplicateArchivePathRejected(root);
                TestUnicodeArchivePathPolicy(root);
                TestArchivePathCollisionRejected(root);
                TestArchiveLinkRejected(root);
                TestArchiveEntryCountRejected(root);
                TestNonPortableArchivePathsRejected(root);
                TestReplacementRollbackPreservesInstalledPackage(root);
                TestSchemaV1Rejected(root);
                TestRetiredManifestAliasesRejected(root);
                TestStrictManifestExtensions();
                TestInstallPreservesOtherVersions(root);
                TestInboxInstallConsumesFiles(root);
                TestInboxNewestVersionWins(root);
                TestInboxPrereleasePrecedence(root);
                TestInboxFallsBackFromIncompatibleHigherVersion(root);
                TestInboxFallsBackFromCorruptHigherVersion(root);
                TestInboxEqualVersionUsesNormalizedPath(root);
                TestInboxChangedAfterPreflightIsRetained(root);
                TestInboxChangedSupersededCandidateIsRetained(root);
                TestInboxEnumerationLimitsFailClosed(root);
                TestInboxRejectsNonRegularCandidate(root);
                TestInboxFailureLeavesFile(root);
                TestScanIgnoresSupersededBrokenVersions(root);
                TestScanStillReportsFullyBrokenPackage(root);
                TestScanRecoversDevToolAsDisabled(root);
                TestScanSelectsDependencyCompatibleProviderVersion(root);
                TestScanBacktracksConsumerVersionForCompleteAssignment(root);
                InstalledVersionCoexistenceTests.Run(root);
                TestRequiredDependenciesHelper();
                TestDependencyOrder(root);
                TestFrameworkDependencyOrder(root);
                TestDependencyFailurePropagation(root);
                TestSoftDependencyCyclesDoNotBlock(root);
                TestDependencyVersionRangeSemantics(root);
                TestManifestDependencyIdsRejected();
                TestRetiredEcosystemIdRootsRejected();
                VersionUtilTests.Run();
                GameCompatibilityTests.Run(root);
                ManifestPathValidationTests.Run();
                ManifestRetirementTests.Run();
                ManifestV6Tests.Run(root);
                MultiplayerContractLockBoundaryTests.Run(root);
                MultiplayerAdmissionTests.Run();
                FirstPartyManifestTests.Run();
                FirstPartyConfigTests.Run();
                ModAssemblyResolutionCatalogTests.Run(root);
                TestUgcExportSchemaContract();
                TestPendingRuntimeManifestContracts();
                WorldLaunchSettingsTests.Run();
                ZombiesConfigTests.Run();
                ZombiesControllerTests.Run();
                UgcNoOpLaunchRequestTests.Run();
                SdkSurfaceTests.Run();
                RuntimeInfoTests.Run();
                ModuleContractSurfaceTests.Run();
                V1LaunchCoverageTests.Run();
                GameplayFacadeTests.Run();
                HostDispatcherTests.Run();
                SessionLifecycleTests.Run(root + "-state");
                GamemodeSessionOrchestratorTests.Run(root + "-session");
                SessionActivationTests.Run(root + "-activation");
                ContentAdmissionTests.Run(root + "-content-admission");
                RuntimeSessionSelectionTests.Run();
                BuiltinWorldProviderTests.Run();
                WorldRuntimeReadinessTests.Run();
                WorldReadinessContractTests.Run(root + "-readiness-contract");
                DuplicateSelectionTests.Run();
                NativeWorldReflectionTests.Run();
                AssetNativeDrainTests.Run();
                OwnerNativeSceneLoadTests.Run();
                NativeWorkLifecycleTests.Run(Path.Combine(root, "native-work"));
                SdkLifecycleTests.Run(root);
                ScopedModContextTests.Run(root);
                ScopedAssetOwnershipTests.Run(root);
                AssetSpawnTransactionTests.Run();
                UiHotkeyOwnershipTests.Run();
                ContextBoundExtensionTests.Run();
                ScopedExtensionFacadeTests.Run();
                TestingKitTests.Run();
                SdkPublicApiBaselineTests.Run();
                PromptRegistryTests.Run();
                RobotDirectiveTests.Run();
                OverrideTests.Run();
                ConversationTests.Run();
                ConversationDirectorTests.Run();
                RobotPersonalityBindingSurfaceTests.Run();
                ObjectiveRunnerTests.Run();
                RobotTargetFactsTests.Run();
                SandboxProgramDirectorTests.Run();
                SandboxConfigTests.Run();
                WorldDiscoveryIdentityTests.Run();
                WorldResourceScopeTests.Run();
                WorldPlayerPlacementTests.Run();
                GeneratedArenaGeometryTests.Run();
                WorldProviderLoaderTests.Run();
                LegacyLaunchDiscoveryTests.RunAsync(FindRepoRoot()).GetAwaiter().GetResult();
                GamemodeConsumerRegressionTests.Run();
                WorldsActivationTests.RunAsync().GetAwaiter().GetResult();
                AcceptanceGamemodeFactoryTests.RunAsync().GetAwaiter().GetResult();
                LocalImportOperationTests.RunAsync().GetAwaiter().GetResult();
                WorldLaunchArmingTests.Run();
                RoboWorldImportPlanTests.Run();
                WorldsSafetyTests.Run();
                PendingOperationTests.Run();
                SceneCoordinatorTests.Run();
                NativeTransitionExecutorTests.Run();
                MultiplayerSceneAuthorityTests.Run();
                MenuSurfaceCensusTests.Run();
                GamemodeContractConformanceTests.Run();
                ManifestActivationSerializationTests.Run();
                LaunchStorageKeyTests.Run(FindRepoRoot());
                LaunchStagingTests.Run();
                AcceptanceIsolationTests.Run();
                AcceptanceIsolationStagingTests.Run();
                AcceptancePluginLifecycleTests.Run();
                WorldMarkerHierarchyTests.Run(FindRepoRoot());
                ProfileLaunchV4PolicyTests.Run();
                RuntimeStartupSelectionTests.Run();
                ManagerLaunchSelectionTests.Run();
                LegacyManagerSelectionTests.Run();
                LaunchTargetPreviewTests.Run();
                RuntimeLaunchCommandTests.Run(Path.Combine(root, "runtime-launch-command"));
                RuntimeLaunchPublicationTests.Run(Path.Combine(root, "runtime-launch-publication"));
                ModServiceRegistryTests.Run();
                SceneTransitionTrackerTests.Run();
                MainThreadDispatchQueueTests.Run();
                MainThreadGuardTests.Run();
                SafeEventTests.Run();
                ChronosTests.Run();
                CreatorContentTests.Run();
                CreatorSceneAdapterTests.Run();
                CreatorEventGraphRunnerTests.Run();
                CreatorWorkbenchLifecycleTests.Run();
                ShopTests.Run();
                GameCompatTests.Run();
                GameCompatActivationTests.Run();
                GameVersionLabelReaderTests.Run();
                UiKitCoreTests.Run();
                TopiaForgeStateFileTests.Run(root);
                UnityToolingFileIoTests.Run(root);
                UiKitSourceConventionTests.Run();
                ModConcurrencyConventionTests.Run();
                Console.WriteLine("All TopiaForge tests passed.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
            finally
            {
                TryDelete(root);
            }
        }

    }
}
