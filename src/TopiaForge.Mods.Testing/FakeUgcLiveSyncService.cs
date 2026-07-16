using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic UGC live-sync fake with injectable snapshots, patches, and failures.</summary>
    public sealed class FakeUgcLiveSyncService : IUgcLiveSyncService
    {
        private readonly FakeModLifetime lifetime;
        private readonly List<OverrideLease> overrides = new List<OverrideLease>();
        private SessionLease? sessionLease;

        /// <summary>Creates a fake UGC service owned by a mod lifetime.</summary>
        public FakeUgcLiveSyncService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets or sets the deterministic timestamp used for new sessions and injected snapshots.</summary>
        public DateTime CurrentUtc { get; set; } = DateTime.UnixEpoch;

        /// <inheritdoc />
        public UgcSyncSession? CurrentSession => sessionLease?.Session;

        /// <inheritdoc />
        public UgcLiveSyncStatus Status { get; private set; } = UgcLiveSyncStatus.Idle;

        /// <inheritdoc />
        public IReadOnlyList<UgcAssetOverride> AssetOverrides
        {
            get
            {
                var values = new List<UgcAssetOverride>(overrides.Count);
                foreach (var value in overrides)
                {
                    values.Add(value.Override);
                }

                values.Sort((left, right) => StringComparer.Ordinal.Compare(left.AssetId, right.AssetId));
                return values.AsReadOnly();
            }
        }

        /// <summary>Gets the number of active session and asset-override leases.</summary>
        public int ActiveLeaseCount => (sessionLease == null ? 0 : 1) + overrides.Count;

        /// <inheritdoc />
        public event Action<UgcSyncSession>? SessionStarted;

        /// <inheritdoc />
        public event Action<UgcSnapshotInfo>? SnapshotImported;

        /// <inheritdoc />
        public event Action<UgcSnapshotInfo>? PatchApplied;

        /// <inheritdoc />
        public event Action<UgcSyncError>? SyncError;

        /// <inheritdoc />
        public event Action<UgcSyncSession>? SessionStopped;

        /// <inheritdoc />
        public OperationResult<IUgcSyncLease> StartLocalSession(UgcLiveSyncRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.WatchFolder))
            {
                return OperationResult<IUgcSyncLease>.Failure(
                    ModErrorCode.InvalidArgument,
                    "A watch folder is required for a local UGC session.");
            }

            return Start(UgcSyncTransport.LocalFolder, request.WatchFolder, request.SceneId);
        }

        /// <inheritdoc />
        public OperationResult<IUgcSyncLease> StartAutomergeSession(UgcLiveSyncRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            var target = string.IsNullOrWhiteSpace(request.EditorUrl)
                ? request.DocumentUrl
                : request.EditorUrl;
            if (string.IsNullOrWhiteSpace(target))
            {
                return OperationResult<IUgcSyncLease>.Failure(
                    ModErrorCode.InvalidArgument,
                    "An editor or document URL is required for an Automerge UGC session.");
            }

            return Start(UgcSyncTransport.Automerge, target, request.SceneId);
        }

        /// <inheritdoc />
        public OperationResult<IUgcAssetOverrideLease> RegisterAssetOverride(UgcAssetOverride assetOverride)
        {
            if (assetOverride == null)
            {
                throw new ArgumentNullException(nameof(assetOverride));
            }

            foreach (var existing in overrides)
            {
                if (string.Equals(existing.Override.AssetId, assetOverride.AssetId, StringComparison.Ordinal))
                {
                    return OperationResult<IUgcAssetOverrideLease>.Failure(
                        ModErrorCode.Conflict,
                        "A fake UGC override is already registered for '" + assetOverride.AssetId + "'.");
                }
            }

            var lease = new OverrideLease(assetOverride, released => overrides.Remove(released));
            overrides.Add(lease);
            return lifetime.TrackResult<IUgcAssetOverrideLease>(
                lease,
                "The fake mod stopped before the UGC asset override could be registered.");
        }

        /// <summary>Raises a deterministic first-snapshot event for the active session.</summary>
        public OperationResult<UgcSnapshotInfo> ImportSnapshot(
            string projectName,
            string sceneId,
            string sceneName,
            int entityCount,
            string revisionLabel)
        {
            var result = CreateSnapshot(projectName, sceneId, sceneName, entityCount, revisionLabel, true);
            if (result.TryGetValue(out var snapshot))
            {
                SnapshotImported?.Invoke(snapshot);
            }

            return result;
        }

        /// <summary>Raises a deterministic incremental or full-rebuild patch event.</summary>
        public OperationResult<UgcSnapshotInfo> ApplyPatch(
            string projectName,
            string sceneId,
            string sceneName,
            int entityCount,
            string revisionLabel,
            bool isFullRebuild = false)
        {
            var result = CreateSnapshot(
                projectName,
                sceneId,
                sceneName,
                entityCount,
                revisionLabel,
                isFullRebuild);
            if (result.TryGetValue(out var snapshot))
            {
                PatchApplied?.Invoke(snapshot);
            }

            return result;
        }

        /// <summary>Raises a non-fatal synchronization failure while preserving the active session.</summary>
        public void RaiseSyncError(string phase, string message)
        {
            Status = UgcLiveSyncStatus.Error;
            SyncError?.Invoke(new UgcSyncError(phase ?? string.Empty, message ?? string.Empty));
        }

        private OperationResult<IUgcSyncLease> Start(UgcSyncTransport transport, string target, string sceneId)
        {
            if (sessionLease != null)
            {
                return OperationResult<IUgcSyncLease>.Failure(
                    ModErrorCode.Conflict,
                    "A fake UGC live-sync session is already active.");
            }

            var session = new UgcSyncSession(transport, target, sceneId, CurrentUtc);
            var lease = new SessionLease(session, Stop);
            sessionLease = lease;
            var tracked = lifetime.TrackResult<IUgcSyncLease>(
                lease,
                "The fake mod stopped before the UGC live-sync session could begin.");
            if (!tracked.Succeeded)
            {
                return tracked;
            }

            Status = transport == UgcSyncTransport.LocalFolder
                ? UgcLiveSyncStatus.Watching
                : UgcLiveSyncStatus.Connected;
            SessionStarted?.Invoke(session);
            return tracked;
        }

        private void Stop(SessionLease lease)
        {
            if (!ReferenceEquals(sessionLease, lease))
            {
                return;
            }

            sessionLease = null;
            Status = UgcLiveSyncStatus.Stopped;
            SessionStopped?.Invoke(lease.Session);
        }

        private OperationResult<UgcSnapshotInfo> CreateSnapshot(
            string projectName,
            string sceneId,
            string sceneName,
            int entityCount,
            string revisionLabel,
            bool isFullRebuild)
        {
            if (sessionLease == null)
            {
                return OperationResult<UgcSnapshotInfo>.Failure(
                    ModErrorCode.InvalidState,
                    "A fake UGC session must be active before applying content.");
            }

            if (entityCount < 0)
            {
                return OperationResult<UgcSnapshotInfo>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Entity count cannot be negative.");
            }

            Status = sessionLease.Session.Transport == UgcSyncTransport.LocalFolder
                ? UgcLiveSyncStatus.Watching
                : UgcLiveSyncStatus.Connected;
            return OperationResult<UgcSnapshotInfo>.Success(new UgcSnapshotInfo(
                projectName ?? string.Empty,
                sceneId ?? string.Empty,
                sceneName ?? string.Empty,
                entityCount,
                revisionLabel ?? string.Empty,
                isFullRebuild,
                CurrentUtc));
        }

        private sealed class SessionLease : IUgcSyncLease
        {
            private Action<SessionLease>? release;

            public SessionLease(UgcSyncSession session, Action<SessionLease> release)
            {
                Session = session;
                this.release = release;
            }

            public UgcSyncSession Session { get; }
            public bool IsActive => release != null;

            public void Dispose()
            {
                var callback = release;
                release = null;
                callback?.Invoke(this);
            }
        }

        private sealed class OverrideLease : IUgcAssetOverrideLease
        {
            private Action<OverrideLease>? release;

            public OverrideLease(UgcAssetOverride value, Action<OverrideLease> release)
            {
                Override = value;
                this.release = release;
            }

            public UgcAssetOverride Override { get; }
            public bool IsActive => release != null;

            public void Dispose()
            {
                var callback = release;
                release = null;
                callback?.Invoke(this);
            }
        }
    }
}
