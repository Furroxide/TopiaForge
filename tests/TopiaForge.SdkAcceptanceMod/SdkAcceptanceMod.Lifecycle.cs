using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.SdkAcceptance
{
    public sealed partial class SdkAcceptanceMod
    {
        private const int RequiredLifecycleCycles = 10;
        private const string LifecycleActionId = "lifecycle-cycle";
        private const string LifecycleCommandId = "lifecycle-cycle";
        private const string LifecycleLocalizationKey = "acceptance.lifecycle-cycle";
        private const string LifecyclePromptId = "dev.topiaforge.sdk-acceptance.lifecycle-prompt";
        private const string LifecycleRobotTarget = "SDK ACCEPTANCE LIFECYCLE TARGET";
        private const string LifecycleSurfaceId = "lifecycle-cycle";
        private const string LifecycleUgcAssetId = "@topiaforge/sdk-acceptance-lifecycle";
        private const string LifecycleWorldId = "dev.topiaforge.sdk-acceptance.lifecycle-world";
        private const string LifecycleGamemodeId = "dev.topiaforge.sdk-acceptance.lifecycle-mode";
        private const string LifecycleMenuEntryId = "dev.topiaforge.sdk-acceptance.lifecycle-menu";

        private async Task RunLifetimeCyclesAsync(PlayerSnapshot player)
        {
            var completedCycles = 0;
            try
            {
                for (var cycle = 0; cycle < RequiredLifecycleCycles; cycle++)
                {
                    if (!CheckMainThread("lifecycle.ten-cycles", "cycle-" + cycle + "-start"))
                    {
                        return;
                    }

                    ProbeExplicitLifetime(cycle);
                    ProbePlayerControl(cycle);
                    ProbeAudio(cycle);
                    ProbeUiModal(cycle);
                    ProbeChronos(cycle);
                    await ProbeCancelledDelayAsync(cycle);

                    var resources = new Stack<IDisposable>();
                    var counters = new LifecycleEventCounters();
                    IInputAction? input = null;
                    IUiSurface? surface = null;
                    ILocalizationRegistration? localization = null;
                    ICommandRegistration? command = null;
                    IExtensionRegistration? extension = null;
                    IPromptOverrideHandle? prompt = null;
                    IRobotTargetRegistration? target = null;
                    IWorldRegistration? world = null;
                    IWorldRegistration? gamemode = null;
                    IWorldRegistration? menu = null;
                    ICreatorSession? creatorSession = null;
                    LifecycleAssetHandles? assets = null;
                    var disposed = false;
                    try
                    {
                        SubscribeCycleEvents(counters, resources);
                        ScheduleCycleCallbacks(counters, resources);
                        input = RegisterCycleInput(resources);
                        surface = RegisterCycleUiSurface(resources, cycle);
                        localization = RegisterCycleLocalization(resources, cycle);
                        command = RegisterCycleCommand(resources, cycle);
                        extension = RegisterCycleExtension(resources, cycle);
                        prompt = RegisterCyclePrompt(resources, cycle);
                        target = RegisterCycleRobotTarget(resources, player, cycle);
                        creatorSession = RegisterCycleCreatorSession(resources, cycle);
                        RegisterCycleWorlds(resources, out world, out gamemode, out menu);
                        assets = await LoadCycleAssetsAsync(resources, player, cycle);
                        await WaitForCycleCallbacksAsync(counters, cycle);

                        DisposeCycleResources(resources);
                        disposed = true;
                        await AssertCycleReleasedAsync(
                            counters,
                            input,
                            surface,
                            localization,
                            command,
                            extension,
                            prompt,
                            target,
                            creatorSession,
                            world,
                            gamemode,
                            menu,
                            assets,
                            cycle);
                        completedCycles++;
                    }
                    finally
                    {
                        if (!disposed)
                        {
                            DisposeCycleResources(resources);
                        }
                    }
                }

                VerifyLifecycleIdsReleased();
                Pass(
                    "lifecycle.ten-cycles",
                    "cycles=" + completedCycles
                    + ";families=lifetime,events,scheduler,input,player-control,assets,entities,interactions,audio,ui,localization,commands,extensions,chronos,prompts,robot-targets,creator-sessions,ugc-overrides,world-registrations"
                    + ";release=reverse-idempotent-reacquired");
            }
            catch (Exception exception)
            {
                Fail(
                    "lifecycle.ten-cycles",
                    "completed=" + completedCycles + "/" + RequiredLifecycleCycles + ";" + exception.Message);
            }
        }

        private void ProbeExplicitLifetime(int cycle)
        {
            var resource = new CountingDisposable();
            var tracked = Context.Lifetime.Track(resource);
            tracked.Dispose();
            tracked.Dispose();
            Require(resource.DisposeCount == 1, "tracked resource was not released exactly once in cycle " + cycle);

            var deferredCount = 0;
            var deferred = Context.Lifetime.Defer(() => deferredCount++);
            deferred.Dispose();
            deferred.Dispose();
            Require(deferredCount == 1, "deferred cleanup was not idempotent in cycle " + cycle);
        }

        private void SubscribeCycleEvents(LifecycleEventCounters counters, Stack<IDisposable> resources)
        {
            resources.Push(Context.Events.SubscribeUpdate(_ =>
            {
                if (CheckMainThread("lifecycle.ten-cycles", "cycle-frame")) counters.Frame++;
            }));
            resources.Push(Context.Events.SubscribeFixedUpdate(_ =>
            {
                if (CheckMainThread("lifecycle.ten-cycles", "cycle-fixed")) counters.Fixed++;
            }));
            resources.Push(Context.Events.SubscribeLateUpdate(_ =>
            {
                if (CheckMainThread("lifecycle.ten-cycles", "cycle-late")) counters.Late++;
            }));
            resources.Push(Context.Events.SubscribeSceneLoaded(_ =>
            {
                if (CheckMainThread("lifecycle.ten-cycles", "cycle-scene")) counters.Scene++;
            }));
            resources.Push(Context.Scenes.SubscribeCheckpointChanged(_ =>
            {
                if (CheckMainThread("lifecycle.ten-cycles", "cycle-checkpoint")) counters.Checkpoint++;
            }));
        }

        private void ScheduleCycleCallbacks(LifecycleEventCounters counters, Stack<IDisposable> resources)
        {
            resources.Push(RequireValue(
                Context.Scheduler.NextFrame(() =>
                {
                    if (CheckMainThread("lifecycle.ten-cycles", "cycle-next-frame")) counters.NextFrame++;
                }),
                "schedule next-frame callback"));
            resources.Push(RequireValue(
                Context.Scheduler.After(TimeSpan.Zero, () =>
                {
                    if (CheckMainThread("lifecycle.ten-cycles", "cycle-after")) counters.After++;
                }),
                "schedule delayed callback"));
            resources.Push(RequireValue(
                Context.Scheduler.Every(TimeSpan.FromMilliseconds(10), () =>
                {
                    if (CheckMainThread("lifecycle.ten-cycles", "cycle-every")) counters.Every++;
                }),
                "schedule repeating callback"));
        }

        private async Task ProbeCancelledDelayAsync(int cycle)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var pending = Context.Scheduler.DelayAsync(TimeSpan.FromMinutes(1), cancellation.Token);
                cancellation.Cancel();
                var cancelled = await pending;
                Require(
                    !cancelled.Succeeded && cancelled.ErrorCode == ModErrorCode.Cancelled,
                    "cancelled async operation did not report Cancelled in cycle " + cycle);
                Require(
                    CheckMainThread("lifecycle.ten-cycles", "cycle-cancelled-delay"),
                    "cancelled async operation completed off the main thread in cycle " + cycle);
            }
        }

        private void ProbePlayerControl(int cycle)
        {
            var first = RequireValue(Context.LocalPlayer.AcquireControl("SDK lifecycle " + cycle + " first"),
                "acquire first player-control lease");
            var second = RequireValue(Context.LocalPlayer.AcquireControl("SDK lifecycle " + cycle + " second"),
                "acquire nested player-control lease");
            try
            {
                Require(first.IsActive && second.IsActive, "nested player-control leases were not active");
                second.Dispose();
                second.Dispose();
                Require(first.IsActive && !second.IsActive, "nested player-control release restored too early");
            }
            finally
            {
                second.Dispose();
                first.Dispose();
                first.Dispose();
            }

            Require(!first.IsActive && !second.IsActive, "player-control leases remained active after release");
        }

        private void ProbeAudio(int cycle)
        {
            var playback = RequireValue(
                Context.Audio.Play(new AudioPlayRequest("acceptance.lifecycle." + cycle, 0f, loop: true)),
                "start lifecycle audio playback");
            playback.Stop();
            playback.Stop();
            playback.Dispose();
            Require(!playback.IsPlaying, "audio playback remained active after release");
        }

        private void ProbeUiModal(int cycle)
        {
            var completedAsCancelled = false;
            var modal = RequireValue(
                Context.Ui.ShowModal(
                    new UiModalRequest("Lifecycle cycle " + (cycle + 1), "Automatic close/release probe."),
                    confirmed => completedAsCancelled = !confirmed),
                "show lifecycle UI modal");
            Require(modal.IsOpen, "lifecycle UI modal did not open");
            modal.Close();
            modal.Close();
            modal.Dispose();
            Require(!modal.IsOpen && completedAsCancelled, "lifecycle UI modal did not close idempotently");
        }

        private void ProbeChronos(int cycle)
        {
            var service = timeControl ?? throw new InvalidOperationException("Chronos provider is unavailable.");
            Require(service.IsAvailable, "Chronos is unavailable on the supported acceptance build");
            var lease = RequireValue(service.Slow("SDK lifecycle " + cycle, 1f), "acquire Chronos lease");
            Require(lease.IsActive, "Chronos lease was not active");
            lease.Release();
            lease.Release();
            lease.Dispose();
            Require(!lease.IsActive, "Chronos lease remained active after release");
        }

        private IInputAction RegisterCycleInput(Stack<IDisposable> resources)
        {
            var definition = new InputActionDefinition(
                LifecycleActionId,
                "Lifecycle cycle",
                new[] { InputBinding.Key("F11") });
            var action = RequireValue(Context.Input.RegisterAction(definition), "register lifecycle input action");
            resources.Push(action);
            var duplicate = Context.Input.RegisterAction(definition);
            Require(!duplicate.Succeeded && duplicate.ErrorCode == ModErrorCode.Conflict,
                "duplicate lifecycle input action did not report Conflict");
            return action;
        }

        private IUiSurface RegisterCycleUiSurface(Stack<IDisposable> resources, int cycle)
        {
            var request = new UiSurfaceRequest(
                LifecycleSurfaceId,
                "LIFECYCLE",
                "Cycle " + (cycle + 1),
                UiSurfaceKind.Hud,
                160f,
                90f);
            var surface = RequireValue(Context.Ui.CreateSurface(request), "create lifecycle UI surface");
            resources.Push(surface);
            Require(Context.Ui.CreateSurface(request).ErrorCode == ModErrorCode.Conflict,
                "duplicate lifecycle UI surface did not report Conflict");
            surface.Hide();
            Require(!surface.IsVisible, "lifecycle UI surface did not hide");
            surface.Show();
            Require(surface.IsVisible, "lifecycle UI surface did not show");
            surface.Hide();
            return surface;
        }

        private ILocalizationRegistration RegisterCycleLocalization(Stack<IDisposable> resources, int cycle)
        {
            var expected = "cycle-" + cycle;
            var registration = RequireValue(
                Context.Localization.Register(new LocalizationCatalog(
                    "en",
                    new Dictionary<string, string> { [LifecycleLocalizationKey] = expected })),
                "register lifecycle localization catalog");
            resources.Push(registration);
            Require(Context.Localization.Get(LifecycleLocalizationKey, "missing") == expected,
                "lifecycle localization catalog did not resolve");
            return registration;
        }

        private ICommandRegistration RegisterCycleCommand(Stack<IDisposable> resources, int cycle)
        {
            var registration = RequireValue(
                Context.Commands.Register(
                    new CommandDefinition(LifecycleCommandId, "Lifecycle registration probe"),
                    _ => OperationResult<string>.Success("cycle-" + cycle)),
                "register lifecycle command");
            resources.Push(registration);
            var duplicate = Context.Commands.Register(
                new CommandDefinition(LifecycleCommandId, "Duplicate lifecycle probe"),
                _ => OperationResult<string>.Success(string.Empty));
            Require(!duplicate.Succeeded && duplicate.ErrorCode == ModErrorCode.Conflict,
                "duplicate lifecycle command did not report Conflict");
            Require(
                Context.Commands.TryExecute(LifecycleCommandId, Array.Empty<string>(), out var result)
                && result?.Succeeded == true
                && result.Value == "cycle-" + cycle,
                "lifecycle command could not execute while registered");
            return registration;
        }

        private IExtensionRegistration RegisterCycleExtension(Stack<IDisposable> resources, int cycle)
        {
            var provider = new LifecycleProbeProvider(cycle);
            var registration = RequireValue(
                Context.Extensions.Register<ILifecycleProbeProvider>(provider),
                "register lifecycle extension provider");
            resources.Push(registration);
            Require(
                registration.IsActive
                && Context.TryGetExtension<ILifecycleProbeProvider>(out var selected)
                && ReferenceEquals(selected, provider),
                "lifecycle extension provider was not selected");
            return registration;
        }

        private IPromptOverrideHandle RegisterCyclePrompt(Stack<IDisposable> resources, int cycle)
        {
            var service = promptOverrides ?? throw new InvalidOperationException("Prompts provider is unavailable.");
            var handle = RequireValue(
                service.Register(new PromptOverrideRequest(
                    LifecyclePromptId,
                    "Lifecycle cycle " + cycle,
                    description: "SDK acceptance reacquisition probe")),
                "register lifecycle prompt override");
            resources.Push(handle);
            Require(
                !handle.IsDisposed
                && service.TryGetEffectiveOverride(LifecyclePromptId, out var effective)
                && ReferenceEquals(effective, handle.Override),
                "lifecycle prompt override was not effective");
            return handle;
        }

        private IRobotTargetRegistration RegisterCycleRobotTarget(
            Stack<IDisposable> resources,
            PlayerSnapshot player,
            int cycle)
        {
            var service = robotObjectives ?? throw new InvalidOperationException("Robot objective provider is unavailable.");
            var registration = RequireValue(
                service.RegisterTarget(
                    LifecycleRobotTarget,
                    RobotTargetKind.Marker,
                    () => new RobotTargetSnapshot(player.Position + new Vec3(0f, cycle * 0.001f, 0f))),
                "register lifecycle robot target");
            resources.Push(registration);
            Require(
                registration.IsActive && service.TryResolveTarget(LifecycleRobotTarget, out _),
                "lifecycle robot target did not resolve");
            return registration;
        }

        private ICreatorSession RegisterCycleCreatorSession(
            Stack<IDisposable> resources,
            int cycle)
        {
            var service = creatorContent
                ?? throw new InvalidOperationException("Creator Content provider is unavailable.");
            var session = RequireValue(
                service.BeginSession(new CreatorSessionOptions(
                    "SDK acceptance lifecycle " + cycle,
                    maximumInstances: 1)),
                "begin lifecycle creator session");
            resources.Push(session);
            Require(session.IsAlive, "lifecycle creator session did not become active");
            return session;
        }

        private void RegisterCycleWorlds(
            Stack<IDisposable> resources,
            out IWorldRegistration world,
            out IWorldRegistration gamemode,
            out IWorldRegistration menu)
        {
            var service = worlds ?? throw new InvalidOperationException("Worlds provider is unavailable.");
            world = RequireValue(
                service.RegisterWorld(new WorldDefinition(
                    LifecycleWorldId,
                    "Lifecycle world",
                    "Registration acquire/release probe.")),
                "register lifecycle world");
            resources.Push(world);
            gamemode = RequireValue(
                service.RegisterGamemode(new GamemodeDefinition(
                    LifecycleGamemodeId,
                    "Lifecycle mode",
                    "Registration acquire/release probe.")),
                "register lifecycle gamemode");
            resources.Push(gamemode);
            menu = RequireValue(
                service.RegisterMenuEntry(new GamemodeMenuEntry(
                    LifecycleMenuEntryId,
                    "Lifecycle entry",
                    "Registration acquire/release probe.",
                    LifecycleGamemodeId,
                    LifecycleWorldId)),
                "register lifecycle menu entry");
            resources.Push(menu);
            Require(world.IsActive && gamemode.IsActive && menu.IsActive,
                "one or more lifecycle world registrations were inactive");
        }

        private async Task<LifecycleAssetHandles> LoadCycleAssetsAsync(
            Stack<IDisposable> resources,
            PlayerSnapshot player,
            int cycle)
        {
            var bundle = RequireValue(
                await Context.Assets.LoadBundleAsync(
                    "third_party/sdk-acceptance-world.bundle",
                    Context.Lifetime.StoppingToken),
                "load lifecycle asset bundle");
            resources.Push(bundle);
            Require(CheckMainThread("lifecycle.ten-cycles", "cycle-bundle-" + cycle),
                "asset bundle completed off the main thread");

            var prefab = RequireValue(
                await Context.Assets.LoadPrefabAsync(
                    bundle,
                    "Assets/World/World.prefab",
                    Context.Lifetime.StoppingToken),
                "load lifecycle prefab");
            resources.Push(prefab);
            Require(CheckMainThread("lifecycle.ten-cycles", "cycle-prefab-" + cycle),
                "prefab completed off the main thread");

            var spawned = RequireValue(
                Context.Assets.Spawn(new AssetSpawnRequest(
                    prefab,
                    new TransformState(
                        player.Position + new Vec3(0f, -1000f - cycle, 0f),
                        Quat.Identity,
                        new Vec3(1f, 1f, 1f)))),
                "spawn lifecycle entity");
            resources.Push(spawned);
            Require(spawned.IsAlive, "spawned lifecycle entity was not alive");

            var interaction = RequireValue(
                Context.Interactions.Register(
                    spawned,
                    new InteractableDefinition("SDK LIFECYCLE", 5f),
                    _ => { }),
                "register lifecycle interaction");
            resources.Push(interaction);
            Require(interaction.IsActive, "lifecycle interaction was not active");

            var ugcOverride = RegisterCycleUgcOverride(resources, prefab);
            return new LifecycleAssetHandles(bundle, prefab, spawned, interaction, ugcOverride);
        }

        private IUgcAssetOverrideLease RegisterCycleUgcOverride(
            Stack<IDisposable> resources,
            IPrefabAsset prefab)
        {
            var service = ugcLiveSync ?? throw new InvalidOperationException("UGC provider is unavailable.");
            var lease = RequireValue(
                service.RegisterAssetOverride(new UgcAssetOverride(LifecycleUgcAssetId, prefab)),
                "register lifecycle UGC asset override");
            resources.Push(lease);
            Require(lease.IsActive && ContainsUgcOverride(service.AssetOverrides, LifecycleUgcAssetId),
                "lifecycle UGC asset override was not active");
            return lease;
        }

        private async Task WaitForCycleCallbacksAsync(LifecycleEventCounters counters, int cycle)
        {
            for (var attempt = 0; attempt < 40 && !counters.RequiredCallbacksSeen; attempt++)
            {
                var delay = await Context.Scheduler.DelayAsync(
                    TimeSpan.FromMilliseconds(25),
                    Context.Lifetime.StoppingToken);
                Require(delay.Succeeded, "callback wait failed in cycle " + cycle + ": " + delay.ErrorMessage);
                Require(CheckMainThread("lifecycle.ten-cycles", "cycle-callback-wait-" + cycle),
                    "callback wait completed off the main thread");
            }

            Require(counters.RequiredCallbacksSeen,
                "frame/fixed/late/next-frame/after/every callbacks were incomplete in cycle " + cycle);
        }

        private async Task AssertCycleReleasedAsync(
            LifecycleEventCounters counters,
            IInputAction input,
            IUiSurface surface,
            ILocalizationRegistration localization,
            ICommandRegistration command,
            IExtensionRegistration extension,
            IPromptOverrideHandle prompt,
            IRobotTargetRegistration target,
            ICreatorSession creatorSession,
            IWorldRegistration world,
            IWorldRegistration gamemode,
            IWorldRegistration menu,
            LifecycleAssetHandles assets,
            int cycle)
        {
            _ = input;
            _ = surface;
            _ = localization;
            _ = command;
            Require(!extension.IsActive && Context.Extensions.GetAll<ILifecycleProbeProvider>().Count == 0,
                "lifecycle extension remained registered after cycle " + cycle);
            Require(prompt.IsDisposed
                    && !(promptOverrides?.TryGetEffectiveOverride(LifecyclePromptId, out _) ?? false),
                "lifecycle prompt remained registered after cycle " + cycle);
            Require(!target.IsActive
                    && !(robotObjectives?.TryResolveTarget(LifecycleRobotTarget, out _) ?? false),
                "lifecycle robot target remained registered after cycle " + cycle);
            Require(!creatorSession.IsAlive,
                "lifecycle creator session remained active after cycle " + cycle);
            Require(!world.IsActive && !gamemode.IsActive && !menu.IsActive,
                "lifecycle world registration remained active after cycle " + cycle);
            Require(!assets.Bundle.IsAlive
                    && !assets.Prefab.IsAlive
                    && !assets.Spawned.IsAlive
                    && !assets.Interaction.IsActive
                    && !assets.UgcOverride.IsActive
                    && !ContainsUgcOverride(ugcLiveSync?.AssetOverrides, LifecycleUgcAssetId),
                "asset, entity, interaction, or UGC handle remained active after cycle " + cycle);
            Require(Context.Localization.Get(LifecycleLocalizationKey, "released") == "released",
                "lifecycle localization catalog remained registered after cycle " + cycle);
            Require(!Context.Commands.TryExecute(
                    LifecycleCommandId,
                    Array.Empty<string>(),
                    out _),
                "lifecycle command remained registered after cycle " + cycle);

            var frame = counters.Frame;
            var fixedCount = counters.Fixed;
            var late = counters.Late;
            var scene = counters.Scene;
            var checkpoint = counters.Checkpoint;
            var nextFrame = counters.NextFrame;
            var after = counters.After;
            var every = counters.Every;
            var delay = await Context.Scheduler.DelayAsync(
                TimeSpan.FromMilliseconds(50),
                Context.Lifetime.StoppingToken);
            Require(delay.Succeeded, "post-release callback wait failed in cycle " + cycle);
            Require(CheckMainThread("lifecycle.ten-cycles", "cycle-release-wait-" + cycle),
                "post-release wait completed off the main thread");
            Require(frame == counters.Frame
                    && fixedCount == counters.Fixed
                    && late == counters.Late
                    && scene == counters.Scene
                    && checkpoint == counters.Checkpoint
                    && nextFrame == counters.NextFrame
                    && after == counters.After
                    && every == counters.Every,
                "event or scheduled callback fired after early release in cycle " + cycle);
        }

        private void VerifyLifecycleIdsReleased()
        {
            var resources = new Stack<IDisposable>();
            try
            {
                RegisterCycleInput(resources);
                RegisterCycleUiSurface(resources, RequiredLifecycleCycles);
                RegisterCycleCommand(resources, RequiredLifecycleCycles);
                RegisterCycleExtension(resources, RequiredLifecycleCycles);
                RegisterCyclePrompt(resources, RequiredLifecycleCycles);
                RegisterCycleRobotTarget(
                    resources,
                    new PlayerSnapshot(Vec3.Zero, new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f))),
                    RequiredLifecycleCycles);
                RegisterCycleCreatorSession(resources, RequiredLifecycleCycles);
                RegisterCycleWorlds(resources, out _, out _, out _);
            }
            finally
            {
                DisposeCycleResources(resources);
            }
        }

        private static T RequireValue<T>(OperationResult<T> result, string operation) where T : notnull
        {
            if (result.TryGetValue(out var value))
            {
                return value;
            }

            throw new InvalidOperationException(
                operation + " failed (" + result.ErrorCode + "): " + result.ErrorMessage);
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        private static void DisposeCycleResources(Stack<IDisposable> resources)
        {
            Exception? first = null;
            while (resources.Count > 0)
            {
                try
                {
                    resources.Pop().Dispose();
                }
                catch (Exception exception)
                {
                    first ??= exception;
                }
            }

            if (first != null)
            {
                throw new InvalidOperationException("reverse lifecycle cleanup failed", first);
            }
        }

        private static bool ContainsUgcOverride(IReadOnlyList<UgcAssetOverride>? overrides, string assetId)
        {
            if (overrides == null) return false;
            for (var index = 0; index < overrides.Count; index++)
            {
                if (string.Equals(overrides[index].AssetId, assetId, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private interface ILifecycleProbeProvider
        {
            int Cycle { get; }
        }

        private sealed class LifecycleProbeProvider : ILifecycleProbeProvider
        {
            public LifecycleProbeProvider(int cycle)
            {
                Cycle = cycle;
            }

            public int Cycle { get; }
        }

        private sealed class LifecycleEventCounters
        {
            public int Frame;
            public int Fixed;
            public int Late;
            public int Scene;
            public int Checkpoint;
            public int NextFrame;
            public int After;
            public int Every;

            public bool RequiredCallbacksSeen =>
                Frame > 0 && Fixed > 0 && Late > 0 && NextFrame > 0 && After > 0 && Every > 0;
        }

        private sealed class LifecycleAssetHandles
        {
            public LifecycleAssetHandles(
                IAssetBundle bundle,
                IPrefabAsset prefab,
                ISpawnedEntity spawned,
                IInteractableRegistration interaction,
                IUgcAssetOverrideLease ugcOverride)
            {
                Bundle = bundle;
                Prefab = prefab;
                Spawned = spawned;
                Interaction = interaction;
                UgcOverride = ugcOverride;
            }

            public IAssetBundle Bundle { get; }
            public IPrefabAsset Prefab { get; }
            public ISpawnedEntity Spawned { get; }
            public IInteractableRegistration Interaction { get; }
            public IUgcAssetOverrideLease UgcOverride { get; }
        }

        private sealed class CountingDisposable : IDisposable
        {
            private int disposed;

            public int DisposeCount { get; private set; }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) == 0) DisposeCount++;
            }
        }
    }
}
