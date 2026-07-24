using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class V1LaunchCoverageTests
    {
        private static readonly Assembly[] SafeAssemblies =
        {
            typeof(IModContext).Assembly,
            typeof(IRobotAgentService).Assembly,
            typeof(IWorldGamemodeService).Assembly,
            typeof(ITimeControlService).Assembly,
            typeof(ICreatorContentService).Assembly,
            typeof(IMultiplayerSession).Assembly,
            typeof(IPromptOverrideRegistry).Assembly,
            typeof(IUgcLiveSyncService).Assembly,
            typeof(FakeModContext).Assembly
        };

        private static readonly string[] PublicAuthoringProjects =
        {
            "src/TopiaForge.Mods.Abstractions/TopiaForge.Mods.Abstractions.csproj",
            "src/TopiaForge.Mods.Chronos/TopiaForge.Mods.Chronos.csproj",
            "src/TopiaForge.Mods.CreatorContent/TopiaForge.Mods.CreatorContent.csproj",
            "src/TopiaForge.Mods.Multiplayer/TopiaForge.Mods.Multiplayer.csproj",
            "src/TopiaForge.Mods.Prompts/TopiaForge.Mods.Prompts.csproj",
            "src/TopiaForge.Mods.RobotKit/TopiaForge.Mods.RobotKit.csproj",
            "src/TopiaForge.Mods.Ugc/TopiaForge.Mods.Ugc.csproj",
            "src/TopiaForge.Mods.Worlds/TopiaForge.Mods.Worlds.csproj",
            "src/TopiaForge.Mods.Testing/TopiaForge.Mods.Testing.csproj",
            "src/TopiaForge.Mods.Interop.Unity/TopiaForge.Mods.Interop.Unity.csproj"
        };

        public static void Run()
        {
            var root = Program.FindRepoRoot();
            var matrixPath = Path.Combine(root, "docs", "capability-matrix.json");
            var acceptancePath = Path.Combine(root, "tests", "live-game-acceptance.json");
            var acceptanceModDirectory = Path.Combine(root, "tests", "TopiaForge.SdkAcceptanceMod");
            var acceptanceManifestPath = Path.Combine(acceptanceModDirectory, "topiaforge.mod.json");
            var harnessPath = Path.Combine(
                root, "apps", "topiaforge_cli", "lib", "src", "live_acceptance_runner.dart");
            var acceptanceCommandPath = Path.Combine(
                root, "apps", "topiaforge_cli", "bin", "topiaforge_acceptance_commands.dart");
            var workflowPath = Path.Combine(root, ".github", "workflows", "game-sdk-acceptance.yml");
            var solution = File.ReadAllText(Path.Combine(root, "TopiaForge.slnx"));
            var docsPublisher = File.ReadAllText(
                Path.Combine(root, "website", "scripts", "prepare-docs.mjs"));
            var docsCatalog = File.ReadAllText(
                Path.Combine(root, "website", "scripts", "docs", "catalog.mjs"));
            Assert(docsPublisher.Contains("./docs/catalog.mjs", StringComparison.Ordinal),
                "the documentation publisher must consume the reviewed page catalog");
            using var matrix = JsonDocument.Parse(File.ReadAllText(matrixPath));
            using var acceptance = JsonDocument.Parse(File.ReadAllText(acceptancePath));

            Assert(matrix.RootElement.GetProperty("schemaVersion").GetInt32() == 1,
                "capability matrix schema version must be 1");
            var acceptanceCases = acceptance.RootElement.GetProperty("cases").EnumerateArray().ToArray();
            var acceptanceIds = acceptanceCases
                .Select(value => RequiredText(value, "id"))
                .ToHashSet(StringComparer.Ordinal);
            Assert(acceptanceIds.Count == acceptanceCases.Length,
                "live acceptance case ids must be unique");
            var requiredCycles = acceptance.RootElement.GetProperty("requiredLifecycleCycles").GetInt32();
            Assert(requiredCycles >= 10, "live acceptance must require at least ten lifecycle cycles");
            var acceptanceSource = string.Join(
                "\n",
                Directory.EnumerateFiles(acceptanceModDirectory, "*.cs", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .Select(File.ReadAllText));
            ValidateAcceptanceProbeMappings(acceptanceCases, acceptanceSource);
            ValidateProviderAcceptanceProbe(
                acceptanceCases,
                acceptanceSource,
                acceptanceManifestPath);
            ValidateMultiplayerAcceptanceProbe(
                acceptanceCases,
                acceptanceSource,
                acceptanceManifestPath);
            ValidateLifecycleAcceptanceProbe(
                acceptanceCases,
                acceptanceSource,
                requiredCycles);

            var harness = File.ReadAllText(harnessPath);
            var acceptanceCommand = File.ReadAllText(acceptanceCommandPath);
            Assert(harness.Contains("options.requiredCases.isEmpty", StringComparison.Ordinal)
                   && harness.Contains("spec.caseIds", StringComparison.Ordinal)
                   && acceptanceCommand.Contains("'--all'", StringComparison.Ordinal),
                "live acceptance must require the full canonical matrix by default");
            var workflow = File.ReadAllText(workflowPath);
            Assert(workflow.Contains("default: both", StringComparison.Ordinal)
                   && workflow.Contains("default: full", StringComparison.Ordinal)
                   && workflow.Contains("runs-on: [self-hosted, Windows", StringComparison.Ordinal)
                   && workflow.Contains("runs-on: [self-hosted, Linux", StringComparison.Ordinal)
                   && workflow.Contains("dart run bin/topiaforge.dart @arguments", StringComparison.Ordinal),
                "live acceptance workflow must default to the full Windows and Linux/Proton gate");
            var rows = matrix.RootElement.GetProperty("rows").EnumerateArray().ToArray();
            Assert(rows.Length == 7, "the V1 matrix must contain exactly seven modder-goal rows");
            var rowIds = new HashSet<string>(StringComparer.Ordinal);
            var validatedSamples = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var id = RequiredText(row, "id");
                Assert(rowIds.Add(id), "duplicate capability row: " + id);
                _ = RequiredText(row, "goal");

                ValidateTypes(row, "apiTypes", requireTestingAssembly: false);
                ValidateTypes(row, "fakeTypes", requireTestingAssembly: true);
                ValidateSampleProjects(root, row, solution, validatedSamples);

                var guide = RequiredText(row, "guide");
                Assert(guide.EndsWith(".md", StringComparison.OrdinalIgnoreCase),
                    id + " guide must be Markdown");
                Assert(File.Exists(Path.Combine(root, guide.Replace('/', Path.DirectorySeparatorChar))),
                    id + " guide does not exist: " + guide);
                Assert(docsCatalog.Contains("page('" + guide + "'", StringComparison.Ordinal),
                    id + " guide is not published by the Starlight source pipeline: " + guide);

                var cases = RequiredArray(row, "acceptanceCases");
                foreach (var value in cases)
                {
                    var caseId = value.GetString() ?? string.Empty;
                    Assert(acceptanceIds.Contains(caseId), id + " references unknown acceptance case " + caseId);
                }
            }

            Assert(rowIds.SetEquals(new[]
                {
                    "utility",
                    "input-ui",
                    "gameplay",
                    "interactions-content",
                    "worlds-modes",
                    "robots-story",
                    "mod-integration"
                }), "capability matrix rows do not match the approved V1 goals");
            ValidateDocumentationContracts(root);
            ValidateLaunchGateDisclosures(root);
            Console.WriteLine("All V1 launch coverage tests passed.");
        }

        private static void ValidateAcceptanceProbeMappings(
            IReadOnlyList<JsonElement> cases,
            string source)
        {
            foreach (var acceptanceCase in cases)
            {
                var caseId = RequiredText(acceptanceCase, "id");
                var methodName = RequiredText(acceptanceCase, "probeMethod");
                var body = ExtractMethodBody(source, methodName);
                Assert(
                    Regex.IsMatch(
                        body,
                        "\\bPass\\s*\\(\\s*\"" + Regex.Escape(caseId) + "\"",
                        RegexOptions.CultureInvariant),
                    "acceptance probe " + methodName
                    + " does not emit an exact PASS marker for canonical case " + caseId);
            }
        }

        private static void ValidateProviderAcceptanceProbe(
            IReadOnlyList<JsonElement> cases,
            string source,
            string manifestPath)
        {
            const string caseId = "integration.provider-scope";
            const string ugcProviderId = "io.github.furroxide.topiaforge.ugc.livesync";
            const string missingProviderId = "dev.topiaforge.sdk-acceptance.missing-provider";
            var acceptanceCase = cases.Single(value =>
                string.Equals(RequiredText(value, "id"), caseId, StringComparison.Ordinal));
            var behaviors = RequiredArray(acceptanceCase, "behaviors")
                .Select(value => value.GetString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);
            Assert(behaviors.SetEquals(new[]
                {
                    "required-provider-singletons",
                    "optional-present-provider",
                    "optional-absent-nonblocking",
                    "singleton-conflict",
                    "multiple-cardinality",
                    "deterministic-selection",
                    "early-release"
                }), caseId + " must declare every provider behavior exercised by the live probe");

            var body = ExtractMethodBody(source, RequiredText(acceptanceCase, "probeMethod"));
            AssertContainsAll(body, caseId, new[]
            {
                "Context.Extensions.GetAll<ITimeControlService>()",
                "Context.Extensions.GetAll<ICreatorContentService>()",
                "Context.Extensions.GetAll<IUgcLiveSyncService>()",
                "Context.Extensions.GetAll<IMissingOptionalProvider>()",
                "ModErrorCode.Conflict",
                "ExtensionCardinality.Multiple",
                "ReferenceEquals(providers[0], firstProvider)",
                "firstMultipleRegistration.Dispose()",
                "Context.TryGetExtension<IAcceptanceProbeProvider>"
            });

            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            var required = manifest.RootElement.GetProperty("dependencies");
            var optional = manifest.RootElement.GetProperty("optionalDependencies");
            Assert(!required.TryGetProperty(ugcProviderId, out _),
                "the live UGC provider must be optional for the provider-scope probe");
            Assert(optional.TryGetProperty(ugcProviderId, out _),
                "the provider-scope probe must declare an installed optional provider");
            Assert(optional.TryGetProperty(missingProviderId, out _),
                "the provider-scope probe must declare a deliberately absent optional provider");
            Assert(source.Contains(
                    "private const string MissingOptionalProviderId = \"" + missingProviderId + "\";",
                    StringComparison.Ordinal),
                "the absent optional provider assertion must use the manifest's exact id");
        }

        private static void ValidateLifecycleAcceptanceProbe(
            IReadOnlyList<JsonElement> cases,
            string source,
            int requiredCycles)
        {
            const string caseId = "lifecycle.ten-cycles";
            var acceptanceCase = cases.Single(value =>
                string.Equals(RequiredText(value, "id"), caseId, StringComparison.Ordinal));
            var minimumCycles = acceptanceCase.GetProperty("minimumCycles").GetInt32();
            Assert(minimumCycles == requiredCycles && minimumCycles >= 10,
                caseId + " minimumCycles must match requiredLifecycleCycles and be at least ten");

            var constant = Regex.Match(
                source,
                @"\bprivate\s+const\s+int\s+RequiredLifecycleCycles\s*=\s*(\d+)\s*;",
                RegexOptions.CultureInvariant);
            Assert(constant.Success && int.Parse(constant.Groups[1].Value) == requiredCycles,
                "the acceptance mod lifecycle constant must match the canonical cycle count");

            var families = RequiredArray(acceptanceCase, "resourceFamilies")
                .Select(value => value.GetString() ?? string.Empty)
                .ToHashSet(StringComparer.Ordinal);
            Assert(families.SetEquals(new[]
                {
                    "explicit-lifetime",
                    "event-subscriptions",
                    "scheduler-operations",
                    "input-actions",
                    "player-control-leases",
                    "asset-bundle-prefab-entity",
                    "interactions",
                    "audio-playback",
                    "ui-surfaces-modals",
                    "localization",
                    "commands",
                    "extensions",
                    "chronos-leases",
                    "prompt-overrides",
                    "robot-targets",
                    "creator-sessions",
                    "ugc-asset-overrides",
                    "world-registrations"
                }), caseId + " resourceFamilies must exactly describe the automatable live cycle coverage");

            var body = ExtractMethodBody(source, RequiredText(acceptanceCase, "probeMethod"));
            AssertContainsAll(body, caseId, new[]
            {
                "cycle < RequiredLifecycleCycles",
                "ProbeExplicitLifetime(cycle)",
                "ProbePlayerControl(cycle)",
                "ProbeAudio(cycle)",
                "ProbeUiModal(cycle)",
                "ProbeChronos(cycle)",
                "ProbeCancelledDelayAsync(cycle)",
                "SubscribeCycleEvents(counters, resources)",
                "ScheduleCycleCallbacks(counters, resources)",
                "RegisterCycleInput(resources)",
                "RegisterCycleUiSurface(resources, cycle)",
                "RegisterCycleLocalization(resources, cycle)",
                "RegisterCycleCommand(resources, cycle)",
                "RegisterCycleExtension(resources, cycle)",
                "RegisterCyclePrompt(resources, cycle)",
                "RegisterCycleRobotTarget(resources, player, cycle)",
                "RegisterCycleCreatorSession(resources, cycle)",
                "RegisterCycleWorlds(resources, out world, out gamemode, out menu)",
                "LoadCycleAssetsAsync(resources, player, cycle)",
                "WaitForCycleCallbacksAsync(counters, cycle)",
                "DisposeCycleResources(resources)",
                "AssertCycleReleasedAsync(",
                "VerifyLifecycleIdsReleased()",
                "completedCycles++"
            });

            ValidateProbeMethod(source, "ProbeExplicitLifetime", new[]
            {
                "Context.Lifetime.Track(resource)",
                "Context.Lifetime.Defer(",
                "tracked.Dispose()",
                "deferred.Dispose()"
            });
            ValidateProbeMethod(source, "SubscribeCycleEvents", new[]
            {
                "Context.Events.SubscribeUpdate",
                "Context.Events.SubscribeFixedUpdate",
                "Context.Events.SubscribeLateUpdate",
                "Context.Events.SubscribeSceneLoaded",
                "Context.Scenes.SubscribeCheckpointChanged"
            });
            ValidateProbeMethod(source, "ScheduleCycleCallbacks", new[]
            {
                "Context.Scheduler.NextFrame",
                "Context.Scheduler.After",
                "Context.Scheduler.Every"
            });
            ValidateProbeMethod(source, "ProbeCancelledDelayAsync", new[]
            {
                "Context.Scheduler.DelayAsync",
                "cancellation.Cancel()",
                "ModErrorCode.Cancelled"
            });
            ValidateProbeMethod(source, "RegisterCycleInput", new[]
            {
                "Context.Input.RegisterAction",
                "ModErrorCode.Conflict"
            });
            ValidateProbeMethod(source, "ProbePlayerControl", new[]
            {
                "Context.LocalPlayer.AcquireControl",
                "first.IsActive",
                "second.IsActive"
            });
            ValidateProbeMethod(source, "LoadCycleAssetsAsync", new[]
            {
                "Context.Assets.LoadBundleAsync",
                "Context.Assets.LoadPrefabAsync",
                "Context.Assets.Spawn",
                "Context.Interactions.Register",
                "RegisterCycleUgcOverride(resources, prefab)"
            });
            ValidateProbeMethod(source, "ProbeAudio", new[]
            {
                "Context.Audio.Play",
                "playback.Stop()",
                "!playback.IsPlaying"
            });
            ValidateProbeMethod(source, "ProbeUiModal", new[]
            {
                "Context.Ui.ShowModal",
                "modal.Close()",
                "!modal.IsOpen"
            });
            ValidateProbeMethod(source, "RegisterCycleUiSurface", new[]
            {
                "Context.Ui.CreateSurface",
                "surface.Hide()",
                "surface.Show()"
            });
            ValidateProbeMethod(source, "RegisterCycleLocalization", new[]
            {
                "Context.Localization.Register",
                "Context.Localization.Get"
            });
            ValidateProbeMethod(source, "RegisterCycleCommand", new[]
            {
                "Context.Commands.Register",
                "Context.Commands.TryExecute",
                "ModErrorCode.Conflict"
            });
            ValidateProbeMethod(source, "RegisterCycleExtension", new[]
            {
                "Context.Extensions.Register<ILifecycleProbeProvider>",
                "Context.TryGetExtension<ILifecycleProbeProvider>",
                "registration.IsActive"
            });
            ValidateProbeMethod(source, "ProbeChronos", new[]
            {
                "service.Slow(",
                "lease.Release()",
                "!lease.IsActive"
            });
            ValidateProbeMethod(source, "RegisterCyclePrompt", new[]
            {
                "service.Register(new PromptOverrideRequest",
                "service.TryGetEffectiveOverride",
                "handle.IsDisposed"
            });
            ValidateProbeMethod(source, "RegisterCycleRobotTarget", new[]
            {
                "service.RegisterTarget(",
                "service.TryResolveTarget",
                "registration.IsActive"
            });
            ValidateProbeMethod(source, "RegisterCycleUgcOverride", new[]
            {
                "service.RegisterAssetOverride",
                "lease.IsActive",
                "ContainsUgcOverride"
            });
            ValidateProbeMethod(source, "RegisterCycleWorlds", new[]
            {
                "service.RegisterWorld",
                "service.RegisterGamemode",
                "service.RegisterMenuEntry",
                "world.IsActive"
            });
            ValidateProbeMethod(source, "AssertCycleReleasedAsync", new[]
            {
                "!extension.IsActive",
                "prompt.IsDisposed",
                "!target.IsActive",
                "!world.IsActive",
                "!assets.Bundle.IsAlive",
                "!assets.Interaction.IsActive",
                "!assets.UgcOverride.IsActive",
                "Context.Scheduler.DelayAsync",
                "event or scheduled callback fired after early release"
            });
            ValidateProbeMethod(source, "DisposeCycleResources", new[]
            {
                "while (resources.Count > 0)",
                "resources.Pop().Dispose()",
                "first ??= exception"
            });
        }

        private static void ValidateMultiplayerAcceptanceProbe(
            IReadOnlyList<JsonElement> cases,
            string source,
            string manifestPath)
        {
            const string caseId = "integration.multiplayer-loopback";
            const string providerId = "io.github.furroxide.topiaforge.multiplayer";
            var acceptanceCase = cases.Single(value =>
                string.Equals(RequiredText(value, "id"), caseId, StringComparison.Ordinal));
            var body = ExtractMethodBody(source, RequiredText(acceptanceCase, "probeMethod"));
            AssertContainsAll(body, caseId, new[]
            {
                "Context.TryGetMultiplayer(out var multiplayer)",
                "MultiplayerSessionState.Ready",
                "MultiplayerProcessKind.Interactive",
                "MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server",
                "session.HasPresentation",
                "participant.IsConnected",
                "participant.IsLocal",
                "BindMultiplayer(multiplayer)",
                "SubmitProbeLoopbackAsync",
                "multiplayerProbeState.Value.Value",
                "multiplayerPresentedValue"
            });

            AssertContainsAll(source, caseId, new[]
            {
                "[MultiplayerContract(",
                "[ReplicatedState(",
                "[MultiplayerCommand(",
                "[PresentationEvent("
            });

            using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            Assert(manifest.RootElement.GetProperty("dependencies").TryGetProperty(providerId, out _),
                "the live loopback probe must declare the multiplayer provider dependency");
            var multiplayer = manifest.RootElement.GetProperty("multiplayer");
            Assert(multiplayer.GetProperty("mode").GetString() == "session"
                   && multiplayer.GetProperty("presence").GetString() == "required"
                   && multiplayer.GetProperty("protocol").GetProperty("version").GetString() == "1.0.0",
                "the live loopback probe must declare its required session protocol");

            var projectPath = Path.Combine(
                Path.GetDirectoryName(manifestPath)!,
                "TopiaForge.SdkAcceptanceMod.csproj");
            var project = File.ReadAllText(projectPath);
            Assert(project.Contains("TopiaForge.Mods.Multiplayer.Generators", StringComparison.Ordinal)
                   && project.Contains("OutputItemType=\"Analyzer\"", StringComparison.Ordinal)
                   && project.Contains("ReferenceOutputAssembly=\"false\"", StringComparison.Ordinal),
                "the live loopback probe must compile through the multiplayer source generator");

            var lockPath = Path.Combine(
                Path.GetDirectoryName(manifestPath)!,
                ModMultiplayerMetadata.ContractLockFileName);
            using var contractLock = JsonDocument.Parse(File.ReadAllText(lockPath));
            var contracts = contractLock.RootElement.GetProperty("contracts").EnumerateArray().ToArray();
            Assert(contracts.Length == 1
                   && contracts[0].GetProperty("id").GetString() == "dev.topiaforge.sdk-acceptance.loopback",
                "the live loopback probe must commit its generated contract lock");
        }

        private static void ValidateProbeMethod(
            string source,
            string methodName,
            IReadOnlyList<string> requiredMarkers)
        {
            AssertContainsAll(ExtractMethodBody(source, methodName), methodName, requiredMarkers);
        }

        private static void AssertContainsAll(
            string text,
            string scope,
            IEnumerable<string> requiredMarkers)
        {
            foreach (var marker in requiredMarkers)
            {
                Assert(text.Contains(marker, StringComparison.Ordinal),
                    scope + " is missing behavior marker: " + marker);
            }
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            var declaration = new Regex(
                @"\b(?:private|protected|internal|public)\s+"
                + @"(?:(?:static|async|override|virtual|sealed|new)\s+)*"
                + @"[A-Za-z_][A-Za-z0-9_<>,?.\[\]]*\s+"
                + Regex.Escape(methodName)
                + @"\s*\(",
                RegexOptions.CultureInvariant);
            var matches = declaration.Matches(source);
            Assert(matches.Count == 1,
                "acceptance probe method " + methodName + " must have exactly one declaration");
            var openingBrace = source.IndexOf('{', matches[0].Index + matches[0].Length);
            Assert(openingBrace >= 0,
                "acceptance probe method " + methodName + " has no body");

            var depth = 0;
            var inString = false;
            var inVerbatimString = false;
            var inCharacter = false;
            var inLineComment = false;
            var inBlockComment = false;
            for (var index = openingBrace; index < source.Length; index++)
            {
                var current = source[index];
                var next = index + 1 < source.Length ? source[index + 1] : '\0';
                if (inLineComment)
                {
                    if (current == '\n') inLineComment = false;
                    continue;
                }

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        index++;
                    }

                    continue;
                }

                if (inString)
                {
                    if (inVerbatimString && current == '"' && next == '"')
                    {
                        index++;
                        continue;
                    }

                    if ((!inVerbatimString && current == '\\') && next != '\0')
                    {
                        index++;
                        continue;
                    }

                    if (current == '"')
                    {
                        inString = false;
                        inVerbatimString = false;
                    }

                    continue;
                }

                if (inCharacter)
                {
                    if (current == '\\' && next != '\0')
                    {
                        index++;
                        continue;
                    }

                    if (current == '\'') inCharacter = false;
                    continue;
                }

                if (current == '/' && next == '/')
                {
                    inLineComment = true;
                    index++;
                    continue;
                }

                if (current == '/' && next == '*')
                {
                    inBlockComment = true;
                    index++;
                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    inVerbatimString = index > 0 && source[index - 1] == '@';
                    continue;
                }

                if (current == '\'')
                {
                    inCharacter = true;
                    continue;
                }

                if (current == '{')
                {
                    depth++;
                }
                else if (current == '}' && --depth == 0)
                {
                    return source.Substring(openingBrace + 1, index - openingBrace - 1);
                }
            }

            throw new InvalidOperationException(
                "acceptance probe method " + methodName + " has an unterminated body");
        }

        private static void ValidateTypes(JsonElement row, string propertyName, bool requireTestingAssembly)
        {
            var id = RequiredText(row, "id");
            foreach (var value in RequiredArray(row, propertyName))
            {
                var qualifiedName = value.GetString() ?? string.Empty;
                var separator = qualifiedName.LastIndexOf(',');
                Assert(separator > 0 && separator < qualifiedName.Length - 1,
                    id + " has an invalid assembly-qualified type name: " + qualifiedName);
                var typeName = qualifiedName.Substring(0, separator).Trim();
                var assemblyName = qualifiedName.Substring(separator + 1).Trim();
                var assembly = SafeAssemblies.SingleOrDefault(candidate =>
                    string.Equals(candidate.GetName().Name, assemblyName, StringComparison.Ordinal));
                Assert(assembly != null, id + " references a non-safe or unknown contract assembly: " + assemblyName);
                var type = assembly!.GetType(typeName, throwOnError: false, ignoreCase: false);
                Assert(type?.IsPublic == true, id + " references a missing public type: " + qualifiedName);
                Assert(!requireTestingAssembly || string.Equals(
                        assemblyName,
                        "TopiaForge.Mods.Testing",
                        StringComparison.Ordinal),
                    id + " fake must come from TopiaForge.Mods.Testing: " + qualifiedName);
            }
        }

        private static void ValidateSampleProjects(
            string root,
            JsonElement row,
            string solution,
            ISet<string> validatedSamples)
        {
            var id = RequiredText(row, "id");
            foreach (var value in RequiredArray(row, "sampleProjects"))
            {
                var relativePath = value.GetString() ?? string.Empty;
                Assert(relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase),
                    id + " sample must be a buildable .csproj project: " + relativePath);
                var projectPath = Path.Combine(
                    root,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert(File.Exists(projectPath),
                    id + " sample project does not exist: " + relativePath);
                Assert(solution.Contains("Project Path=\"" + relativePath + "\"", StringComparison.Ordinal),
                    id + " sample is not compiled by the canonical solution: " + relativePath);
                if (!validatedSamples.Add(relativePath))
                {
                    continue;
                }

                var project = XDocument.Load(projectPath);
                foreach (var reference in project.Descendants()
                             .Where(element => element.Name.LocalName == "Reference"
                                 || element.Name.LocalName == "ProjectReference"))
                {
                    var include = (string?)reference.Attribute("Include") ?? string.Empty;
                    Assert(!ContainsNativeDependency(include),
                        relativePath + " references a native or loader-owned dependency: " + include);
                }

                var projectDirectory = Path.GetDirectoryName(projectPath)!;
                var sources = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                    .Where(path => !IsBuildOutput(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToArray();
                Assert(sources.Length > 0, relativePath + " has no compiled C# source");
                var source = string.Join("\n", sources.Select(File.ReadAllText));
                foreach (var forbidden in new[]
                         {
                             "UnityEngine",
                             "GameCode",
                             "Harmony",
                             "System.Reflection",
                             "TopiaForge.Mods.UnityUi",
                             "TopiaForge.Mods.Interop.Unity",
                             "ITopiaForgeMod"
                         })
                {
                    Assert(!source.Contains(forbidden, StringComparison.Ordinal),
                        relativePath + " contains forbidden safe-sample API " + forbidden);
                }

                Assert(source.Contains("TopiaForgeMod", StringComparison.Ordinal),
                    relativePath + " must exercise the public V1 authoring base class");
                Assert(!Regex.IsMatch(
                        source,
                        @"\bobject(?:\?|\s+[A-Za-z_][A-Za-z0-9_]*)",
                        RegexOptions.CultureInvariant),
                    relativePath + " must not traffic in raw native object handles");
            }
        }

        private static bool ContainsNativeDependency(string include)
        {
            return include.Contains("Unity", StringComparison.OrdinalIgnoreCase)
                || include.Contains("GameCode", StringComparison.OrdinalIgnoreCase)
                || include.Contains("Harmony", StringComparison.OrdinalIgnoreCase)
                || include.Contains("Interop.Unity", StringComparison.OrdinalIgnoreCase);
        }

        private static void ValidateDocumentationContracts(string root)
        {
            var docfxPath = Path.Combine(root, "website", "docfx.json");
            var docfx = File.ReadAllText(docfxPath);
            foreach (var relativePath in PublicAuthoringProjects)
            {
                var projectPath = Path.Combine(
                    root,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Assert(File.Exists(projectPath), "public authoring project is missing: " + relativePath);
                var project = XDocument.Load(projectPath);
                Assert(HasPropertyValue(project, "GenerateDocumentationFile", "true"),
                    relativePath + " must emit IntelliSense XML documentation");
                Assert(HasPropertyValue(project, "ProduceReferenceAssembly", "true"),
                    relativePath + " must produce a reference assembly");
                var warnings = project.Descendants()
                    .Where(element => element.Name.LocalName == "WarningsAsErrors")
                    .Select(element => element.Value)
                    .ToArray();
                Assert(warnings.Any(value => value.Contains("CS1591", StringComparison.Ordinal)
                    && value.Contains("CS1574", StringComparison.Ordinal)),
                    relativePath + " must fail on missing or broken public XML documentation");
                Assert(docfx.Contains("\"" + relativePath + "\"", StringComparison.Ordinal),
                    relativePath + " is missing from the DocFX public reference build");
            }

            Assert(!docfx.Contains("TopiaForge.Mods.UnityUi.csproj", StringComparison.Ordinal),
                "the loader-owned UnityUi renderer must not be published as an authoring contract");

            using var package = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "website", "package.json")));
            var scripts = package.RootElement.GetProperty("scripts");
            var checkScript = scripts.GetProperty("check").GetString() ?? string.Empty;
            Assert(checkScript.Contains("npm test", StringComparison.Ordinal)
                && checkScript.Contains("docs:prepare", StringComparison.Ordinal)
                && checkScript.Contains("astro build", StringComparison.Ordinal)
                && checkScript.Contains("build:reference", StringComparison.Ordinal)
                && checkScript.Contains("check:built-links", StringComparison.Ordinal),
                "the documentation check must build Starlight and DocFX and validate merged links");

            var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
            var docsStart = workflow.IndexOf("\n  docs:\n", StringComparison.Ordinal);
            var templatesStart = workflow.IndexOf("\n  template-matrix:\n", StringComparison.Ordinal);
            Assert(docsStart >= 0 && templatesStart > docsStart,
                "CI must define a dedicated documentation job before the template matrix");
            var docsJob = workflow.Substring(docsStart, templatesStart - docsStart);
            Assert(docsJob.Contains("needs: [csharp-tests, template-matrix]", StringComparison.Ordinal),
                "documentation publishing must wait for C# and all compiled release scaffolds");
            Assert(docsJob.Contains("Restore Robotopia managed refs", StringComparison.Ordinal)
                && docsJob.Contains(
                    "dist/api/csharp/api/TopiaForge.Mods.Interop.Unity.IUnityInteropService.html",
                    StringComparison.Ordinal),
                "documentation CI must resolve native metadata and assert the unstable interop reference");
        }

        private static bool HasPropertyValue(XDocument project, string propertyName, string expected)
        {
            return project.Descendants()
                .Where(element => element.Name.LocalName == propertyName)
                .Any(element => string.Equals(element.Value.Trim(), expected, StringComparison.OrdinalIgnoreCase));
        }

        private static void ValidateLaunchGateDisclosures(string root)
        {
            var trust = File.ReadAllText(Path.Combine(root, "docs", "PrivacyAndCapabilities.md"));
            Assert(trust.Contains("trusted in-process C# code", StringComparison.Ordinal)
                && trust.Contains("do not sandbox, mediate, or grant", StringComparison.Ordinal),
                "public capability docs must state that mods are trusted full-process code, not sandboxed");

            var live = File.ReadAllText(Path.Combine(root, "docs", "LiveGameAcceptance.md"));
            Assert(live.Contains("## External-only launch gate", StringComparison.Ordinal)
                && live.Contains("they cannot mark a live", StringComparison.Ordinal)
                && live.Contains("exact frozen candidate package hashes", StringComparison.Ordinal),
                "live acceptance docs must distinguish external evidence from offline/static checks");
        }

        private static bool IsBuildOutput(string path)
        {
            var separator = Path.DirectorySeparatorChar;
            return path.Contains(separator + "bin" + separator, StringComparison.Ordinal)
                || path.Contains(separator + "obj" + separator, StringComparison.Ordinal);
        }

        private static JsonElement[] RequiredArray(JsonElement row, string propertyName)
        {
            var values = row.GetProperty(propertyName).EnumerateArray().ToArray();
            Assert(values.Length > 0, RequiredText(row, "id") + " must declare " + propertyName);
            return values;
        }

        private static string RequiredText(JsonElement value, string propertyName)
        {
            var text = value.GetProperty(propertyName).GetString() ?? string.Empty;
            Assert(!string.IsNullOrWhiteSpace(text), propertyName + " must not be empty");
            return text;
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }
    }
}
