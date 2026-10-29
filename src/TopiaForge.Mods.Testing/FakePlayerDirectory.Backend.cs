using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Testing
{
    public sealed partial class FakePlayerDirectory
    {
        // Keep the runtime bridge out of the fake's public SDK surface.
        private sealed class Backend : IInternalPlayerBackend
        {
            private readonly FakePlayerDirectory owner;
            internal Backend(FakePlayerDirectory owner) { this.owner = owner; }
            public IEntity? Entity(ParticipantId id) => owner.BackendEntity(id);
            public bool Snapshot(ParticipantId id, out PlayerSnapshot? value) => owner.BackendSnapshot(id, out value);
            public bool Health(ParticipantId id, out PlayerHealthSnapshot? value) => owner.BackendHealth(id, out value);
            public OperationResult<PlayerHealthSnapshot> Damage(ParticipantId id, PlayerDamageRequest request) => owner.BackendDamage(id, request);
            public OperationResult<PlayerHealthSnapshot> Heal(ParticipantId id, float amount, string source) => owner.BackendHeal(id, amount, source);
            public OperationResult<bool> Teleport(ParticipantId id, Vec3 position, float? yaw) => owner.BackendTeleport(id, position, yaw);
            public IPlayerInput Input(ParticipantId id) => owner.BackendInput(id);
            public IViewControls View(ParticipantId id) => owner.WorldView.ForPlayer(id, owner.lifetime, owner.IsAuthoritative);
            public IPlayerUi Ui(ParticipantId id) => owner.BackendUi(id);
            public OperationResult<IScoreboardColumn> Column(ScoreboardColumnDefinition definition) => owner.BackendColumn(definition);
        }
    }
}
