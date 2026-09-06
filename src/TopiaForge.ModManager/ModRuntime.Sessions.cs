using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private GamemodeSessionOrchestrator? activeSessions;
        private IReadOnlyList<RejectedRuntimeSelection> sessionSelectionRejections = Array.Empty<RejectedRuntimeSelection>();
        internal GamemodeSessionOrchestrator Sessions => activeSessions ?? throw new InvalidOperationException("Session authority has not been activated.");
        internal GamemodeSessionOrchestrator ActivateSessionRuntime(EffectiveProfile profile,
            Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>>? mainMenu = null,
            IReadOnlyList<RejectedRuntimeSelection>? rejectedSelections = null)
        {
            ConfigureSessionSelection(profile);
            sessionSelectionRejections = Array.AsReadOnly((rejectedSelections ?? Array.Empty<RejectedRuntimeSelection>()).ToArray());
            activeSessions = new GamemodeSessionOrchestrator(nativeDispatcher, sceneCoordinator,
                new BoundSessionEnvironment(sessionBindings!, mainMenu ?? LoadSessionMainMenuAsync, sessionSelectionRejections), runtimeOwnershipId);
            AttachSessionLifecycle(activeSessions, nativeDispatcher);
            activeSessions.DiagnosticFailure += error => logger.Error(error, "Session lifecycle failure.");
            return activeSessions;
        }
        internal RuntimeSessionSnapshot CaptureSessionRuntime() => sessionBindings?.Capture()
            ?? throw new InvalidOperationException("The selected runtime declarations are unavailable.");
        internal IReadOnlyList<ModLaunchTargetDeclaration> LaunchTargets
        {
            get
            {
                var snapshot = CaptureSessionRuntime();
                var ownership = new LaunchProfileIndex(snapshot.Profile);
                return snapshot.Profile.Packages.SelectMany(package => (package.Manifest.Contributions?.LaunchTargets
                    ?? new List<ModLaunchTargetDeclaration>()).Where(target => ownership.Owns(package, target.Id)))
                    .OrderBy(target => target.SortKey ?? 0).ThenBy(target => target.Id, StringComparer.Ordinal).ToArray();
            }
        }
        internal LaunchResolution ResolveLaunch(LaunchRequest request)
        {
            InvalidRuntimeSelectionException.ThrowIfAny(sessionSelectionRejections);
            var snapshot = CaptureSessionRuntime();
            var selection = LaunchResolver.Resolve(snapshot.Profile, request, snapshot.Observation);
            return !selection.Resolved ? selection : LaunchResolver.ResolveAgain(selection.Plan!.Descriptor,
                snapshot.Profile, snapshot.Observation, snapshot.Bindings);
        }
        internal Task<OperationResult<bool>> LaunchTargetAsync(LaunchRequest request, string? requestId = null,
            CancellationToken cancellationToken = default)
        {
            LaunchResolution resolution;
            try { resolution = ResolveLaunch(request); }
            catch (InvalidRuntimeSelectionException error)
            { return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.InvalidState, error.Message)); }
            return resolution.Resolved ? Sessions.StartAsync(resolution.Plan!.Descriptor,
                requestId ?? Guid.NewGuid().ToString("N"), cancellationToken)
                : Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.InvalidState,
                    string.Join("; ", resolution.Blocks.Select(block => block.Code + ": " + block.Subject))));
        }
        internal async Task DiscoverWorldsAsync(CancellationToken token = default)
        {
            var registry = sessionBindings ?? throw new InvalidOperationException("Discovery requires configured declarations.");
            var discovery = new RuntimeWorldDiscovery(registry, nativeDispatcher);
            foreach (var owner in registry.Capture().DiscoverySources.Select(source => source.Package).Distinct())
            {
                var result = await discovery.DiscoverAsync(owner, cancellationToken: token);
                if (!result.Succeeded) logger.Warn("World discovery for " + owner.Id + ": " + result.ErrorMessage);
            }
        }
        private async Task<OperationResult<bool>> LoadSessionMainMenuAsync(IInternalSceneTransitionService transitions, CancellationToken token)
        {
            if (nativeHost == null) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The native menu adapter is unavailable.");
            var dispatched = transitions.TryDispatch(new NativeSceneRequest(GameScenes.MainMenuSceneName, false,
                "session main menu", observeSceneArrival: false),
                new DelegateNativeSceneDispatch(completion => nativeHost.Scenes.DispatchLoad(
                    new SceneLoadRequest(GameScenes.MainMenuSceneName, SceneLoadMode.Single), completion)), token);
            if (!dispatched.TryGetValue(out var operation)) return OperationResult<bool>.Failure(dispatched.ErrorCode, dispatched.ErrorMessage);
            var result = await operation.Completion;
            await operation.NativeDrained;
            var terminal = await operation.NativeCompletion;
            if (!terminal.Succeeded && terminal.ErrorCode != ModErrorCode.Cancelled)
                return OperationResult<bool>.Failure(terminal.ErrorCode, terminal.ErrorMessage);
            return result.Succeeded ? OperationResult<bool>.Success(true)
                : OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
        }
        private sealed class BoundSessionEnvironment : IRuntimeSessionEnvironment
        {
            private readonly RuntimeBindingRegistry registry;
            private readonly Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> mainMenu;
            private readonly IReadOnlyList<RejectedRuntimeSelection> rejections;
            internal BoundSessionEnvironment(RuntimeBindingRegistry registry,
                Func<IInternalSceneTransitionService, CancellationToken, Task<OperationResult<bool>>> mainMenu,
                IReadOnlyList<RejectedRuntimeSelection> rejections)
            { this.registry = registry; this.mainMenu = mainMenu; this.rejections = rejections; }
            public RuntimeSessionSnapshot Capture()
            {
                InvalidRuntimeSelectionException.ThrowIfAny(rejections);
                return registry.Capture();
            }
            public Task<OperationResult<bool>> LoadMainMenuAsync(IInternalSceneTransitionService transitions, CancellationToken token) => mainMenu(transitions, token);
        }
    }
}
