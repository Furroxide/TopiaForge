using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext
    {
        // Reuse the parent's synchronized persistent store, but never let a stale child mutate it.
        private sealed class ScopedConfigService : IModConfigService
        {
            private readonly IModConfigService parent;
            private readonly IModLifetime lifetime;
            internal ScopedConfigService(IModConfigService parent, IModLifetime lifetime)
            { this.parent = parent; this.lifetime = lifetime; }
            public OperationResult<T> Load<T>(ConfigDefinition<T> definition) where T : class => lifetime.IsStopping
                ? OperationResult<T>.Failure(ModErrorCode.Cancelled, "The context is stopping.") : parent.Load(definition);
            public OperationResult<bool> Save<T>(ConfigDefinition<T> definition, T value) where T : class => lifetime.IsStopping
                ? OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The context is stopping.") : parent.Save(definition, value);
            public OperationResult<T> Reset<T>(ConfigDefinition<T> definition) where T : class => lifetime.IsStopping
                ? OperationResult<T>.Failure(ModErrorCode.Cancelled, "The context is stopping.") : parent.Reset(definition);
        }
        private sealed class ScopedStorageService : ILocalModStorageService
        {
            private readonly ILocalModStorageService parent;
            private readonly IModLifetime lifetime;
            internal ScopedStorageService(ILocalModStorageService parent, IModLifetime lifetime)
            { this.parent = parent; this.lifetime = lifetime; }
            public bool Contains(string key) => !lifetime.IsStopping && parent.Contains(key);
            public OperationResult<T> Load<T>(string key) where T : class => lifetime.IsStopping
                ? OperationResult<T>.Failure(ModErrorCode.Cancelled, "The context is stopping.") : parent.Load<T>(key);
            public OperationResult<bool> Save<T>(string key, T value) where T : class => lifetime.IsStopping
                ? OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The context is stopping.") : parent.Save(key, value);
            public OperationResult<bool> Delete(string key) => lifetime.IsStopping
                ? OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The context is stopping.") : parent.Delete(key);
        }
    }
}
