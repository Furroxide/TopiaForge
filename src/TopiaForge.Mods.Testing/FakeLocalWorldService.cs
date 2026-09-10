using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic local import service bound to a running session and an owning lifetime.</summary>
    public sealed class FakeLocalWorldService : ILocalWorldService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<OverrideLease> overrides = new List<OverrideLease>();
        private readonly List<string> imported = new List<string>();
        /// <summary>Creates a local importer owned by the supplied lifetime.</summary>
        public FakeLocalWorldService(FakeModLifetime lifetime) { this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime)); }
        /// <summary>Gets or sets the running session identity. Null represents no running session.</summary>
        public string? SessionId { get; set; }
        /// <summary>Gets or sets whether native import bindings are available.</summary>
        public bool LocalWorldsAvailable { get; set; } = true;
        /// <summary>Gets the exports returned by discovery, including malformed files.</summary>
        public List<LocalWorldFile> LocalWorlds { get; } = new List<LocalWorldFile>();
        /// <summary>Gets successfully imported file paths.</summary>
        public IReadOnlyList<string> ImportedLocalWorlds => imported.AsReadOnly();
        /// <summary>Gets the current asset override snapshot.</summary>
        public IReadOnlyList<WorldAssetOverride> AssetOverrides => overrides.ConvertAll(item => item.Value).AsReadOnly();
        /// <inheritdoc />
        public OperationResult<IReadOnlyList<LocalWorldFile>> ListLocalWorlds() =>
            !LocalWorldsAvailable ? OperationResult<IReadOnlyList<LocalWorldFile>>.Failure(ModErrorCode.Unavailable, "Local importer unavailable.")
            : OperationResult<IReadOnlyList<LocalWorldFile>>.Success(new List<LocalWorldFile>(LocalWorlds).AsReadOnly());
        /// <inheritdoc />
        public OperationResult<IDisposable> RegisterAssetOverride(WorldAssetOverride assetOverride)
        {
            if (assetOverride == null) throw new ArgumentNullException(nameof(assetOverride));
            if (lifetime.IsStopping) return OperationResult<IDisposable>.Failure(ModErrorCode.Cancelled, "The owner is stopping.");
            var index = overrides.FindIndex(item => item.Value.AssetId == assetOverride.AssetId);
            var lease = new OverrideLease(this, assetOverride);
            if (index < 0) overrides.Add(lease);
            else { overrides[index].Dispose(); overrides.Insert(index, lease); }
            try { lease.Tracked = lifetime.Track(lease); return OperationResult<IDisposable>.Success(lease); }
            catch { lease.Dispose(); throw; }
        }
        /// <inheritdoc />
        public Task<OperationResult<bool>> ImportAsync(string sessionId, string requestedPath, CancellationToken cancellationToken = default)
        {
            OperationResult<bool> result;
            if (lifetime.IsStopping || cancellationToken.IsCancellationRequested) result = Failure(ModErrorCode.Cancelled, "The import owner is stopping.");
            else if (SessionId == null || !string.Equals(sessionId, SessionId, StringComparison.Ordinal)) result = Failure(ModErrorCode.InvalidState, "The running session identity is stale.");
            else if (!LocalWorldsAvailable) result = Failure(ModErrorCode.Unavailable, "Local importer unavailable.");
            else
            {
                var match = LocalWorlds.Find(item => item.Path == requestedPath || item.FileName == requestedPath);
                if (match == null) result = Failure(ModErrorCode.NotFound, "The export was not found.");
                else if (!match.IsLoadable) result = Failure(ModErrorCode.InvalidArgument, match.LoadError);
                else { imported.Add(match.Path); result = OperationResult<bool>.Success(true); }
            }
            return Task.FromResult(result);
        }
        private static OperationResult<bool> Failure(ModErrorCode code, string message) => OperationResult<bool>.Failure(code, message);
        private sealed class OverrideLease : IDisposable
        {
            private FakeLocalWorldService? owner;
            internal OverrideLease(FakeLocalWorldService owner, WorldAssetOverride value) { this.owner = owner; Value = value; }
            internal WorldAssetOverride Value { get; }
            internal IDisposable? Tracked;
            public void Dispose() { var previous = owner; owner = null; previous?.overrides.Remove(this); var tracked = Tracked; Tracked = null; tracked?.Dispose(); }
        }
    }
}
