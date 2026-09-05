using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

// The real owner-facade source is compiled against this recording backing service so its
// admission/event behavior is executable without loading Unity. No production binding is replaced.
namespace TopiaForge.Worlds
{
    public sealed partial class WorldsService : IOwnerBoundExtensionFactory
    {
        internal int Calls;
        public IReadOnlyList<WorldDefinition> Worlds => Array.Empty<WorldDefinition>();
        public IReadOnlyList<GamemodeDefinition> Gamemodes => Array.Empty<GamemodeDefinition>();
        public IReadOnlyList<GamemodeMenuEntry> MenuEntries => Array.Empty<GamemodeMenuEntry>();
        public WorldSession? CurrentSession => null;
        public event Action<WorldSession>? SessionChanged;
        public event Action<WorldSessionEnd>? SessionEnded;
        internal void Raise() { SessionChanged?.Invoke(null!); SessionEnded?.Invoke(null!); }
        public OperationResult<IWorldRegistration> RegisterWorld(WorldDefinition world, ICustomWorldContent? content = null) => Call<IWorldRegistration>();
        public OperationResult<IWorldRegistration> RegisterGamemode(GamemodeDefinition mode) => Call<IWorldRegistration>();
        public OperationResult<IWorldRegistration> RegisterMenuEntry(GamemodeMenuEntry entry) => Call<IWorldRegistration>();
        public OperationResult<IDisposable> RegisterAssetOverride(WorldAssetOverride value) => Call<IDisposable>();
        public OperationResult<bool> EndSession(WorldSessionEndReason reason) => Call<bool>();
        public OperationResult<bool> LoadLocalWorld(string path) => Call<bool>();
        public OperationResult<IReadOnlyList<LocalWorldFile>> ListLocalWorlds() => Call<IReadOnlyList<LocalWorldFile>>();
        public Task<OperationResult<WorldSession>> LoadAsync(WorldLoadRequest request, CancellationToken token) => Task.FromResult(Call<WorldSession>());
        public Task<OperationResult<WorldSession>> LaunchMenuEntryAsync(string id, CancellationToken token) => Task.FromResult(Call<WorldSession>());
        private OperationResult<T> Call<T>() where T : notnull
        { Calls++; return OperationResult<T>.Failure(ModErrorCode.Unavailable, "recording backend"); }
    }
}
