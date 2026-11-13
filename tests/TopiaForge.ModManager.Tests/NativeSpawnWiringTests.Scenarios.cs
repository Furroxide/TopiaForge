namespace TopiaForge.ModManager.Tests
{
    internal static partial class NativeSpawnWiringTests
    {
        private const string Scenarios = """
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.ModManager;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace SettlementProbe
{
    public static class Scenarios
    {
        public static void Run()
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            Accepted(true);
            Accepted(false);
            PausedFallIsRefused();
            LongPauseAndOverallCap();
            PausePreservesUnpausedBudget();
            LateUnpausedSettlementIsRefused();
            ScaledHeight();
            ControllerAndPlayerCurrentness();
            SceneCurrentness();
            CancellationAndCleanup();
            UnpausedStall();
        }

        private static void Accepted(bool paused)
        {
            using var test = new Fixture(paused);
            MovementController.Lift = 0.13f;
            test.Player.FPSController.enabled = false;
            if (paused) test.ResumeAt(3);
            var result = test.Finish();
            Check(MovementController.Teleports == 1, "placement teleports exactly once while observing settlement");
            Check(result.Succeeded, "actual WorldPreparation accepts settled upward readback " + (paused ? "after resuming" : "while running"));
            Check(result.Value.Spawn == Pose(0.13f), "readiness records the actual applied readback rather than the request");
            Check(Time.frameCount == (paused ? 6 : 3), "upward placement needs its first fixed observation and two steady observations");
            Check(Time.fixedTime > 0f, "accepted readiness observes a real fixed epoch at the Unity time boundary");
            Check(!test.Player.FPSController.enabled, "placement does not enable caller-disabled movement controls");
            Check(test.Admission.Releases == 1, "completed readiness releases its existing admission once");
            test.Close();
            Check(test.Native.IsDisposed && test.Admission.Releases == 1 && !test.Player.FPSController.enabled,
                "close disposes native observation without stealing control state or repeating release");
        }

        private static void PausedFallIsRefused()
        {
            using var test = new Fixture(true);
            MovementController.Lift = -0.011f;
            test.ResumeAt(3);
            Check(test.Finish().ErrorCode == ModErrorCode.External && Time.frameCount == 4
                && test.Player.transform.position.y == -0.011f && MovementController.Teleports == 1,
                "the first fixed epoch refuses a fall over one centimetre after paused rendered frames");
            Check(test.Admission.Releases == 1, "a refused post-pause placement releases its admission");
        }

        private static void LongPauseAndOverallCap()
        {
            using (var test = new Fixture(true))
            {
                MovementController.Lift = 0.13f;
                test.ResumeAt(35);
                var result = test.Finish();
                Check(result.Succeeded && result.Value.Spawn == Pose(0.13f) && Time.frameCount == 38,
                    "more than thirty paused seconds still allow actual fixed-step settlement after resuming");
            }
            using (var test = new Fixture(true))
            {
                Check(test.Finish().ErrorCode == ModErrorCode.TimedOut && Time.frameCount == 120 && Time.fixedTime == 0f
                    && MovementController.Teleports == 1 && test.Admission.Releases == 1,
                    "permanent pause hits the overall bound and releases admission without publishing readiness");
            }
            using (var test = new Fixture(true))
            {
                test.ResumeAt(118);
                Check(test.Finish().ErrorCode == ModErrorCode.TimedOut && Time.frameCount == 120 && Time.fixedTime > 0f,
                    "the overall cap refuses an observation that could otherwise settle at the deadline");
            }
        }

        private static void PausePreservesUnpausedBudget()
        {
            using var test = new Fixture(false);
            test.Clock.AdvanceFixed = false;
            test.Clock.OnFrame = frame =>
            {
                if (frame == 12) Time.timeScale = 0f;
                if (frame == 47) Time.timeScale = 1f;
            };
            Check(test.Finish().ErrorCode == ModErrorCode.TimedOut && Time.frameCount == 65 && Time.fixedTime == 0f,
                "a pause suspends the unpaused budget without resetting the time already spent");
        }

        private static void LateUnpausedSettlementIsRefused()
        {
            using (var test = new Fixture(false))
            {
                test.Clock.AdvanceFixed = false;
                var lateProbe = false;
                test.Clock.OnFrame = frame =>
                {
                    if (frame == 28) test.Clock.AdvanceFixed = true;
                    if (frame == 30) Time.ReadingFixedTime = () => lateProbe = true;
                };
                Check(test.Finish().ErrorCode == ModErrorCode.TimedOut && Time.frameCount == 30 && !lateProbe,
                    "the active budget refuses a would-be settling frame before probing");
            }
            using (var test = new Fixture(false))
            {
                MovementController.Lift = 0.13f;
                var enteredProbe = false;
                test.Clock.OnFrame = frame =>
                {
                    if (frame != 3) return;
                    Time.ReadingFixedTime = () =>
                    {
                        enteredProbe = true;
                        test.Clock.AdvanceElapsed(TimeSpan.FromSeconds(27));
                    };
                };
                Check(test.Finish().ErrorCode == ModErrorCode.TimedOut && Time.frameCount == 3 && enteredProbe
                    && test.Clock.Elapsed == TimeSpan.FromSeconds(30) && test.Admission.Releases == 1,
                    "a probe entered within the active budget cannot publish a valid pose at its deadline");
            }
        }

        private static void ScaledHeight()
        {
            using (var test = new Fixture(false))
            {
                test.Player.transform.lossyScale = new Vector3(3, 3, 3);
                MovementController.Lift = 6f;
                Check(test.Finish().Succeeded, "actual height includes uniform world scale at its inclusive bound");
            }
            using (var test = new Fixture(false))
            {
                test.Player.transform.lossyScale = new Vector3(3, 3, 3);
                MovementController.Lift = 6.001f;
                Check(test.Finish().ErrorCode == ModErrorCode.External, "actual scaled height still refuses above-bound lift");
            }
        }

        private static void ControllerAndPlayerCurrentness()
        {
            foreach (var replacement in new[] { false, true })
            {
                using var test = new Fixture(true);
                test.Clock.OnFrame = _ =>
                {
                    if (replacement) test.Player.gameObject.Controller = new CharacterController { gameObject = test.Player.gameObject };
                    else test.Player.gameObject.Controller!.enabled = false;
                };
                Check(test.Finish().ErrorCode == ModErrorCode.Unavailable,
                    replacement ? "replacement collision controller cannot settle old placement" : "disabled collision controller cannot settle placement");
            }
            using (var test = new Fixture(true))
            {
                test.Clock.OnFrame = _ => PlayerController.Current = PlayerController.Create();
                Check(test.Finish().ErrorCode == ModErrorCode.Conflict, "replacement player cannot settle old placement");
            }
            using (var test = new Fixture(true))
            {
                Check(test.Native.ApplySpawn(Pose(0)) == null, "actual first application begins observation");
                Check(test.Native.ApplySpawn(Pose(1))!.ErrorCode == ModErrorCode.Conflict,
                    "changing the selected request cannot reuse an earlier placement");
                Check(MovementController.Teleports == 1, "changed request does not teleport again");
            }
            foreach (var invalid in new Action<Fixture>[]
            {
                test => test.Player.gameObject.Controller = null,
                test => test.Player.gameObject.Controller!.enabled = false,
                test => test.Player.gameObject.Controller!.height = float.NaN,
                test => test.Player.transform.lossyScale = new Vector3(1, 2, 1),
                test => test.Player.transform.lossyScale = new Vector3(-1, -1, -1)
            })
            {
                using var test = new Fixture(true);
                invalid(test);
                Check(test.Finish().ErrorCode == ModErrorCode.Unavailable && MovementController.Teleports == 0,
                    "unsupported collision shape is refused before native teleport");
            }
            using (var test = new Fixture(true))
            {
                test.Clock.OnFrame = _ => test.Player.gameObject.activeInHierarchy = false;
                Check(test.Finish().ErrorCode == ModErrorCode.TimedOut && MovementController.Teleports == 1,
                    "unavailable player retains bounded wait semantics without reteleport");
            }
        }

        private static void SceneCurrentness()
        {
            foreach (var change in new Action[]
            {
                SceneManager.Unload,
                () => SceneManager.World.handle++,
                () => SceneManager.World.name = "Replacement",
                () => SceneManager.World.path = "Replacement.unity"
            })
            {
                using var test = new Fixture(true);
                test.Clock.OnFrame = _ => change();
                Check(test.Finish().ErrorCode == ModErrorCode.InvalidState,
                    "real captured scene validation refuses unload and identity replacement before another observation");
                Check(test.Admission.Releases == 1, "scene refusal releases existing admission");
            }
        }

        private static void CancellationAndCleanup()
        {
            using (var test = new Fixture(true))
            {
                test.Caller.Cancel();
                Check(test.Finish().ErrorCode == ModErrorCode.Cancelled && MovementController.Teleports == 0,
                    "caller cancellation before finish prevents placement");
                test.Close();
                Check(test.Admission.Releases == 1 && test.Native.IsDisposed, "pre-finish cancellation closes retained admission and native load");
            }
            using (var test = new Fixture(true))
            {
                MovementController.AfterTeleport = test.Caller.Cancel;
                Check(test.Finish().ErrorCode == ModErrorCode.Cancelled && MovementController.Teleports == 1,
                    "cancellation during actual native teleport never publishes readiness");
                Check(test.Admission.Releases == 1, "in-finish cancellation releases existing admission");
            }
            using (var test = new Fixture(true))
            {
                test.Clock.OnFrame = _ => test.Owner.Cancel();
                Check(test.Finish().ErrorCode == ModErrorCode.Cancelled, "owner stop during rendered observation prevents readiness");
                test.Close();
                Check(test.Native.IsDisposed && test.Admission.Releases == 1 && test.Player.FPSController.enabled,
                    "owner stop cleanup preserves initially enabled control state");
            }
        }

        private static void UnpausedStall()
        {
            using var test = new Fixture(false);
            test.Clock.AdvanceFixed = false;
            Check(test.Finish().ErrorCode == ModErrorCode.TimedOut && Time.frameCount == 30 && MovementController.Teleports == 1,
                "unpaused rendered frames cannot replace physics progress within the original thirty-second deadline");
        }

        private static TransformState Pose(float height) => new TransformState(new Vec3(0, height, 0), Quat.Identity, new Vec3(1, 1, 1));
        private static void Check(bool value, string message)
        { if (!value) throw new InvalidOperationException("Native spawn wiring: " + message); }

        private sealed class Fixture : IDisposable
        {
            internal readonly CancellationTokenSource Owner = new CancellationTokenSource();
            internal readonly CancellationTokenSource Caller = new CancellationTokenSource();
            internal readonly FrameClock Clock = new FrameClock();
            internal readonly Admission Admission = new Admission();
            private readonly List<Exception> reported = new List<Exception>();
            internal readonly HostDispatcher Dispatcher;
            internal readonly PlayerController Player;
            internal readonly ProbeNativeLoad Native;
            private readonly WorldPreparation preparation;
            private bool closed;
            internal Fixture(bool paused)
            {
                Dispatcher = new HostDispatcher(reported.Add);
                Time.fixedTime = 0; Time.frameCount = 0; Time.timeScale = paused ? 0 : 1;
                SceneManager.World = new Scene { handle = 7, name = "World", path = "World.unity", isLoaded = true };
                Player = PlayerController.Create(); PlayerController.Current = Player;
                MovementController.Teleports = 0; MovementController.Lift = 0; MovementController.AfterTeleport = null;
                Native = new ProbeNativeLoad();
                preparation = new WorldPreparation(Native, Owner.Token, Caller.Token, Clock, Dispatcher);
                preparation.Attach(Admission);
                try
                {
                    Check(preparation.PrepareAsync().GetAwaiter().GetResult().Succeeded, "actual preparation captures scene and native default spawn");
                }
                catch
                {
                    Dispose();
                    throw;
                }
            }
            internal OperationResult<WorldReadiness> Finish()
            {
                var task = preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, null, default);
                Dispatcher.Drain();
                Check(reported.Count == 0, "host dispatcher reports no callback failures during finish");
                Check(task.IsCompleted, "controlled synchronous frames drain the actual host callback");
                return task.GetAwaiter().GetResult();
            }
            internal void ResumeAt(int frame)
            {
                Clock.OnFrame = current =>
                {
                    if (current != frame) return;
                    Check(Time.fixedTime == 0f && Player.transform.position.y == 0f && MovementController.Teleports == 1,
                        "paused rendered frames leave the player at the requested pose before the first fixed step");
                    Time.timeScale = 1f;
                };
            }
            internal void Close()
            {
                if (closed) return;
                var task = preparation.CloseAsync(); Dispatcher.Drain();
                Check(task.IsCompleted, "actual cleanup drains on its host dispatcher");
                task.GetAwaiter().GetResult(); closed = true;
                Check(reported.Count == 0, "host dispatcher reports no callback failures during close");
            }
            public void Dispose()
            {
                try { Close(); }
                finally
                {
                    try { Dispatcher.Dispose(); }
                    finally
                    {
                        Owner.Dispose(); Caller.Dispose();
                        MovementController.AfterTeleport = null; PlayerController.Current = null; Time.ReadingFixedTime = null;
                    }
                }
            }
        }
    }
}
""";
    }
}
