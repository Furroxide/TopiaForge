using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    /// <summary>Loads the generated Open Sandbox with owned geometry, environment, player readiness, and kill plane.</summary>
    public sealed class OpenSandboxProvider : IWorldContentProvider
    {
        public Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (context.SpawnPolicy.Kind != WorldSpawnKind.ProviderDefault)
                return Task.FromResult(OperationResult<IWorldInstance>.Failure(ModErrorCode.NotFound,
                    "The generated Open Sandbox does not contain authored spawn markers."));
            return WorldProviderLoader.LoadAsync(context, NativeWorldLoadRequest.OpenSandbox(context.Transition), cancellationToken,
                async (preparation, resources) =>
                {
                    if (!preparation.NativeDefaultSpawn.HasValue)
                        return OperationResult<WorldReadiness>.Failure(ModErrorCode.Unavailable, "Open Sandbox has no resolved native bootstrap spawn.");
                    if (!UnityWorldScene.TryGetLoaded(preparation.Scene, out var scene))
                        return OperationResult<WorldReadiness>.Failure(ModErrorCode.InvalidState, "The prepared Open Sandbox scene is no longer loaded.");
                    resources.ThrowIfStopping();
                    var spawn = preparation.NativeDefaultSpawn.Value;
                    var root = UnityWorldResources.Own(resources, new GameObject("TopiaForge Worlds - Open Sandbox"));
                    SceneManager.MoveGameObjectToScene(root, scene);
                    SandboxArenaBuilder.Build(root, new Vector3(spawn.Position.X, spawn.Position.Y, spawn.Position.Z), context.Context.Logger, resources);
                    HdrpEnvironment.Apply(root, resources);
                    var ready = await preparation.FinishAsync(context.SpawnPolicy, null, spawn, resources.StoppingToken);
                    resources.ThrowIfStopping();
                    if (ready.TryGetValue(out var readiness))
                    {
                        if (!UnityWorldScene.ContainsRoot(preparation.Scene, root)
                            || readiness.Scene.InstanceId != preparation.Scene.InstanceId
                            || !string.Equals(readiness.Scene.Name, preparation.Scene.Name, StringComparison.Ordinal))
                            return OperationResult<WorldReadiness>.Failure(ModErrorCode.InvalidState,
                                "The generated Open Sandbox content was removed or moved during readiness.");
                        var guard = root.AddComponent<OpenSandboxKillPlane>();
                        guard.Initialize(readiness, resources.StoppingToken, context.Context.Logger);
                    }
                    return ready;
                });
        }
    }
}
