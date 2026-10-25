using System;
using TopiaForge.ModManager;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.TestDoubles
{
    /// <summary>Game services for player API tests; health changes feed the real presence sampler.</summary>
    internal sealed class PlayersTestServices : IDisposable
    {
        internal readonly FakeModLifetime Lifetime = new FakeModLifetime();
        internal readonly FakeUiService Ui;
        internal readonly FakeInputService Input;
        internal readonly PlayerContext Context;
        internal readonly PlayerDirectory Directory;
        internal PlayersTestServices(MultiplayerPlayers runtime, LoopbackControl control, FakePresenceBackend backend)
        {
            Ui = new FakeUiService(Lifetime);
            Input = new FakeInputService(Lifetime);
            var activity = new ModInputActivity();
            var tracked = new NetInputService(Input, activity, Lifetime);
            tracked.RegisterAction(new InputActionDefinition("fire", "Fire", new[] { InputBinding.Key("K") }));
            Context = runtime.Bind("players-test", Lifetime, new Local(backend), Ui, activity);
            Directory = new PlayerDirectory(() => control.SessionSnapshot, () => 0, Context);
            Context.Directory = Directory;
        }
        internal IPlayer Player(ParticipantId id)
        {
            if (!Directory.TryGet(id, out var player)) throw new InvalidOperationException("The test player is absent.");
            return player!;
        }
        public void Dispose() { Directory.Dispose(); Lifetime.Dispose(); }

        private sealed class Local : ILocalPlayerService
        {
            private readonly FakePresenceBackend backend;
            private PlayerHealthSnapshot? health;
            private PlayerSnapshot? snapshot;
            internal Local(FakePresenceBackend backend) { this.backend = backend; }
            public bool TryGetSnapshot(out PlayerSnapshot? result)
            {
                var feet = backend.Feet; var position = new Vec3(feet.X, feet.Y, feet.Z);
                if (snapshot == null || !snapshot.Position.Equals(position)) snapshot = new PlayerSnapshot(position, new Ray(position, new Vec3(0, 0, 1)));
                result = backend.HasPlayer ? snapshot : null; return result != null;
            }
            public bool TryGetHealth(out PlayerHealthSnapshot? result)
            {
                if (health == null || health.Current != backend.Health || health.Maximum != backend.MaximumHealth) health = new PlayerHealthSnapshot(backend.Health, backend.MaximumHealth);
                result = backend.HasHealth ? health : null; return result != null;
            }
            public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request) { backend.Health = Math.Max(0, backend.Health - request.Amount); TryGetHealth(out var result); return OperationResult<PlayerHealthSnapshot>.Success(result!); }
            public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source) { backend.Health = Math.Min(backend.MaximumHealth, backend.Health + amount); TryGetHealth(out var result); return OperationResult<PlayerHealthSnapshot>.Success(result!); }
            public OperationResult<IPlayerControlLease> AcquireControl(string reason) => OperationResult<IPlayerControlLease>.Failure(ModErrorCode.Unavailable, "No controller in this test.");
        }
    }
}
