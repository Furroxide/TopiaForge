using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Internal
{
    internal interface IInternalWorldRuntimeContext
    {
        IInternalWorldRuntimeService WorldRuntime { get; }
    }

    internal interface IInternalWorldRuntimeService
    {
        Task<OperationResult<IReadOnlyList<NativeWorldEntry>>> DiscoverAsync(
            NativeWorldSource source, int maximumResults, CancellationToken cancellationToken);
        Task<OperationResult<IInternalWorldPreparation>> PrepareAsync(
            NativeWorldLoadRequest request, CancellationToken cancellationToken);
    }

    internal enum NativeWorldSource { CuratedLevels, BuildScenes }
    internal enum NativeWorldLoadKind { Scene, Curated, OpenSandbox }

    internal sealed class NativeWorldEntry
    {
        public NativeWorldEntry(NativeWorldSource source, string sourceKey, string name, string description, string sceneName)
        {
            if (!Enum.IsDefined(typeof(NativeWorldSource), source)) throw new ArgumentOutOfRangeException(nameof(source));
            Source = source;
            SourceKey = Required(sourceKey, nameof(sourceKey));
            Name = Required(name, nameof(name));
            Description = description ?? throw new ArgumentNullException(nameof(description));
            SceneName = Required(sceneName, nameof(sceneName));
        }
        public NativeWorldSource Source { get; }
        public string SourceKey { get; }
        public string Name { get; }
        public string Description { get; }
        public string SceneName { get; }
        private static string Required(string value, string name) => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A native world identity is required.", name) : value;
    }

    internal sealed class NativeWorldLoadRequest
    {
        private NativeWorldLoadRequest(NativeWorldLoadKind kind, string? key, WorldLoadTransition transition)
        {
            if (!Enum.IsDefined(typeof(WorldLoadTransition), transition)) throw new ArgumentOutOfRangeException(nameof(transition));
            Kind = kind; Key = key; Transition = transition;
        }
        public NativeWorldLoadKind Kind { get; }
        public string? Key { get; }
        public WorldLoadTransition Transition { get; }
        public static NativeWorldLoadRequest Scene(string sceneName, WorldLoadTransition transition) =>
            new NativeWorldLoadRequest(NativeWorldLoadKind.Scene, Required(sceneName), transition);
        public static NativeWorldLoadRequest Curated(string sourceKey, WorldLoadTransition transition) =>
            new NativeWorldLoadRequest(NativeWorldLoadKind.Curated, Required(sourceKey), transition);
        public static NativeWorldLoadRequest OpenSandbox(WorldLoadTransition transition) =>
            new NativeWorldLoadRequest(NativeWorldLoadKind.OpenSandbox, null, transition);
        private static string Required(string value) => string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A native world source is required.") : value;
    }

    internal interface IInternalWorldPreparation : IDisposable
    {
        WorldSceneIdentity Scene { get; }
        TransformState? NativeDefaultSpawn { get; }
        Task<OperationResult<WorldReadiness>> FinishAsync(WorldSpawnPolicy policy, IEntity? contentRoot,
            TransformState? providerSpawn, CancellationToken cancellationToken);
    }
}
