using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.CreatorContent;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static class CreatorContentTests
    {
        public static void Run()
        {
            TestSessionBoundsAndOwnedLifecycle();
            TestLifoAndFactoryFailureCleanup();
            TestProjectValidationAndLibraryPersistence();
            TestProjectLibraryBoundsAndRecovery();
            TestTypedGraphValidation();
            TestRouterPriorityAndToggle();
            TestConfigurableSingleHotkey();
            TestHostToggleBindingLifecycle();
            TestMutationGateFailsClosed();
            TestCreatorTestingFakeTargetsAndFailures();
            Console.WriteLine("CreatorContentTests passed.");
        }

        private static void TestSessionBoundsAndOwnedLifecycle()
        {
            AssertThrows<ArgumentOutOfRangeException>(
                () => new CreatorSessionOptions("too many", 257),
                "creator sessions must cap at 256 entities");

            using var context = new FakeModContext();
            context.Runtime.SetProviderVersion("test.provider", SemanticVersion.Parse("1.2.3"));
            using var service = new CreatorContentService("test.provider", context.Runtime, context.Logger);
            var factory = new TestFactory();
            var registrationResult = service.Register(new CreatorContentRegistrationRequest(
                "crate",
                "Test crate",
                "A safe test prop.",
                CreatorContentKind.Prop,
                CreatorTransformCapabilities.All,
                factory));
            var registration = RequireValue(registrationResult, "custom content registration should succeed");
            Assert(registration.Descriptor.ContentId == "test.provider:crate", "content id should be source qualified");
            Assert(registration.Descriptor.SourceVersion == "1.2.3", "catalog should report the runtime source version");
            Assert(service.Catalog.Sources.Any(source => source.SourceId == "robotopia.items"
                && source.State == CreatorCatalogSourceState.Unavailable),
                "unsafe built-in sources should report explicit unavailability");
            var stableCatalog = service.Catalog;
            Assert(ReferenceEquals(stableCatalog, service.Catalog),
                "unchanged catalog reads should reuse the immutable revision snapshot");

            var sessionResult = service.BeginSession(new CreatorSessionOptions("test run", 2));
            var session = RequireValue(sessionResult, "creator session should begin");
            var spawned = session.Spawn(new CreatorSpawnRequest(registration.Descriptor.ContentId, TransformState.Identity));
            var handle = RequireValue(spawned, "registered content should spawn an owned handle");
            Assert(handle.IsAlive, "registered content should spawn an owned handle");
            var duplicate = handle.Duplicate(new TransformState(
                new Vec3(1f, 0f, 0f),
                Quat.Identity,
                new Vec3(1f, 1f, 1f)));
            Assert(duplicate.Succeeded && factory.ActiveCount == 2, "duplicate should create a second source-owned instance");
            Assert(session.Spawn(new CreatorSpawnRequest(registration.Descriptor.ContentId, TransformState.Identity)).ErrorCode
                == ModErrorCode.RateLimited, "session instance limit should fail closed");

            registration.Dispose();
            var changedCatalog = service.Catalog;
            Assert(!ReferenceEquals(stableCatalog, changedCatalog) && changedCatalog.Revision > stableCatalog.Revision,
                "content removal should invalidate the cached catalog revision snapshot");
            Assert(factory.ActiveCount == 0 && !handle.IsAlive, "source unregister should destroy every produced instance");
            session.Dispose();
        }

        private static void TestLifoAndFactoryFailureCleanup()
        {
            using var context = new FakeModContext();
            using var service = new CreatorContentService("test.provider", context.Runtime, context.Logger);
            var sourceOrder = new List<string>();
            var sourceFactory = new TestFactory(sourceOrder);
            var registrationResult = service.Register(new CreatorContentRegistrationRequest(
                "ordered-source", "Ordered", string.Empty, CreatorContentKind.Prop,
                CreatorTransformCapabilities.All, sourceFactory));
            var registration = RequireValue(registrationResult, "ordered source should register");
            var sessionResult = service.BeginSession(new CreatorSessionOptions("source LIFO", 3));
            var session = RequireValue(sessionResult, "ordered source session should begin");
            for (var index = 0; index < 3; index++)
            {
                Assert(session.Spawn(new CreatorSpawnRequest(registration.Descriptor.ContentId, TransformState.Identity)).Succeeded,
                    "ordered source spawn should succeed");
            }
            registration.Dispose();
            Assert(sourceOrder.SequenceEqual(new[] { "creator-test-3", "creator-test-2", "creator-test-1" }),
                "source unload must dispose produced instances exactly once in LIFO order");

            var sessionOrder = new List<string>();
            var sessionFactory = new TestFactory(sessionOrder);
            var second = service.Register(new CreatorContentRegistrationRequest(
                "ordered-session", "Ordered session", string.Empty, CreatorContentKind.Prop,
                CreatorTransformCapabilities.All, sessionFactory));
            var secondRegistration = RequireValue(second, "second ordered source should register");
            var secondSessionResult = service.BeginSession(new CreatorSessionOptions("session LIFO", 3));
            var secondSession = RequireValue(secondSessionResult, "ordered session should begin");
            for (var index = 0; index < 3; index++)
            {
                Assert(secondSession.Spawn(new CreatorSpawnRequest(secondRegistration.Descriptor.ContentId, TransformState.Identity)).Succeeded,
                    "ordered session spawn should succeed");
            }
            secondSession.Dispose();
            Assert(sessionOrder.SequenceEqual(new[] { "creator-test-3", "creator-test-2", "creator-test-1" }),
                "session unload must dispose produced instances exactly once in LIFO order");

            var badSource = new ThrowingEntitySource();
            var bad = service.Register(new CreatorContentRegistrationRequest(
                "bad-source", "Bad source", string.Empty, CreatorContentKind.Other,
                CreatorTransformCapabilities.None, new FixedSourceFactory(badSource)));
            var badRegistration = RequireValue(bad, "bad source factory should register");
            var badSessionResult = service.BeginSession(new CreatorSessionOptions("factory failure", 1));
            var badSession = RequireValue(badSessionResult, "factory failure session should begin");
            Assert(badSession.Spawn(new CreatorSpawnRequest(badRegistration.Descriptor.ContentId, TransformState.Identity)).ErrorCode
                == ModErrorCode.External, "a source throwing while exposing Entity should become a stable failure");
            Assert(badSource.DisposeCount == 1, "a partially returned factory source must be cleaned exactly once");

            var throwingAliveSource = new ThrowingAliveSource();
            var throwingAlive = service.Register(new CreatorContentRegistrationRequest(
                "throwing-alive", "Throwing alive", string.Empty, CreatorContentKind.Other,
                CreatorTransformCapabilities.All, new FixedSourceFactory(throwingAliveSource)));
            var throwingAliveRegistration = RequireValue(throwingAlive, "throwing alive source should register");
            var throwingAliveSessionResult = service.BeginSession(new CreatorSessionOptions("alive probe failure", 1));
            var throwingAliveSession = RequireValue(throwingAliveSessionResult, "alive probe session should begin");
            var throwingAliveSpawn = throwingAliveSession.Spawn(new CreatorSpawnRequest(
                throwingAliveRegistration.Descriptor.ContentId,
                TransformState.Identity));
            var throwingAliveHandle = RequireValue(throwingAliveSpawn, "first alive probe should permit spawning");
            Assert(!throwingAliveHandle.IsAlive, "a faulting source alive probe should fail closed without escaping");
            Assert(throwingAliveHandle.SetTransform(TransformState.Identity).ErrorCode == ModErrorCode.External,
                "a faulting source alive probe during transform should become a stable external failure");
            throwingAliveSession.Dispose();
            Assert(throwingAliveSource.DisposeCount == 1, "faulting alive sources should still clean up exactly once");
        }

        private static void TestProjectValidationAndLibraryPersistence()
        {
            using var context = new FakeModContext();
            using var content = new CreatorContentService("test.provider", context.Runtime, context.Logger);
            var validator = new CreatorProjectValidator(() => content.Catalog);
            using var library = new CreatorProjectLibrary(context.Files, validator, context.Logger);
            var project = Project(
                new[]
                {
                    new CreatorGraphNode("start", CreatorGraphNodeKind.ProjectStart, Vec2.Zero),
                    new CreatorGraphNode("toast", CreatorGraphNodeKind.ShowToast, new Vec2(100f, 0f),
                        new Dictionary<string, string> { ["text"] = "Ready" })
                },
                new[] { new CreatorGraphEdge("start", "fired", "toast", "in") });

            Assert(library.Validate(project).IsValid, "bounded acyclic project should validate");
            var saved = library.SaveAsync(project).GetAwaiter().GetResult();
            Assert(saved.Succeeded, "valid project should persist");
            Assert(context.FileSystem.DataFiles.Contains("event-projects/demo.v1.json")
                && context.FileSystem.DataFiles.Contains("event-projects/index.v1.json"),
                "library should write a project document and authoritative index");
            var loaded = library.LoadAsync("demo").GetAwaiter().GetResult();
            Assert(loaded.TryGetValue(out var roundTrip) && roundTrip.Nodes.Count == 2,
                "project should round-trip through strict JSON storage");

            var cyclic = Project(
                new[]
                {
                    new CreatorGraphNode("a", CreatorGraphNodeKind.ManualTrigger, Vec2.Zero),
                    new CreatorGraphNode("b", CreatorGraphNodeKind.ShowToast, Vec2.Zero,
                        new Dictionary<string, string> { ["text"] = "loop" })
                },
                new[]
                {
                    new CreatorGraphEdge("a", "fired", "b", "in"),
                    new CreatorGraphEdge("b", "done", "a", "in")
                });
            Assert(!library.Validate(cyclic).IsValid, "v1 event graph cycles should be rejected");

            var excessiveRepeat = Project(
                new[]
                {
                    new CreatorGraphNode("repeat", CreatorGraphNodeKind.Repeat, Vec2.Zero,
                        new Dictionary<string, string> { ["value"] = "101" })
                },
                Array.Empty<CreatorGraphEdge>());
            Assert(!library.Validate(excessiveRepeat).IsValid,
                "repeat nodes must remain bounded to at most 100 iterations");

            var unresolved = new CreatorEventProject(
                CreatorProjectValidator.CurrentSchemaVersion,
                "unresolved",
                "Unresolved",
                string.Empty,
                CreatorProjectScope.Global,
                string.Empty,
                "Mall",
                DateTimeOffset.UtcNow,
                entities: new[]
                {
                    new CreatorProjectEntity(
                        "missing",
                        "Missing",
                        "missing.source:prop",
                        "1.0.0",
                        TransformState.Identity,
                        true)
                },
                nativeBindings: new[]
                {
                    new CreatorNativeBinding(
                        "door",
                        "Door",
                        "Mall",
                        "Door",
                        Vec3.Zero,
                        2f,
                        "robotopia.ugc-props")
                },
                origin: CreatorProjectOrigin.PlayerAtRun);
            var unresolvedValidation = library.Validate(unresolved);
            Assert(unresolvedValidation.IsValid
                && unresolvedValidation.Issues.Any(issue => issue.Code == "entity.content-unresolved"
                    && issue.Severity == CreatorProjectValidationSeverity.Warning),
                "unresolved content should remain loadable while surfacing a run-blocking availability warning");
            Assert(library.SaveAsync(unresolved).GetAwaiter().GetResult().Succeeded,
                "an unresolved-but-structurally-valid project should remain saveable");
            Assert(library.LoadAsync("unresolved").GetAwaiter().GetResult().Succeeded,
                "an unresolved project should remain loadable for repair");
            Assert(library.DeleteAsync("demo").GetAwaiter().GetResult().Value == true,
                "delete should remove the indexed project");
        }

        private static void TestProjectLibraryBoundsAndRecovery()
        {
            using (var context = new FakeModContext())
            {
                var summaries = Enumerable.Range(0, 256).Select(index => new CreatorProjectSummary(
                    "project" + index.ToString("000"),
                    "Project " + index,
                    CreatorProjectScope.Sandbox,
                    DateTimeOffset.UtcNow));
                context.FileSystem.SetDataText("event-projects/index.v1.json", CreatorProjectCodec.EncodeIndex(summaries));
                var validator = new CreatorProjectValidator(() => new CreatorCatalogSnapshot(
                    0, Array.Empty<CreatorContentDescriptor>(), Array.Empty<CreatorCatalogSourceStatus>()));
                using var library = new CreatorProjectLibrary(context.Files, validator, context.Logger);
                Assert(library.ListAsync().GetAwaiter().GetResult().Value?.Projects.Count == 256,
                    "a library at the project cap should load");
                Assert(library.SaveAsync(EmptyProject("overflow", "Overflow")).GetAwaiter().GetResult().ErrorCode
                    == ModErrorCode.RateLimited, "saving a 257th project must fail before writing its file");
                Assert(!context.FileSystem.DataFiles.Contains("event-projects/overflow.v1.json"),
                    "a rejected 257th project must not leave an orphan document");
            }

            using (var context = new FakeModContext())
            {
                var summaries = Enumerable.Range(0, 257).Select(index => new CreatorProjectSummary(
                    "project" + index.ToString("000"),
                    "Project " + index,
                    CreatorProjectScope.Sandbox,
                    DateTimeOffset.UtcNow));
                context.FileSystem.SetDataText("event-projects/index.v1.json", CreatorProjectCodec.EncodeIndex(summaries));
                var validator = new CreatorProjectValidator(() => new CreatorCatalogSnapshot(
                    0, Array.Empty<CreatorContentDescriptor>(), Array.Empty<CreatorCatalogSourceStatus>()));
                using var library = new CreatorProjectLibrary(context.Files, validator, context.Logger);
                Assert(library.ListAsync().GetAwaiter().GetResult().ErrorCode == ModErrorCode.External,
                    "an oversized stored index must fail closed");
            }

            using (var context = new FakeModContext())
            {
                var validator = new CreatorProjectValidator(() => new CreatorCatalogSnapshot(
                    0, Array.Empty<CreatorContentDescriptor>(), Array.Empty<CreatorCatalogSourceStatus>()));
                using var library = new CreatorProjectLibrary(context.Files, validator, context.Logger);
                Assert(library.SaveAsync(EmptyProject("recover", "Before")).GetAwaiter().GetResult().Succeeded,
                    "recovery fixture should save initially");
                context.Files.FailNextWritePath = "event-projects/index.v1.json";
                context.Files.FailNextWriteErrorCode = ModErrorCode.Io;
                Assert(library.SaveAsync(EmptyProject("recover", "After")).GetAwaiter().GetResult().ErrorCode
                    == ModErrorCode.Io, "configured index write failure should surface");
                var listed = library.ListAsync().GetAwaiter().GetResult();
                Assert(listed.TryGetValue(out var snapshot)
                    && snapshot.Projects.Single().DisplayName == "Before",
                    "failed index update must restore the prior in-memory summary");
                var recoveredDocument = library.LoadAsync("recover").GetAwaiter().GetResult();
                Assert(recoveredDocument.TryGetValue(out var recoveredProject)
                    && recoveredProject.DisplayName == "Before",
                    "failed index update must restore the previous project document as well as its summary");

                context.Files.FailNextWritePath = "event-projects/index.v1.json";
                context.Files.FailNextWriteErrorCode = ModErrorCode.Io;
                Assert(library.SaveAsync(EmptyProject("new-failure", "New failure")).GetAwaiter().GetResult().ErrorCode
                    == ModErrorCode.Io, "new-project index failure should surface");
                Assert(library.ListAsync().GetAwaiter().GetResult().Value?.Projects.Count == 1,
                    "failed new-project index update must remove only the tentative summary");
                Assert(!context.FileSystem.DataFiles.Contains("event-projects/new-failure.v1.json"),
                    "failed new-project index update must remove its unindexed project document");

                library.Dispose();
                using var restarted = new CreatorProjectLibrary(context.Files, validator, context.Logger);
                var afterRestart = restarted.LoadAsync("recover").GetAwaiter().GetResult();
                Assert(afterRestart.TryGetValue(out var restartedProject)
                    && restartedProject.DisplayName == "Before",
                    "a restarted library must observe the old indexed metadata and old project document together");
            }
        }

        private static void TestTypedGraphValidation()
        {
            using var context = new FakeModContext();
            using var content = new CreatorContentService("test.provider", context.Runtime, context.Logger);
            var validator = new CreatorProjectValidator(() => content.Catalog);
            var binding = new CreatorNativeBinding(
                "native", "Native", "Mall", "Door", Vec3.Zero, 2f, "robotopia.ugc-props");
            var entity = new CreatorProjectEntity(
                "crate", "Crate", "missing.source:crate", "1.0.0", TransformState.Identity, false);

            var validNative = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("radius", CreatorGraphNodeKind.PlayerEnteredRadius, Vec2.Zero,
                        new Dictionary<string, string> { ["nativeBindingId"] = "native", ["radius"] = "4" }),
                    new CreatorGraphNode("state", CreatorGraphNodeKind.RobotObjectiveState, Vec2.Zero,
                        new Dictionary<string, string> { ["nativeBindingId"] = "native", ["value"] = "Arrived" }),
                    new CreatorGraphNode("move", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["nativeBindingId"] = "native", ["value"] = "1,2,3" }),
                    new CreatorGraphNode("interact", CreatorGraphNodeKind.InteractionTrigger, Vec2.Zero,
                        new Dictionary<string, string>
                        {
                            ["nativeBindingId"] = "native",
                            ["prompt"] = "OPEN",
                            ["radius"] = "4"
                        }),
                    new CreatorGraphNode("configure", CreatorGraphNodeKind.ConfigureRobot, Vec2.Zero,
                        new Dictionary<string, string>
                        {
                            ["entityId"] = "crate",
                            ["name"] = "Guide",
                            ["tint"] = "Cyan",
                            ["scale"] = "1.25",
                            ["brain"] = "Autonomous"
                        }),
                    new CreatorGraphNode("entity-move", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "crate", ["value"] = "4,5,6" }),
                    new CreatorGraphNode("decision", CreatorGraphNodeKind.ConversationDecision, Vec2.Zero)
                },
                Array.Empty<CreatorGraphEdge>(),
                new[] { entity },
                new[] { binding });
            Assert(validator.Validate(validNative).IsValid,
                "targeted triggers/actions should accept one valid native binding and documented decision wildcard");

            var caseDistinctEntity = new CreatorProjectEntity(
                "Crate", "Upper crate", "missing.source:crate", "1.0.0", TransformState.Identity, false);
            var caseSensitive = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("node", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "crate", ["value"] = "1,2,3" }),
                    new CreatorGraphNode("Node", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "Crate", ["value"] = "4,5,6" })
                }, Array.Empty<CreatorGraphEdge>(), new[] { entity, caseDistinctEntity }, new[] { binding });
            Assert(validator.Validate(caseSensitive).IsValid,
                "persisted project ids should be case-sensitive exactly like runner lookups");

            var wrongCaseReference = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("move", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "CRATE", ["value"] = "1,2,3" })
                }, Array.Empty<CreatorGraphEdge>(), new[] { entity }, new[] { binding });
            Assert(validator.Validate(wrongCaseReference).Issues.Any(issue => issue.Code == "node.entity"),
                "validator references must reject casing that the runtime cannot resolve");

            var ambiguous = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("move", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "crate", ["nativeBindingId"] = "native" })
                }, Array.Empty<CreatorGraphEdge>(), new[] { entity }, new[] { binding });
            Assert(!validator.Validate(ambiguous).IsValid,
                "targeted actions must reject ambiguous entity and native binding references");

            var missing = GlobalProject(
                new[] { new CreatorGraphNode("move", CreatorGraphNodeKind.SetTransform, Vec2.Zero) },
                Array.Empty<CreatorGraphEdge>(), new[] { entity }, new[] { binding });
            Assert(!validator.Validate(missing).IsValid, "targeted actions must reject missing target references");

            var invalidParameters = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("native-move", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["nativeBindingId"] = "native", ["value"] = "1,2,3,4" }),
                    new CreatorGraphNode("entity-move", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "crate" }),
                    new CreatorGraphNode("interact", CreatorGraphNodeKind.InteractionTrigger, Vec2.Zero,
                        new Dictionary<string, string>
                        {
                            ["entityId"] = "crate",
                            ["prompt"] = " ",
                            ["radius"] = "11"
                        }),
                    new CreatorGraphNode("configure", CreatorGraphNodeKind.ConfigureRobot, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "crate" })
                }, Array.Empty<CreatorGraphEdge>(), new[] { entity }, new[] { binding });
            var invalidParameterIssues = validator.Validate(invalidParameters).Issues;
            Assert(invalidParameterIssues.Count(issue => issue.Code == "node.transform") == 2
                && invalidParameterIssues.Any(issue => issue.Code == "node.prompt")
                && invalidParameterIssues.Any(issue => issue.Code == "node.radius")
                && invalidParameterIssues.Any(issue => issue.Code == "node.robot-configuration"),
                "native and project-entity transforms, interactions, and robot configuration must validate their bounded runtime parameters");

            var legacyBrain = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("configure", CreatorGraphNodeKind.ConfigureRobot, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "crate", ["value"] = "Dormant" })
                }, Array.Empty<CreatorGraphEdge>(), new[] { entity }, new[] { binding });
            Assert(validator.Validate(legacyBrain).IsValid,
                "configure-robot should retain the documented legacy value fallback for brain mode");

            var sandboxNative = new CreatorEventProject(
                CreatorProjectValidator.CurrentSchemaVersion,
                "sandbox-native",
                "Sandbox native",
                string.Empty,
                CreatorProjectScope.Sandbox,
                "sandbox-world",
                "Mall",
                DateTimeOffset.UtcNow,
                nativeBindings: new[] { binding },
                nodes: new[]
                {
                    new CreatorGraphNode("move", CreatorGraphNodeKind.SetTransform, Vec2.Zero,
                        new Dictionary<string, string> { ["nativeBindingId"] = "native", ["value"] = "1,2,3" })
                });
            Assert(validator.Validate(sandboxNative).IsValid,
                "sandbox projects should retain exact-scene native recipes for per-session confirmation");

            var robotBinding = new CreatorNativeBinding(
                "robot", "Native robot", "Mall", "Robot", Vec3.Zero, 2f, "robotkit.native-robot");
            var unsupportedRobotInteraction = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("interact", CreatorGraphNodeKind.InteractionTrigger, Vec2.Zero,
                        new Dictionary<string, string>
                        {
                            ["nativeBindingId"] = "robot",
                            ["prompt"] = "TALK",
                            ["radius"] = "3"
                        })
                }, Array.Empty<CreatorGraphEdge>(), Array.Empty<CreatorProjectEntity>(), new[] { robotBinding });
            Assert(validator.Validate(unsupportedRobotInteraction).Issues.Any(issue =>
                    issue.Code == "node.native-binding-capability"),
                "native RobotKit targets without IEntity must reject interaction registration");

            var nativeSpawn = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("spawn", CreatorGraphNodeKind.SpawnContent, Vec2.Zero,
                        new Dictionary<string, string> { ["nativeBindingId"] = "native" })
                }, Array.Empty<CreatorGraphEdge>(), new[] { entity }, new[] { binding });
            Assert(!validator.Validate(nativeSpawn).IsValid, "spawn must remain project-entity-only");

            var wrongPort = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("toast", CreatorGraphNodeKind.ShowToast, Vec2.Zero,
                        new Dictionary<string, string> { ["text"] = "hello" }),
                    new CreatorGraphNode("next", CreatorGraphNodeKind.ShowToast, Vec2.Zero,
                        new Dictionary<string, string> { ["text"] = "next" })
                },
                new[] { new CreatorGraphEdge("toast", "each", "next", "in") },
                new[] { entity }, new[] { binding });
            Assert(!validator.Validate(wrongPort).IsValid,
                "action nodes must reject output ports belonging to other node kinds");

            var incomingTrigger = Project(
                new[]
                {
                    new CreatorGraphNode("start", CreatorGraphNodeKind.ProjectStart, Vec2.Zero),
                    new CreatorGraphNode("manual", CreatorGraphNodeKind.ManualTrigger, Vec2.Zero)
                },
                new[] { new CreatorGraphEdge("start", "fired", "manual", "in") });
            Assert(validator.Validate(incomingTrigger).Issues.Any(issue => issue.Code == "edge.trigger-target"),
                "trigger nodes must reject incoming graph edges because they can only fire from runtime events");

            var blankPersonality = new CreatorEventProject(
                CreatorProjectValidator.CurrentSchemaVersion,
                "blank-personality",
                "Blank personality",
                string.Empty,
                CreatorProjectScope.Sandbox,
                "sandbox",
                string.Empty,
                DateTimeOffset.UtcNow,
                entities: new[] { entity },
                personas: new[]
                {
                    new CreatorPersona("blank", "Blank", "   ", string.Empty)
                },
                nodes: new[]
                {
                    new CreatorGraphNode("personality", CreatorGraphNodeKind.SetRobotPersonality, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "crate", ["personaId"] = "blank" })
                });
            Assert(validator.Validate(blankPersonality).Issues.Any(issue => issue.Code == "node.persona-system-frame"),
                "SetRobotPersonality must reject a referenced persona with an empty SystemFrame");

            var badTrigger = GlobalProject(
                new[]
                {
                    new CreatorGraphNode("radius", CreatorGraphNodeKind.PlayerEnteredRadius, Vec2.Zero,
                        new Dictionary<string, string> { ["entityId"] = "crate", ["radius"] = "0" }),
                    new CreatorGraphNode("decision", CreatorGraphNodeKind.ConversationDecision, Vec2.Zero,
                        new Dictionary<string, string> { ["value"] = "invented" })
                }, Array.Empty<CreatorGraphEdge>(), new[] { entity }, new[] { binding });
            var badIssues = validator.Validate(badTrigger).Issues;
            Assert(badIssues.Any(issue => issue.Code == "node.radius")
                && badIssues.Any(issue => issue.Code == "node.conversation-decision"),
                "trigger radii and conversation decisions must be finite bounded closed values");
        }

        private static void TestRouterPriorityAndToggle()
        {
            using var context = new FakeModContext();
            using var router = new CreatorToolHostRouter(
                "test.provider",
                context.Input,
                context.Scenes,
                context.Logger);
            Assert(router is ICreatorToolHostService, "router implementation should publish the primary tool-host service contract");
            var low = new TestHost();
            var high = new TestHost();
            Assert(router.RegisterHost(new CreatorToolHostRegistrationRequest("low", "Low", 0, low)).Succeeded,
                "low priority host should register");
            Assert(router.RegisterHost(new CreatorToolHostRegistrationRequest("high", "High", 10, high)).Succeeded,
                "high priority host should register");
            Assert(router.Toggle().Value == true && high.OpenCount == 1 && low.OpenCount == 0,
                "router should choose the highest priority available host");
            Assert(router.Toggle().Value == true && high.CloseCount == 1
                && high.LastCloseReason == CreatorToolCloseReason.UserToggle,
                "second toggle should hide the active host with user-toggle semantics");
        }

        private static void TestMutationGateFailsClosed()
        {
            var service = new UnavailableMutationSafetyService();
            Assert(service.Status.State == CreatorMutationSafetyState.Unavailable
                && !service.Status.PersistenceIsolationAvailable,
                "missing isolation bridge should be visible without blocking browsing");
            Assert(service.Acquire(new CreatorMutationLeaseRequest("global edits", false)).ErrorCode == ModErrorCode.Conflict,
                "mutation should require one-time acknowledgement first");
            Assert(service.Acquire(new CreatorMutationLeaseRequest("global edits", true)).ErrorCode == ModErrorCode.Unavailable,
                "acknowledgement must not bypass missing persistence isolation");
        }

        private static void TestConfigurableSingleHotkey()
        {
            var config = new CreatorContentConfig { ToggleKey = "F9" };
            config.Normalize();
            Assert(config.ToggleKey == "F9", "provider config should preserve a genuine custom toggle key");
            config.ToggleKey = "not a key!";
            config.Normalize();
            Assert(config.ToggleKey == "F5", "invalid provider toggle keys should fail back to F5");

            using var context = new FakeModContext();
            using var router = new CreatorToolHostRouter(
                "test.provider",
                context.Input,
                context.Scenes,
                context.Logger);
            Assert(router.AttachInput("F9").Succeeded, "router should register its configured physical action");
            Assert(context.Input.ActiveActionCount == 1
                && context.Input.GetAction("creator-tools.toggle").Bindings.Single().Control == "F9",
                "router should own exactly one action using the provider key");
            Assert(router.RegisterHost(new CreatorToolHostRegistrationRequest(
                    "legacy", "Legacy host", 0, new TestHost(), toggleBinding: "F8")).Succeeded,
                "a host should be able to migrate one genuine custom toggle binding");
            Assert(context.Input.ActiveActionCount == 1
                && context.Input.GetAction("creator-tools.toggle").Bindings.Select(binding => binding.Control)
                    .OrderBy(value => value, StringComparer.Ordinal).SequenceEqual(new[] { "F8", "F9" }),
                "legacy custom binding must be merged into the one provider-owned action");
            Assert(router.AttachInput("F5").Value == false && context.Input.ActiveActionCount == 1,
                "reattaching must not create a duplicate physical action");
        }

        private static void TestHostToggleBindingLifecycle()
        {
            using var context = new FakeModContext();
            using var router = new CreatorToolHostRouter(
                "test.provider",
                context.Input,
                context.Scenes,
                context.Logger);
            Assert(router.AttachInput("F5").Succeeded, "router should attach its provider-owned action");

            var fallback = new TestHost();
            Assert(router.RegisterHost(new CreatorToolHostRegistrationRequest(
                    "fallback", "Fallback", 0, fallback)).Succeeded,
                "fallback host should register");
            var custom = router.RegisterHost(new CreatorToolHostRegistrationRequest(
                "custom", "Custom", 10, new TestHost(), toggleBinding: "F8"));
            var customRegistration = RequireValue(custom, "a live host's custom binding should register");
            Assert(context.Input.GetAction("creator-tools.toggle").Bindings.Any(binding => binding.Control == "F8"),
                "a live host's custom binding should join the provider action");

            context.Input.SetValue("creator-tools.toggle", 1f);
            customRegistration.Dispose();
            Assert(context.Input.GetAction("creator-tools.toggle").Bindings.Select(binding => binding.Control)
                    .SequenceEqual(new[] { "F5" }),
                "disposing a host must remove its host-only custom binding immediately");
            router.Tick();
            Assert(fallback.OpenCount == 0,
                "an already-sampled removed custom binding must not toggle another eligible host");
            context.Input.SetValue("creator-tools.toggle", 0f);
            router.Tick();
            context.Input.FinishFrame();

            var sharedOne = router.RegisterHost(new CreatorToolHostRegistrationRequest(
                "shared-one", "Shared one", 1, new TestHost(), toggleBinding: "F9"));
            var sharedTwo = router.RegisterHost(new CreatorToolHostRegistrationRequest(
                "shared-two", "Shared two", 2, new TestHost(), toggleBinding: "f9"));
            var sharedOneRegistration = RequireValue(sharedOne,
                "the first shared custom binding should register");
            var sharedTwoRegistration = RequireValue(sharedTwo,
                "the second shared custom binding should register");
            Assert(context.Input.GetAction("creator-tools.toggle").Bindings.Count(binding =>
                    string.Equals(binding.Control, "F9", StringComparison.OrdinalIgnoreCase)) == 1,
                "shared custom bindings should be reference-counted without duplicates");
            sharedOneRegistration.Dispose();
            Assert(context.Input.GetAction("creator-tools.toggle").Bindings.Any(binding =>
                    string.Equals(binding.Control, "F9", StringComparison.OrdinalIgnoreCase)),
                "a shared custom binding should remain while one owner is registered");
            sharedTwoRegistration.Dispose();
            Assert(!context.Input.GetAction("creator-tools.toggle").Bindings.Any(binding =>
                    string.Equals(binding.Control, "F9", StringComparison.OrdinalIgnoreCase)),
                "the final owner disposal should remove a shared custom binding");

            for (var index = 0; index < 12; index++)
            {
                var changing = router.RegisterHost(new CreatorToolHostRegistrationRequest(
                    "changing-" + index,
                    "Changing " + index,
                    1,
                    new TestHost(),
                    toggleBinding: "F" + (index + 10)));
                var changingRegistration = RequireValue(changing,
                    "replacing a custom binding must not consume the eight-active-binding budget");
                changingRegistration.Dispose();
            }
            Assert(context.Input.ActiveActionCount == 1
                && context.Input.GetAction("creator-tools.toggle").Bindings.Select(binding => binding.Control)
                    .SequenceEqual(new[] { "F5" }),
                "repeated host rebinding should leave one provider action and no leaked bindings");
        }

        private static void TestCreatorTestingFakeTargetsAndFailures()
        {
            using var context = new FakeModContext();
            using var service = new FakeCreatorContentService(context.Lifetime);
            var firstEntity = new FakeEntity("native-1", "Door Alpha", Vec3.Zero);
            var first = new FakeCreatorSceneTarget(
                "target-1",
                "Door Alpha",
                "test.props",
                firstEntity,
                CreatorContentKind.Prop,
                CreatorSceneTargetCapabilities.Transform | CreatorSceneTargetCapabilities.TemporaryVisibility,
                hidden: false);
            var second = new FakeCreatorSceneTarget(
                "target-2",
                "Door Beta",
                "other.props",
                new FakeEntity("native-2", "Door Beta", new Vec3(2f, 0f, 0f)),
                CreatorContentKind.Prop);
            service.AddSceneTarget(first);
            service.AddSceneTarget(second);

            var factory = new TestFactory();
            service.RegisterErrorCode = ModErrorCode.Conflict;
            Assert(service.Register(new CreatorContentRegistrationRequest(
                    "rejected", "Rejected", string.Empty, CreatorContentKind.Prop,
                    CreatorTransformCapabilities.All, factory)).ErrorCode == ModErrorCode.Conflict
                && service.ActiveRegistrationCount == 0,
                "fake should inject registration failures without retaining resources");
            service.RegisterErrorCode = ModErrorCode.None;
            var registered = service.Register(new CreatorContentRegistrationRequest(
                "crate", "Crate", string.Empty, CreatorContentKind.Prop,
                CreatorTransformCapabilities.All, factory));
            var registration = RequireValue(registered, "fake creator registration should succeed");
            service.BeginSessionErrorCode = ModErrorCode.RateLimited;
            Assert(service.BeginSession(new CreatorSessionOptions("rejected session", 2)).ErrorCode == ModErrorCode.RateLimited
                && service.ActiveSessionCount == 0,
                "fake should inject session failures without retaining resources");
            service.BeginSessionErrorCode = ModErrorCode.None;
            var begun = service.BeginSession(new CreatorSessionOptions("fake target test", 2));
            var session = RequireValue(begun, "fake creator session should begin");
            Assert(service.ActiveRegistrationCount == 1 && service.ActiveSessionCount == 1
                && service.ActiveSceneTargetCount == 2, "fake should expose active resource counts");

            service.FactoryErrorCode = ModErrorCode.Unavailable;
            Assert(session.Spawn(new CreatorSpawnRequest(registration.Descriptor.ContentId, TransformState.Identity)).ErrorCode
                == ModErrorCode.Unavailable, "fake should inject factory failures without creating a spawn");
            Assert(service.ActiveSpawnCount == 0 && factory.ActiveCount == 0,
                "injected factory failure must not leak a source instance");
            service.FactoryErrorCode = ModErrorCode.None;
            Assert(session.Spawn(new CreatorSpawnRequest(registration.Descriptor.ContentId, TransformState.Identity)).Succeeded
                && service.ActiveSpawnCount == 1, "fake should count successful spawns");

            var filtered = session.QuerySceneTargets(new CreatorSceneQuery(
                nameContains: "Door", maximumResults: 8, adapterId: "test.props"));
            Assert(filtered.TryGetValue(out var matches) && matches.Count == 1
                && matches[0].AdapterId == "test.props", "fake query must enforce the persisted adapter-id filter");
            service.QueryTargetsErrorCode = ModErrorCode.Io;
            Assert(session.QuerySceneTargets(new CreatorSceneQuery()).ErrorCode == ModErrorCode.Io,
                "fake should inject query failures");
            service.QueryTargetsErrorCode = ModErrorCode.None;

            var editResult = session.BeginTemporaryEdit(first);
            Assert(editResult.TryGetValue(out var editContract)
                && editContract is FakeCreatorTemporaryEdit
                && service.ActiveEditCount == 1,
                "fake should create and count an exclusive temporary edit");
            var edit = (FakeCreatorTemporaryEdit)editContract!;
            Assert(session.BeginTemporaryEdit(first).ErrorCode == ModErrorCode.Conflict,
                "fake target edits must be exclusive");

            var editedTransform = new TransformState(
                new Vec3(5f, 0f, 0f),
                new Quat(0f, 0f, 1f, 0f),
                new Vec3(2f, 2f, 2f));
            service.EditTransformErrorCode = ModErrorCode.Io;
            Assert(edit.SetTransform(editedTransform).ErrorCode == ModErrorCode.Io
                && firstEntity.Position == Vec3.Zero,
                "fake should inject transform failures without mutating the target");
            service.EditTransformErrorCode = ModErrorCode.None;
            service.EditVisibilityErrorCode = ModErrorCode.Unavailable;
            Assert(edit.SetTemporarilyHidden(true).ErrorCode == ModErrorCode.Unavailable && !first.Hidden,
                "fake should inject visibility failures without mutating the target");
            service.EditVisibilityErrorCode = ModErrorCode.None;
            Assert(edit.SetTransform(editedTransform).Succeeded
                && edit.SetTemporarilyHidden(true).Succeeded,
                "fake edit should apply configured transform and visibility capabilities");
            firstEntity.Position = new Vec3(9f, 0f, 0f);
            service.RestoreEditErrorCode = ModErrorCode.Io;
            Assert(edit.Restore().ErrorCode == ModErrorCode.Io && service.ActiveEditCount == 1,
                "restore failure injection should preserve the live lease for retry");
            service.RestoreEditErrorCode = ModErrorCode.None;
            Assert(edit.Restore().Value == true && edit.LastRestoreHadConflict,
                "retry should restore non-conflicting properties and report an external change");
            Assert(firstEntity.Position == new Vec3(9f, 0f, 0f)
                && firstEntity.Rotation == Quat.Identity
                && firstEntity.Scale == new Vec3(1f, 1f, 1f)
                && !first.Hidden
                && service.ActiveEditCount == 0,
                "per-property restore must preserve external position while restoring rotation, scale, and visibility");

            service.BeginEditErrorCode = ModErrorCode.Unavailable;
            Assert(session.BeginTemporaryEdit(first).ErrorCode == ModErrorCode.Unavailable,
                "fake should inject edit-acquisition failures");
            service.BeginEditErrorCode = ModErrorCode.None;
            service.ResolveTargetErrorCode = ModErrorCode.External;
            Assert(session.ResolveSceneTarget(firstEntity).ErrorCode == ModErrorCode.External,
                "fake should inject scene-target resolution failures");
            service.ResolveTargetErrorCode = ModErrorCode.None;

            var autoRestore = session.BeginTemporaryEdit(first);
            var autoEdit = RequireValue(autoRestore, "second fake edit should begin after restoration");
            Assert(autoEdit.SetTransform(editedTransform).Succeeded, "second fake edit should apply");
            session.Dispose();
            Assert(service.ActiveSessionCount == 0 && service.ActiveSpawnCount == 0
                && service.ActiveEditCount == 0 && firstEntity.Position == new Vec3(9f, 0f, 0f),
                "session disposal must restore edits and clean spawns without taking ownership of borrowed targets");
        }

        private static CreatorEventProject Project(
            IEnumerable<CreatorGraphNode> nodes,
            IEnumerable<CreatorGraphEdge> edges) => new CreatorEventProject(
                CreatorProjectValidator.CurrentSchemaVersion,
                "demo",
                "Demo",
                string.Empty,
                CreatorProjectScope.Sandbox,
                "sandbox",
                string.Empty,
                DateTimeOffset.UtcNow,
                nodes: nodes,
                edges: edges);

        private static CreatorEventProject EmptyProject(string id, string displayName) => new CreatorEventProject(
            CreatorProjectValidator.CurrentSchemaVersion,
            id,
            displayName,
            string.Empty,
            CreatorProjectScope.Sandbox,
            "sandbox",
            string.Empty,
            DateTimeOffset.UtcNow);

        private static CreatorEventProject GlobalProject(
            IEnumerable<CreatorGraphNode> nodes,
            IEnumerable<CreatorGraphEdge> edges,
            IEnumerable<CreatorProjectEntity> entities,
            IEnumerable<CreatorNativeBinding> bindings) => new CreatorEventProject(
                CreatorProjectValidator.CurrentSchemaVersion,
                "global-test",
                "Global test",
                string.Empty,
                CreatorProjectScope.Global,
                string.Empty,
                "Mall",
                DateTimeOffset.UtcNow,
                entities: entities,
                nativeBindings: bindings,
                nodes: nodes,
                edges: edges);

        private static void Assert(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static T RequireValue<T>(OperationResult<T> result, string message) where T : notnull
        {
            if (!result.TryGetValue(out var value)) throw new InvalidOperationException(message);
            return value;
        }

        private static void AssertThrows<T>(Action action, string message) where T : Exception
        {
            try
            {
                action();
            }
            catch (T)
            {
                return;
            }
            throw new InvalidOperationException(message);
        }

        private sealed class TestFactory : ICreatorContentFactory
        {
            private readonly IList<string>? disposalOrder;
            private int nextId;

            public TestFactory(IList<string>? disposalOrder = null)
            {
                this.disposalOrder = disposalOrder;
            }

            public int ActiveCount { get; private set; }
            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
            {
                var id = "creator-test-" + (++nextId);
                ActiveCount++;
                return OperationResult<ICreatorSourceInstance>.Success(
                    new TestSource(id, transform, () =>
                    {
                        ActiveCount--;
                        disposalOrder?.Add(id);
                    }));
            }
        }

        private sealed class FixedSourceFactory : ICreatorContentFactory
        {
            private readonly ICreatorSourceInstance source;

            public FixedSourceFactory(ICreatorSourceInstance source)
            {
                this.source = source;
            }

            public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform) =>
                OperationResult<ICreatorSourceInstance>.Success(source);
        }

        private sealed class ThrowingEntitySource : ICreatorSourceInstance
        {
            public int DisposeCount { get; private set; }
            public IEntity Entity => throw new InvalidOperationException("Factory entity failure.");
            public bool IsAlive => DisposeCount == 0;
            public bool TryGetTransform(out TransformState transform)
            {
                transform = TransformState.Identity;
                return false;
            }
            public OperationResult<TransformState> SetTransform(TransformState transform) =>
                OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "Unavailable.");
            public void Dispose() => DisposeCount++;
        }

        private sealed class ThrowingAliveSource : ICreatorSourceInstance
        {
            private readonly FakeEntity entity = new FakeEntity("throwing-alive", "Throwing alive", Vec3.Zero);
            private int aliveChecks;

            public int DisposeCount { get; private set; }
            public IEntity Entity => entity;
            public bool IsAlive => ++aliveChecks == 1
                ? true
                : throw new InvalidOperationException("Alive probe failure.");
            public bool TryGetTransform(out TransformState transform)
            {
                transform = TransformState.Identity;
                return true;
            }
            public OperationResult<TransformState> SetTransform(TransformState transform) =>
                OperationResult<TransformState>.Success(transform);
            public void Dispose()
            {
                DisposeCount++;
                entity.Destroy();
            }
        }

        private sealed class TestSource : ICreatorSourceInstance
        {
            private Action? release;
            private TransformState transform;
            private readonly FakeEntity entity;

            public TestSource(string id, TransformState transform, Action release)
            {
                this.transform = transform;
                this.release = release;
                entity = new FakeEntity(id, "Test", transform.Position)
                {
                    Rotation = transform.Rotation,
                    Scale = transform.Scale
                };
            }

            public IEntity Entity => entity;
            public bool IsAlive => release != null;
            public bool TryGetTransform(out TransformState value)
            {
                value = transform;
                return IsAlive;
            }
            public OperationResult<TransformState> SetTransform(TransformState value)
            {
                if (!IsAlive) return OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "Disposed.");
                transform = value;
                entity.Position = value.Position;
                entity.Rotation = value.Rotation;
                entity.Scale = value.Scale;
                return OperationResult<TransformState>.Success(value);
            }
            public void Dispose()
            {
                var callback = release;
                release = null;
                entity.Destroy();
                callback?.Invoke();
            }
        }

        private sealed class TestHost : ICreatorToolHost
        {
            public bool IsOpen { get; private set; }
            public int OpenCount { get; private set; }
            public int CloseCount { get; private set; }
            public CreatorToolCloseReason LastCloseReason { get; private set; }
            public bool CanOpen(CreatorToolOpenContext context) => true;
            public OperationResult<bool> Open(CreatorToolOpenContext context)
            {
                IsOpen = true;
                OpenCount++;
                return OperationResult<bool>.Success(true);
            }
            public OperationResult<bool> Close(CreatorToolCloseReason reason)
            {
                IsOpen = false;
                CloseCount++;
                LastCloseReason = reason;
                return OperationResult<bool>.Success(true);
            }
        }
    }
}
