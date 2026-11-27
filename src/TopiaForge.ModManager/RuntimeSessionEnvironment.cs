using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Capture is atomic at the production registry; constructor validation alone is not package verification.</summary>
    internal interface IRuntimeSessionEnvironment
    {
        RuntimeSessionSnapshot Capture();
        Task<OperationResult<bool>> LoadMainMenuAsync(IInternalSceneTransitionService transitions, CancellationToken cancellationToken);

        /// <summary>
        /// Loads a level by its scene name through the game's own loader, replacing the scene as a Build Scenes world does, and
        /// once the player is in it, stands the player at <paramref name="spawn"/> when one is given. Session follow (WS3.8)
        /// loads the level a host's additive arena is built over with it, and the level a host is in outside a world session.
        /// </summary>
        Task<OperationResult<bool>> LoadSceneAsync(IInternalSceneTransitionService transitions, string sceneName, TransformState? spawn,
            CancellationToken cancellationToken);
    }

    /// <summary>The gameplay host's level loader, which session follow (WS3.8) loads a level by its scene name through.</summary>
    internal interface ISessionLevelLoader
    {
        /// <inheritdoc cref="IRuntimeSessionEnvironment.LoadSceneAsync"/>
        Task<OperationResult<bool>> LoadLevelAsync(IInternalSceneTransitionService transitions, string sceneName, TransformState? spawn,
            CancellationToken cancellationToken);
    }

    internal sealed class RuntimeSessionSnapshot
    {
        internal RuntimeSessionSnapshot(EffectiveProfile profile, RuntimeBindingSnapshot bindings,
            IReadOnlyDictionary<string, ModContext> contexts,
            IEnumerable<ISessionImplementation<IGamemodeFactory>> gamemodes,
            IEnumerable<ISessionImplementation<IWorldContentProvider>> worlds,
            RuntimeObservation? observation = null,
            IEnumerable<ISessionImplementation<IWorldDiscoverySource>>? discoverySources = null,
            IEnumerable<RuntimeBindingFailure>? bindingFailures = null,
            bool packageCleanupPending = false)
        {
            Profile = profile;
            PackageCleanupPending = packageCleanupPending;
            Bindings = bindings;
            Contexts = new ReadOnlyDictionary<string, ModContext>(contexts.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
            Gamemodes = Array.AsReadOnly(gamemodes.ToArray());
            Worlds = Array.AsReadOnly(worlds.ToArray());
            Observation = observation ?? RuntimeObservation.None;
            DiscoverySources = Array.AsReadOnly((discoverySources ?? Array.Empty<ISessionImplementation<IWorldDiscoverySource>>()).ToArray());
            BindingFailures = Array.AsReadOnly((bindingFailures ?? Array.Empty<RuntimeBindingFailure>()).ToArray());
        }
        internal IReadOnlyList<ISessionImplementation<IWorldDiscoverySource>> DiscoverySources { get; }
        internal IReadOnlyList<RuntimeBindingFailure> BindingFailures { get; }
        internal bool PackageCleanupPending { get; }
        internal EffectiveProfile Profile { get; }
        internal RuntimeBindingSnapshot Bindings { get; }
        internal RuntimeObservation Observation { get; }
        internal IReadOnlyDictionary<string, ModContext> Contexts { get; }
        internal IReadOnlyList<ISessionImplementation<IGamemodeFactory>> Gamemodes { get; }
        internal IReadOnlyList<ISessionImplementation<IWorldContentProvider>> Worlds { get; }
    }
}
