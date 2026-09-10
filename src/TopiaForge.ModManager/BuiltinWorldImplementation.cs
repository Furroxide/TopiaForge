using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    // Closed manager activation for content that has no package implementation type.
    internal sealed class BuiltinWorldImplementation : ISessionImplementation<IWorldContentProvider>
    {
        private readonly string kind;
        private readonly string? bundle;
        private readonly string? prefab;
        private readonly string? sceneName;

        internal BuiltinWorldImplementation(PackageIdentity package, ModWorldDeclaration declaration)
        {
            if (package == null) throw new ArgumentNullException(nameof(package));
            if (declaration == null) throw new ArgumentNullException(nameof(declaration));
            Package = new PackageIdentity(package.Id, package.Version);
            DeclarationId = declaration.Id;
            var content = declaration.Content ?? throw new ArgumentException("World content is required.", nameof(declaration));
            kind = content.Kind;
            bundle = content.Bundle;
            prefab = content.Prefab;
            sceneName = content.SceneName;
            if (kind != ModWorldContent.BundleKind && kind != ModWorldContent.GameSceneKind)
                throw new ArgumentException("Only bundle and game-scene declarations use built-in activation.", nameof(declaration));
            if (kind == ModWorldContent.BundleKind ? string.IsNullOrWhiteSpace(bundle) || string.IsNullOrWhiteSpace(prefab)
                : string.IsNullOrWhiteSpace(sceneName))
                throw new ArgumentException("Built-in world content is incomplete.", nameof(declaration));
        }
        public PackageIdentity Package { get; }
        public string DeclarationId { get; }
        public IWorldContentProvider Create() => new Provider(DeclarationId, kind, bundle, prefab, sceneName);

        private sealed class Provider : IWorldContentProvider
        {
            private readonly string id;
            private readonly string kind;
            private readonly string? bundle;
            private readonly string? prefab;
            private readonly string? sceneName;
            internal Provider(string id, string kind, string? bundle, string? prefab, string? sceneName)
            { this.id = id; this.kind = kind; this.bundle = bundle; this.prefab = prefab; this.sceneName = sceneName; }

            public async Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, CancellationToken cancellationToken)
            {
                if (context == null) throw new ArgumentNullException(nameof(context));
                if (!string.Equals(context.WorldId, id, StringComparison.OrdinalIgnoreCase) || context.WorldFamilyId != null)
                    return OperationResult<IWorldInstance>.Failure(ModErrorCode.InvalidArgument, "The built-in world selection does not match its declaration.");
                if (!(context.Context is IInternalWorldRuntimeContext runtime))
                    return OperationResult<IWorldInstance>.Failure(ModErrorCode.Unavailable, "Native world preparation is unavailable in this host.");
                OwnedWorldContent? owned = null;
                try
                {
                    CheckActive(context, cancellationToken);
                    owned = OwnedWorldContent.Create(context.Context.Lifetime, cancellationToken);
                    var token = owned.StoppingToken;
                    CheckActive(context, token);
                    var request = kind == ModWorldContent.GameSceneKind
                        ? NativeWorldLoadRequest.Scene(sceneName!, context.Transition)
                        : NativeWorldLoadRequest.OpenSandbox(context.Transition);
                    var preparation = owned.Add(Require(await runtime.WorldRuntime.PrepareAsync(request, token)));
                    CheckActive(context, token);
                    IEntity? root = null;
                    if (kind == ModWorldContent.BundleKind)
                    {
                        var assets = context.Context.Assets;
                        var loaded = owned.Add(Require(await assets.LoadBundleAsync(bundle!, token)));
                        CheckActive(context, token);
                        var asset = owned.Add(Require(await assets.LoadPrefabAsync(loaded, prefab!, token)));
                        CheckActive(context, token);
                        // Bundle coordinates are authored relative to their prefab origin. Spawn comes from
                        // the selected marker/native provider; the content origin is not a player fallback.
                        root = owned.Add(Require(assets.Spawn(new AssetSpawnRequest(asset, TransformState.Identity))));
                    }
                    CheckActive(context, token);
                    var readiness = Require(await preparation.FinishAsync(context.SpawnPolicy, root, null, token));
                    CheckActive(context, token);
                    if (readiness.Scene.InstanceId != preparation.Scene.InstanceId
                        || !string.Equals(readiness.Scene.Name, preparation.Scene.Name, StringComparison.Ordinal))
                        throw new LoadFailure(ModErrorCode.InvalidState, "The prepared world scene changed before readiness completed.");
                    return OperationResult<IWorldInstance>.Success(new Instance(readiness, owned));
                }
                catch (Exception error)
                {
                    try { owned?.Dispose(); }
                    catch (Exception cleanup) { throw new AggregateException("World startup and cleanup failed.", error, cleanup); }
                    var code = error is OperationCanceledException || (error is ObjectDisposedException
                        && (cancellationToken.IsCancellationRequested || context.Context.Lifetime.IsStopping)) ? ModErrorCode.Cancelled
                        : error is LoadFailure failure ? failure.Code : ModErrorCode.External;
                    return OperationResult<IWorldInstance>.Failure(code, error.Message);
                }
            }

            private static void CheckActive(IWorldLoadContext context, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (context.Context.Lifetime.IsStopping) throw new OperationCanceledException("The world owner is stopping.");
            }
            private static T Require<T>(OperationResult<T> result) where T : class
            {
                if (result.TryGetValue(out var value)) return value;
                var code = result.Succeeded || result.ErrorCode == ModErrorCode.None
                    || !Enum.IsDefined(typeof(ModErrorCode), result.ErrorCode) ? ModErrorCode.External : result.ErrorCode;
                throw new LoadFailure(code, string.IsNullOrWhiteSpace(result.ErrorMessage)
                    ? "World content did not return the required owned result." : result.ErrorMessage!);
            }
        }

        private sealed class Instance : IWorldInstance
        {
            private readonly OwnedWorldContent owned;
            internal Instance(WorldReadiness readiness, OwnedWorldContent owned) { Readiness = readiness; this.owned = owned; }
            public WorldReadiness Readiness { get; }
            public void Dispose() => owned.Dispose();
        }
        private sealed class LoadFailure : Exception
        {
            internal LoadFailure(ModErrorCode code, string message) : base(message) { Code = code; }
            internal ModErrorCode Code { get; }
        }
    }
}
