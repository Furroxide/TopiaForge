using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>Represents a loaded, owner-scoped asset bundle.</summary>
    public interface IAssetBundle : IDisposable
    {
        /// <summary>Gets the bundle's package-relative path.</summary>
        string RelativePath { get; }

        /// <summary>Gets whether the bundle remains usable.</summary>
        bool IsAlive { get; }
    }

    /// <summary>Represents a prefab resolved from a loaded asset bundle.</summary>
    public interface IPrefabAsset : IDisposable
    {
        /// <summary>Gets the asset name inside its bundle.</summary>
        string Name { get; }

        /// <summary>Gets whether the prefab and its bundle remain usable.</summary>
        bool IsAlive { get; }
    }

    /// <summary>Describes a prefab spawn.</summary>
    public sealed class AssetSpawnRequest
    {
        /// <summary>Creates a prefab spawn request.</summary>
        public AssetSpawnRequest(IPrefabAsset prefab, TransformState transform)
        {
            Prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
            Transform = transform;
        }

        /// <summary>Gets the prefab to instantiate.</summary>
        public IPrefabAsset Prefab { get; }

        /// <summary>Gets the initial world transform.</summary>
        public TransformState Transform { get; }
    }

    /// <summary>Represents an entity spawned and owned by the current mod.</summary>
    public interface ISpawnedEntity : IEntity, IDisposable
    {
        /// <summary>Gets the initial transform supplied to the spawn operation.</summary>
        TransformState InitialTransform { get; }
    }

    /// <summary>Loads package assets and spawns opaque, lifetime-owned entities.</summary>
    public interface IAssetService
    {
        /// <summary>Loads an asset bundle from a safe package-relative path.</summary>
        Task<OperationResult<IAssetBundle>> LoadBundleAsync(
            string relativePath,
            CancellationToken cancellationToken = default);

        /// <summary>Loads a prefab from a bundle created by this context.</summary>
        Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(
            IAssetBundle bundle,
            string assetName,
            CancellationToken cancellationToken = default);

        /// <summary>Spawns a prefab and owns the resulting entity for the current mod lifetime.</summary>
        OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request);
    }

    /// <summary>Describes an audio cue playback.</summary>
    public sealed class AudioPlayRequest
    {
        /// <summary>Creates an audio playback request.</summary>
        public AudioPlayRequest(string cueId, float volume = 1f, bool loop = false, Vec3? position = null)
        {
            if (string.IsNullOrWhiteSpace(cueId))
            {
                throw new ArgumentException("An audio cue id is required.", nameof(cueId));
            }

            if (volume < 0f || volume > 1f || float.IsNaN(volume))
            {
                throw new ArgumentOutOfRangeException(nameof(volume));
            }

            CueId = cueId;
            Volume = volume;
            Loop = loop;
            Position = position;
        }

        /// <summary>Gets the framework or provider cue id.</summary>
        public string CueId { get; }

        /// <summary>Gets the normalized playback volume.</summary>
        public float Volume { get; }

        /// <summary>Gets whether playback should loop until released.</summary>
        public bool Loop { get; }

        /// <summary>Gets an optional world position; a missing value requests non-positional playback.</summary>
        public Vec3? Position { get; }
    }

    /// <summary>Represents lifetime-owned audio playback.</summary>
    public interface IAudioPlayback : IDisposable
    {
        /// <summary>Gets whether the cue is still playing.</summary>
        bool IsPlaying { get; }

        /// <summary>Stops playback. Calling this more than once is safe.</summary>
        void Stop();
    }

    /// <summary>Plays framework audio cues without exposing engine audio objects.</summary>
    public interface IAudioService
    {
        /// <summary>Starts a cue and tracks the playback for the current mod lifetime.</summary>
        OperationResult<IAudioPlayback> Play(AudioPlayRequest request);
    }
}
