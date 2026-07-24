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

            if (args.Length == 1 && string.Equals(args[0], "--sdk-lifecycle", StringComparison.Ordinal))
            {
                var lifecycleRoot = Path.Combine(
                    Path.GetTempPath(),
                    "TopiaForgeSdkLifecycleTests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(lifecycleRoot);
                try
                {
                    SdkLifecycleTests.Run(lifecycleRoot);
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
                SceneTransitionTrackerTests.Run();
                return 0;
            }

            if (args.Length == 1 && string.Equals(args[0], "--manifest-v5", StringComparison.Ordinal))
            {
                var manifestRoot = Path.Combine(
                    Path.GetTempPath(),
                    "TopiaForgeManifestV5Tests-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(manifestRoot);
                try
                {
                    TestStrictManifestExtensions();
                    ManifestV5Tests.Run(manifestRoot);
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

            var root = Path.Combine(Path.GetTempPath(), "TopiaForgeModManagerTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);

            try
            {
                TestInstallSuccess(root);
                TestLegacyPackageExtensionRejected(root);
                TestUpdatePreservesDisabledState(root);
                TestDevToolInstallsDisabledAndUpdatePreservesState(root);
                TestAppliedRestartRequirementsClear();
                RuntimePersistenceSecurityTests.Run(root);
                StartupJournalTests.Run(root);
                StartupRecoveryPolicyTests.Run();
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
                ManifestV5Tests.Run(root);
                MultiplayerContractLockBoundaryTests.Run(root);
                MultiplayerAdmissionTests.Run();
                FirstPartyManifestTests.Run();
                FirstPartyConfigTests.Run();
                ModAssemblyResolutionCatalogTests.Run(root);
                ProfileLaunchConfigurationTests.Run();
                TestUgcExportSchemaContract();
                TestPendingRuntimeManifestContracts();
                WorldLaunchSettingsTests.Run();
                ZombiesConfigTests.Run();
                ZombiesControllerTests.Run();
                UgcNoOpLaunchRequestTests.Run();
                UgcLiveSyncTests.Run();
                SdkSurfaceTests.Run();
                RuntimeInfoTests.Run();
                ModuleContractSurfaceTests.Run();
                V1LaunchCoverageTests.Run();
                GameplayFacadeTests.Run();
                SdkLifecycleTests.Run(root);
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
                WorldAutoLoadRouterTests.Run();
                WorldsSafetyTests.Run();
                SceneCoordinatorTests.Run();
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
                GameVersionLabelReaderTests.Run();
                UiKitCoreTests.Run();
                TopiaForgeStateFileTests.Run(root);
                UnityToolingFileIoTests.Run(root);
                UiKitSourceConventionTests.Run();
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
