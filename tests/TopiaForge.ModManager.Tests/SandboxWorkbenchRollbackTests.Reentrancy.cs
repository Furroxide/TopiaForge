using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TopiaForge.CreatorTools;
using TopiaForge.CreatorTools.Shared;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class SandboxWorkbenchRollbackTests
    {
        private static void ReentrantIsolationAcquireCannotAuthorizeChangedSession()
        {
            foreach (var transition in new[] { "end", "reopen", "dispose" })
            {
                var safety = new ReentrantSafety();
                using var fixture = new Fixture(CreatorProjectScope.Global, mutationSafety: safety);
                fixture.Workbench.Open();
                fixture.Click("enable-mutations");
                safety.DuringAcquire = () =>
                {
                    if (transition == "dispose") fixture.Workbench.Dispose();
                    else
                    {
                        fixture.Workbench.EndSession();
                        if (transition == "reopen") fixture.Workbench.Open();
                    }
                };
                fixture.Context.Ui.Modals.Single().Confirm();
                Assert(safety.ActiveLeases == 0 && safety.DisposeCalls == 1,
                    "reentrant Acquire must release its returned lease after session " + transition);
                Assert(!fixture.Workbench.SpawnRobot().Succeeded && fixture.Robots.Agents.ActiveAgents.Count == 0,
                    "retired acquisition cannot grant mutation to the replacement or disposed workbench");
            }
            var replacementSafety = new ReentrantSafety();
            using var replacementFixture = new Fixture(CreatorProjectScope.Global, mutationSafety: replacementSafety);
            replacementFixture.Workbench.Open();
            var nested = false;
            var ended = false;
            replacementSafety.DuringAcquire = () =>
            {
                if (nested) return;
                nested = true;
                replacementFixture.Click("enable-mutations");
                replacementFixture.Context.Ui.Modals.Single().Confirm();
            };
            replacementSafety.DuringDispose = () =>
            {
                if (ended) return;
                ended = true;
                replacementFixture.Workbench.EndSession();
                replacementFixture.Workbench.Open();
            };
            replacementFixture.Click("enable-mutations");
            replacementFixture.Context.Ui.Modals.Single().Confirm();
            Assert(replacementSafety.ActiveLeases == 0 && replacementSafety.DisposeCalls == 2
                && replacementFixture.Workbench.IsSessionActive && !replacementFixture.Workbench.SpawnRobot().Succeeded,
                "disposing a replaced isolation lease must detach ownership and recheck the session before attachment");
        }

        private static void SameSessionStaleConfirmationCannotReplaceCurrent()
        {
            using var fixture = new Fixture();
            fixture.Workbench.Open();
            fixture.Click("end-session");
            var old = fixture.Context.Ui.Modals.Single();
            var late = CaptureQueuedCallback(old);
            old.Close();
            fixture.Click("end-session");
            var current = fixture.Context.Ui.Modals.Single();
            late(true);
            Assert(fixture.Workbench.IsSessionActive && current.IsOpen
                && ReferenceEquals(CurrentConfirmation(fixture), current),
                "same-session retired modal must not end the session or clear its replacement");
            current.Confirm();
            Assert(!fixture.Workbench.IsSessionActive, "current same-session confirmation remains effective");
        }

        private static void StaleProjectDeletionCannotDeleteOrClearModal()
        {
            foreach (var replaceSession in new[] { false, true })
            {
                var library = ProjectLibrary(withBinding: false);
                using var fixture = new Fixture(library: library);
                OpenProject(fixture);
                fixture.Click("delete-project");
                var old = fixture.Context.Ui.Modals.Single();
                var late = CaptureQueuedCallback(old);
                if (replaceSession)
                {
                    fixture.Workbench.EndSession();
                    OpenProject(fixture);
                }
                else old.Close();
                fixture.Click("end-session");
                var current = fixture.Context.Ui.Modals.Single();
                late(true);
                Assert(library.LoadAsync("confirmation-fixture").GetAwaiter().GetResult().Succeeded
                    && ReferenceEquals(CurrentConfirmation(fixture), current),
                    "retired Delete Project callback must not delete bytes or clear the current modal");
            }
        }

        private static void StaleNativeBindingConfirmationCannotClearNewerModal()
        {
            using var fixture = new Fixture(library: ProjectLibrary(withBinding: true));
            OpenProject(fixture);
            fixture.Click("confirm-native-bindings");
            var old = fixture.Context.Ui.Modals.Single();
            var late = CaptureQueuedCallback(old);
            old.Close();
            fixture.Click("end-session");
            var current = fixture.Context.Ui.Modals.Single();
            var toastCount = fixture.Context.Ui.Toasts.Count;
            late(true);
            Assert(ReferenceEquals(CurrentConfirmation(fixture), current) && fixture.Context.Ui.Toasts.Count == toastCount,
                "retired native-binding confirmation must neither resolve nor clear its replacement");
        }

        private static void ManualRemovalReportsThrowingTargetRegistration()
        {
            using var fixture = new Fixture();
            fixture.Workbench.Open();
            var released = 0;
            var entry = AddOwnedEntry(fixture, "manual-one", () => released++);
            var registration = new ThrowingRegistration();
            entry.TargetRegistration = registration;
            fixture.Workbench.Open();
            Assert(fixture.Window.SelectListItem("roster-list", entry.Id).Succeeded, "select owned fixture");
            fixture.Click("remove-selected");
            Assert(released == 1 && registration.DisposeCalls == 1 && !Roster(fixture).Contains(entry),
                "manual remove still releases object ownership after registration failure");
            Assert(fixture.Context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger
                && toast.Message.Contains("target registration", StringComparison.Ordinal)),
                "manual remove must report target registration cleanup failure instead of removed success");
        }

        private static void ManualCleanupAggregatesThrowingOwnedDisposers()
        {
            using var fixture = new Fixture();
            fixture.Workbench.Open();
            var attempts = 0;
            AddOwnedEntry(fixture, "first-owned", () => { attempts++; throw new InvalidOperationException("first-owned cleanup"); });
            AddOwnedEntry(fixture, "second-owned", () => { attempts++; throw new InvalidOperationException("second-owned cleanup"); });
            var result = fixture.Workbench.CleanUpEverything();
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.External
                && result.ErrorMessage.Contains("first-owned cleanup", StringComparison.Ordinal)
                && result.ErrorMessage.Contains("second-owned cleanup", StringComparison.Ordinal),
                "manual bulk cleanup must aggregate every owned disposer failure");
            Assert(attempts == 2 && !Roster(fixture).Any(entry => entry.Owned), "bulk cleanup releases remaining entries after a throw");
        }

        private static void DeadEntriesReportFailuresAndContinue()
        {
            using var fixture = new Fixture();
            fixture.Workbench.Open();
            var attempts = 0;
            foreach (var id in new[] { "first-dead", "second-dead" })
            {
                var entry = AddOwnedEntry(fixture, id, () => { attempts++; throw new InvalidOperationException(id + " cleanup"); });
                ((FakeRobotEditTarget)entry.RobotTarget!).IsAlive = false;
            }
            fixture.Context.AdvanceFrame(TimeSpan.Zero);
            Assert(attempts == 2 && !Roster(fixture).Any(entry => entry.Owned), "dead-entry cleanup continues through all failures");
            Assert(fixture.Context.Logger.Entries.Any(log => log.Level == CapturedLogLevel.Warning
                && log.Message.Contains("first-dead cleanup", StringComparison.Ordinal)
                && log.Message.Contains("second-dead cleanup", StringComparison.Ordinal))
                && fixture.Context.Ui.Toasts.Any(toast => toast.Tone == UiTone.Danger),
                "dead-entry failures must be logged and surfaced rather than silently dropped");
        }

        private static void UndoSpawnReportsThrowingTargetRegistration()
        {
            using var fixture = new Fixture();
            fixture.Workbench.Open();
            fixture.Workbench.SpawnRobot();
            var entry = Roster(fixture).Single(item => item.Owned);
            var old = entry.TargetRegistration;
            entry.TargetRegistration = new ThrowingRegistration(old);
            var result = fixture.Workbench.Undo();
            Assert(!result.Succeeded && result.ErrorMessage.Contains("target registration", StringComparison.Ordinal)
                && fixture.Robots.Agents.ActiveAgents.Count == 0,
                "undo spawn must propagate cleanup failure while retiring the owned robot");
        }

        private static List<CreatorRosterEntry> Roster(Fixture fixture) =>
            (List<CreatorRosterEntry>)typeof(CreatorWorkbench).GetField("roster", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Workbench)!;
        private static object? CurrentConfirmation(Fixture fixture) =>
            typeof(CreatorWorkbench).GetField("confirmation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Workbench);
        private static CreatorRosterEntry AddOwnedEntry(Fixture fixture, string id, Action cleanup)
        {
            var target = new FakeRobotEditTarget(id, TransformState.Identity);
            var entry = new CreatorRosterEntry("owned-test:" + id, id, CreatorContentKind.Prop, true,
                new TestCleanup(() => { target.IsAlive = false; cleanup(); }))
            { RobotTarget = target };
            Roster(fixture).Add(entry);
            return entry;
        }
        private static FakeCreatorProjectLibrary ProjectLibrary(bool withBinding)
        {
            var library = new FakeCreatorProjectLibrary();
            library.SaveAsync(new CreatorEventProject(1, "confirmation-fixture", "Confirmation fixture", string.Empty,
                CreatorProjectScope.Sandbox, string.Empty, string.Empty, new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero),
                nodes: new[] { new CreatorGraphNode("start", CreatorGraphNodeKind.ProjectStart, Vec2.Zero) },
                nativeBindings: withBinding ? new[] { new CreatorNativeBinding("target", "Target", "RobotopiaCity", "borrowed", Vec3.Zero, 50f) } : null))
                .GetAwaiter().GetResult();
            return library;
        }
        private static void OpenProject(Fixture fixture)
        {
            fixture.Workbench.Open();
            fixture.Context.AdvanceFrame(TimeSpan.Zero);
            fixture.Window.SelectListItem("project-list", "confirmation-fixture");
            fixture.Click("load-project");
            fixture.Context.AdvanceFrame(TimeSpan.Zero);
        }
        private sealed class TestCleanup : IDisposable
        {
            private Action? cleanup;
            public TestCleanup(Action cleanup) => this.cleanup = cleanup;
            public void Dispose() { var action = cleanup; cleanup = null; action?.Invoke(); }
        }
        private sealed class ThrowingRegistration : IRobotTargetRegistration
        {
            private readonly IRobotTargetRegistration? inner;
            public ThrowingRegistration(IRobotTargetRegistration? inner = null) => this.inner = inner;
            public string Name => "throwing-target";
            public RobotTargetKind Kind => RobotTargetKind.Robot;
            public bool IsActive => DisposeCalls == 0;
            public int DisposeCalls { get; private set; }
            public void Dispose()
            {
                if (DisposeCalls++ != 0) return;
                inner?.Dispose();
                throw new InvalidOperationException("Injected target registration cleanup failure.");
            }
        }
        private sealed class ReentrantSafety : ICreatorMutationSafetyService
        {
            public Action? DuringAcquire { get; set; }
            public Action? DuringDispose { get; set; }
            public int ActiveLeases { get; private set; }
            public int DisposeCalls { get; private set; }
            public CreatorMutationSafetySnapshot Status => new CreatorMutationSafetySnapshot(CreatorMutationSafetyState.Ready, true, "Synthetic bridge.");
            public OperationResult<ICreatorMutationLease> Acquire(CreatorMutationLeaseRequest request)
            {
                DuringAcquire?.Invoke();
                ActiveLeases++;
                return OperationResult<ICreatorMutationLease>.Success(new Lease(this));
            }
            private sealed class Lease : ICreatorMutationLease
            {
                private ReentrantSafety? owner;
                public Lease(ReentrantSafety owner) => this.owner = owner;
                public string Purpose => "Reentrant fake acquire";
                public bool IsAlive => owner != null;
                public bool IsPersistenceIsolated => owner != null;
                public void Dispose()
                {
                    var current = owner;
                    owner = null;
                    if (current != null) { current.ActiveLeases--; current.DisposeCalls++; current.DuringDispose?.Invoke(); }
                }
            }
        }
    }
}
