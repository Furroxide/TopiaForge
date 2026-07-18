using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        public static void Run()
        {
            RegistrationConflictsFailClosed();
            SuccessfulModLifecycleReusesOneContextWithoutSessionLeaks();
            SceneReadinessRequiresTheSessionScene();
            CompleteWaveExceedsTheConcurrentAliveCap();
            BruteHealthIsControllerOwned();
            ControlAndWorldClocksStaySeparated();
            HardFreezeBlocksCustomSimulation();
            StrandedRangeGapCannotDeadlockWave();
            RestartRestoresNativeHealthAndCleansAgents();
            RestartRestoresPartiallyInjuredNativeHealthExactly();
            DelayedNativeHealthCaptureStillRestores();
            SameIdPlayerReplacementDoesNotReceiveOldNativeHealth();
            GameOverUiFailureRestartsSafely();
            StaleGameOverFreezeRecoversAfterChronosReset();
            GameOverControlRetryDoesNotSpamDiagnostics();
            StaleShopFreezeRecoversAfterChronosReset();
            PauseFailuresAreBackedOffAndReportedOnce();
            StaleSuperhotLeasesReacquireAfterRestart();
            HudSkipsSteadyStateBodyWrites();
            DisabledShopSkipsCreditsAndRequisitionFeedback();
            UplinkFailuresExplainWithoutSpendingCharge();
            FullyStabilizedAllyCheckIsFreeAtZeroCharge();
            LiveJackInCompositionFailureCleansUpAndFallsBack();
            LiveJackInUsesRobotKitConversationAndReleasesControl();
            DismissingLiveJackInRefusesWithoutDeterministicFallback();
            LiveJackInDefaultsToTextAndSamplesVoiceWithUiFocus();
            PlayerEntityRebindsAfterNativeRecreation();
            AllyRetargetCadenceAndCapAreEnforced();
            ReturnToMenuLoadsTheMenuScene();
            RestartIsRejectedDuringPendingMenuReturn();
            ReturningPresentationFailureSuppressesStaleRestart();
            RuntimeMathSaturatesAtNumericBoundaries();
            RepeatedLifecycleReturnsToLeakBaseline();
        }

        private static ZombiesConfig FastConfig()
        {
            var config = new ZombiesConfig
            {
                StartingCountdownSeconds = 0f,
                InterWaveDelaySeconds = 30f,
                BaseZombiesPerWave = 1,
                ZombiesPerWaveIncrement = 0,
                MaxAliveZombies = 1,
                SpawnIntervalSeconds = 0f,
                PlayerIntegrity = 1000f,
                ZombieHealth = 10f,
                ZombieAttackDamage = 0.1f,
                ZapperDamage = 10f,
                ZapperCooldownSeconds = 0f,
                HeadshotDamageMultiplier = 1f,
                HeadshotFlatBonusScore = 0,
                ChargeShotEnabled = false,
                ArchetypesEnabled = false,
                EnableEnemyEmotes = false,
                OverrideEnabled = false,
                ShopEnabled = false,
            };
            return config;
        }

        private static void AimAtProxy(Harness harness, FakeRobotAgent agent)
        {
            var proxy = new FakeEntity(agent.Id, "Robot collider proxy", agent.Position);
            harness.Context.Physics.RaycastHit = new PhysicsHit(
                proxy,
                agent.Position,
                new Vec3(0f, 1f, 0f),
                1f);
        }

        private static FakeUiSurface FindSurface(FakeModContext context, string id)
        {
            foreach (var surface in context.Ui.Surfaces)
            {
                if (string.Equals(surface.Id, id, StringComparison.Ordinal))
                {
                    return surface;
                }
            }

            throw new InvalidOperationException("Expected UI surface '" + id + "'.");
        }

        private static string LastToast(FakeModContext context)
        {
            var toasts = context.Ui.Toasts;
            return toasts.Count == 0 ? string.Empty : toasts[toasts.Count - 1].Message;
        }

        private static int CountDiagnostics(FakeModContext context, string code)
        {
            var count = 0;
            foreach (var diagnostic in context.Diagnostics.GetSnapshot())
            {
                if (string.Equals(diagnostic.Entry.Code, code, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        private static void PumpUntil(
            Harness harness,
            Func<bool> condition,
            ref int maximumConcurrent,
            string message)
        {
            for (var frame = 0; frame < 30; frame++)
            {
                maximumConcurrent = Math.Max(maximumConcurrent, harness.Robots.Agents.ActiveAgents.Count);
                if (condition())
                {
                    return;
                }

                harness.Advance(0.01f);
            }

            throw new InvalidOperationException("Assertion failed: " + message + ". Status: "
                + harness.Controller.DescribeStatus());
        }

        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message + ".");
            }
        }

        private sealed class Harness : IDisposable
        {
            private bool disposed;

            public Harness(
                ZombiesConfig config,
                string activeScene = "ZombiesArena",
                string sessionScene = "ZombiesArena",
                bool withChronos = false,
                Func<CancellationToken, Task<OperationResult<SceneSnapshot>>>? returnToMenu = null)
            {
                Config = config;
                Context = new FakeModContext();
                Context.Scenes.Load(activeScene);
                Context.Player.Snapshot = new PlayerSnapshot(
                    Vec3.Zero,
                    new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
                Context.Player.Health = new PlayerHealthSnapshot(config.PlayerIntegrity, config.PlayerIntegrity);
                Robots = new FakeRobotKit(Context.Lifetime);
                Robots.Agents.AutoCompleteAgentMovement = false;
                Robots.Agents.PlayerEntity = new FakeEntity("zombies-player", "Player", Vec3.Zero);
                if (config.UseLiveBrain && config.ConversationEnabled)
                {
                    Assert(Context.Extensions.Register<IRobotConversationService>(Robots.Conversations).Succeeded,
                        "RobotKit conversations should register in the live JACK IN harness");
                    Assert(Context.Extensions.Register<IPlayerDialogueInputService>(Robots.DialogueInput).Succeeded,
                        "RobotKit dialogue input should register in the live JACK IN harness");
                }

                if (withChronos)
                {
                    Chronos = new FakeTimeControlService(Context.Lifetime);
                    Assert(Context.Extensions.Register<ITimeControlService>(Chronos).Succeeded,
                        "Chronos should register in the fake extension service");
                }

                var session = new WorldSession(
                    "test.world",
                    "topiaforge.zombies",
                    "gameScene",
                    sessionScene,
                    DateTimeOffset.UnixEpoch);
                Controller = new ZombiesController(
                    Context,
                    config,
                    Robots.Agents,
                    session,
                    returnToMenu ?? (cancellationToken => Context.Scenes.LoadAsync(
                        new SceneLoadRequest(GameScenes.MainMenuSceneName),
                        cancellationToken)));
            }

            public ZombiesConfig Config { get; }
            public FakeModContext Context { get; }
            public FakeRobotKit Robots { get; }
            public FakeTimeControlService? Chronos { get; }
            public ZombiesController Controller { get; }

            public void ReadyToWave()
            {
                Advance(0.01f);
                Advance(0.01f);
                Assert(Controller.TestingPhase == ZombiesPhase.Wave,
                    "the fast harness should enter its first wave");
            }

            public void Advance(float seconds)
            {
                Chronos?.Advance(seconds);
                Context.AdvanceFrame(TimeSpan.FromSeconds(seconds));
            }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                Controller.Dispose();
                Context.Dispose();
                Context.AssertNoLeaks();
            }
        }

        private sealed class TestWorldPauseMenuService : IWorldPauseMenuService
        {
            private readonly FakeModLifetime lifetime;
            private readonly Dictionary<string, PauseRegistration> actions =
                new Dictionary<string, PauseRegistration>(StringComparer.Ordinal);

            public TestWorldPauseMenuService(FakeModLifetime lifetime)
            {
                this.lifetime = lifetime;
            }

            public bool IsAvailable => !lifetime.IsStopping;
            public int ActiveActionCount => actions.Count;

            public OperationResult<IDisposable> RegisterAction(WorldPauseAction action)
            {
                if (actions.ContainsKey(action.Id))
                {
                    return OperationResult<IDisposable>.Failure(
                        ModErrorCode.Conflict,
                        "A pause action already uses '" + action.Id + "'.");
                }

                var registration = new PauseRegistration(
                    action.Id,
                    action.Callback,
                    id => actions.Remove(id));
                actions.Add(action.Id, registration);
                try
                {
                    registration.AttachLifetimeLease(lifetime.Track(registration));
                    return OperationResult<IDisposable>.Success(registration);
                }
                catch (ObjectDisposedException)
                {
                    registration.Dispose();
                    return OperationResult<IDisposable>.Failure(
                        ModErrorCode.Cancelled,
                        "The pause action owner is stopping.");
                }
            }

            public OperationResult<IDisposable> InterceptExit(
                Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor)
            {
                return OperationResult<IDisposable>.Failure(
                    ModErrorCode.Unavailable,
                    "The lifecycle fixture does not need an exit interceptor.");
            }

            public bool Invoke(string id)
            {
                return actions.TryGetValue(id, out var registration) && registration.Invoke();
            }

            private sealed class PauseRegistration : IDisposable
            {
                private readonly string id;
                private Action? callback;
                private Action<string>? release;
                private IDisposable? lifetimeLease;

                public PauseRegistration(string id, Action callback, Action<string> release)
                {
                    this.id = id;
                    this.callback = callback;
                    this.release = release;
                }

                public void AttachLifetimeLease(IDisposable lease)
                {
                    lifetimeLease = lease;
                }

                public bool Invoke()
                {
                    var active = callback;
                    if (active == null)
                    {
                        return false;
                    }

                    active();
                    return true;
                }

                public void Dispose()
                {
                    callback = null;
                    var releaseNow = Interlocked.Exchange(ref release, null);
                    try
                    {
                        releaseNow?.Invoke(id);
                    }
                    finally
                    {
                        Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                    }
                }
            }
        }
    }
}
