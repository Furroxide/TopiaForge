using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    internal sealed class LocalWorldService : ILocalWorldService, IOwnerContextBoundExtensionFactory, IDisposable
    {
        private readonly IModLogger logger;
        private readonly Func<ILocalWorldImportHost> createHost;
        private readonly bool enabled;
        private readonly string configuredFolder;
        private bool disposed;
        internal LocalWorldService(IModLogger logger, Func<ILocalWorldImportHost> createHost, bool enabled, string folder)
        { this.logger = logger; this.createHost = createHost; this.enabled = enabled; configuredFolder = folder; }
        public void Dispose() { disposed = true; }
        public OperationResult<IDisposable> RegisterAssetOverride(WorldAssetOverride value) =>
            OperationResult<IDisposable>.Failure(ModErrorCode.InvalidState, "Use the local service from the owning mod context.");
        public Task<OperationResult<bool>> ImportAsync(string sessionId, string path, CancellationToken token = default) =>
            Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Use the local service from the owning mod context."));
        public OperationResult<IReadOnlyList<LocalWorldFile>> ListLocalWorlds()
        {
            if (disposed || !enabled) return OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(ModErrorCode.Unavailable, "Local worlds are disabled.");
            try
            {
                using var host = createHost();
                if (!host.CanScanFolder) return OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(ModErrorCode.Unavailable, "The native local export scanner is unavailable.");
                var folder = ResolveFolder(host);
                if (folder.Length == 0) return OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(ModErrorCode.InvalidState, "No local world folder is configured.");
                return OperationResult<IReadOnlyList<LocalWorldFile>>.Success(host.ScanFolder(folder)
                    .Select(item => new LocalWorldFile(item.Path, item.FileName, item.ProjectName, item.LoadError)).ToArray());
            }
            catch (Exception error) { return OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(ModErrorCode.External, error.Message); }
        }
        private string ResolveFolder(ILocalWorldImportHost host) => RoboWorldImportPlan.TryNormalizeFolder(configuredFolder)
            ?? RoboWorldImportPlan.TryNormalizeFolder(host.GetDefaultImportFolder()) ?? string.Empty;
        object IOwnerContextBoundExtensionFactory.CreateOwnerFacade(Type contractType, IModContext context)
        {
            if (contractType != typeof(ILocalWorldService) || !(context is IInternalWorldSessionContext runtime))
                throw new ArgumentException("The local importer requires the consuming session context.");
            return new Facade(this, context, runtime.ContentOperations);
        }
        private sealed class Facade : ILocalWorldService
        {
            private readonly LocalWorldService service;
            private readonly IModContext context;
            private readonly IInternalWorldSessionOperations operations;
            private readonly Dictionary<string, OverrideLease> overrides = new Dictionary<string, OverrideLease>(StringComparer.Ordinal);
            internal Facade(LocalWorldService service, IModContext context, IInternalWorldSessionOperations operations)
            { this.service = service; this.context = context; this.operations = operations; }
            public OperationResult<IReadOnlyList<LocalWorldFile>> ListLocalWorlds() => context.Lifetime.IsStopping
                ? OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(ModErrorCode.Cancelled, "The import owner is stopping.") : service.ListLocalWorlds();
            public OperationResult<IDisposable> RegisterAssetOverride(WorldAssetOverride value)
            {
                if (value == null) throw new ArgumentNullException(nameof(value));
                if (context.Lifetime.IsStopping || service.disposed) return OperationResult<IDisposable>.Failure(ModErrorCode.Cancelled, "The import owner is stopping.");
                if (overrides.TryGetValue(value.AssetId, out var previous)) previous.Dispose();
                var lease = new OverrideLease(this, value); overrides.Add(value.AssetId, lease);
                try { lease.Tracked = context.Lifetime.Track(lease); return OperationResult<IDisposable>.Success(lease); }
                catch { lease.Dispose(); throw; }
            }
            public Task<OperationResult<bool>> ImportAsync(string sessionId, string path, CancellationToken token = default)
            {
                if (service.disposed || context.Lifetime.IsStopping || token.IsCancellationRequested)
                    return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The import owner is stopping."));
                if (!service.enabled) return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Local worlds are disabled."));
                return operations.RunContentOperationAsync(sessionId, async (content, cancellation) =>
                {
                    if (!(content.Context is IInternalSceneTransitionContext transitions))
                        return OperationResult<IDisposable>.Failure(ModErrorCode.Unavailable, "The shared transition executor is unavailable.");
                    using var host = service.createHost();
                    if (!host.IsAvailable) return OperationResult<IDisposable>.Failure(ModErrorCode.Unavailable, "The native local importer is unavailable.");
                    if (!RoboWorldImportPlan.TryPlan(service.ResolveFolder(host), path, File.Exists, out var plan, out var reason))
                        return OperationResult<IDisposable>.Failure(ModErrorCode.InvalidArgument, reason);
                    cancellation.ThrowIfCancellationRequested();
                    var prepared = host.Prepare(plan!, overrides.Values.Select(item => item.Value).ToArray(), content.World.Scene);
                    if (!prepared.TryGetValue(out var transaction)) return OperationResult<IDisposable>.Failure(prepared.ErrorCode, prepared.ErrorMessage);
                    using (transaction)
                        return await LocalWorldImportOperation.RunAsync(transitions.SceneTransitions, content.World.Scene, cancellation, transaction);
                }, token);
            }
            private sealed class OverrideLease : IDisposable
            {
                private Facade? owner;
                internal OverrideLease(Facade owner, WorldAssetOverride value) { this.owner = owner; Value = value; }
                internal WorldAssetOverride Value { get; }
                internal IDisposable? Tracked;
                public void Dispose()
                {
                    var previous = owner; owner = null;
                    if (previous != null && previous.overrides.TryGetValue(Value.AssetId, out var current) && ReferenceEquals(current, this))
                        previous.overrides.Remove(Value.AssetId);
                    var tracked = Tracked; Tracked = null; tracked?.Dispose();
                }
            }
        }
    }
}
