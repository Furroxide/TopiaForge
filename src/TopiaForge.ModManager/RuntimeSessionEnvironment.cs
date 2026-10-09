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
