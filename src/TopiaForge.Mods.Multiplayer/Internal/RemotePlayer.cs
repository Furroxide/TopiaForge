using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>A permanently bound player incarnation. All retained Input and Ui references share its lifetime check.</summary>
    internal sealed class RemotePlayer : IPlayer, IPlayerInput, IPlayerUi, IViewControls
    {
        private readonly PlayerDirectory directory;
        private readonly IInternalPlayerBackend? backend;
        internal RemotePlayer(PlayerDirectory directory, MultiplayerParticipant record, IInternalPlayerBackend? backend)
        { this.directory = directory; Record = record; this.backend = backend; Id = record.Id; Key = record.PlayerKey; }
        internal MultiplayerParticipant Record { get; set; }
        internal bool Present { get; set; } = true;
        public ParticipantId Id { get; }
        public MultiplayerPlayerKey Key { get; }
        public string DisplayName { get { directory.Refresh(); return Record.DisplayName; } }
        public bool IsPresent { get { directory.Refresh(); return Present; } }
        public bool IsLocal => IsPresent && Record.IsLocal;
        public bool IsHost => IsPresent && Record.IsHost;
        public IEntity? Entity => IsPresent ? backend?.Entity(Id) : null;
        public IViewControls View { get { directory.CheckThread(); return this; } }
        public OperationResult<IViewLease> Environment(string preset) => IsPresent && backend != null ? backend.View(Id).Environment(preset) : Fail<IViewLease>();
        public OperationResult<IViewLease> Fog(VisionFog fog) => IsPresent && backend != null ? backend.View(Id).Fog(fog) : Fail<IViewLease>();
        public OperationResult<IViewLease> Hide(IEntity entity) => IsPresent && backend != null ? backend.View(Id).Hide(entity) : Fail<IViewLease>();
        public IPlayerInput Input { get { directory.CheckThread(); return this; } }
        public IPlayerUi Ui { get { directory.CheckThread(); return this; } }
        public bool TryGetSnapshot(out PlayerSnapshot? snapshot)
        {
            snapshot = null;
            return IsPresent && backend != null && backend.Snapshot(Id, out snapshot);
        }
        public bool TryGetHealth(out PlayerHealthSnapshot? health)
        {
            health = null;
            return IsPresent && backend != null && backend.Health(Id, out health);
        }
        public OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request) =>
            IsPresent && backend != null ? backend.Damage(Id, request) : Fail<PlayerHealthSnapshot>();
        public OperationResult<PlayerHealthSnapshot> Heal(float amount, string source) =>
            IsPresent && backend != null ? backend.Heal(Id, amount, source) : Fail<PlayerHealthSnapshot>();
        public OperationResult<bool> Teleport(Vec3 position, float? yawDegrees = null) =>
            IsPresent && backend != null ? backend.Teleport(Id, position, yawDegrees) : Fail<bool>();
        public bool IsHeld(string actionName) => IsPresent && backend != null && backend.Input(Id).IsHeld(actionName);
        public bool WasPressed(string actionName) => IsPresent && backend != null && backend.Input(Id).WasPressed(actionName);
        public bool WasReleased(string actionName) => IsPresent && backend != null && backend.Input(Id).WasReleased(actionName);
        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) =>
            IsPresent && backend != null ? backend.Ui(Id).ShowToast(message, tone) : Fail<bool>();
        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) =>
            IsPresent && backend != null ? backend.Ui(Id).ShowModal(request, completed) : Fail<IUiModal>();
        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) =>
            IsPresent && backend != null ? backend.Ui(Id).CreateSurface(request) : Fail<IUiSurface>();
        private OperationResult<T> Fail<T>() where T : notnull => IsPresent
            ? PlayerDirectory.Unavailable<T>() : OperationResult<T>.Failure(ModErrorCode.NotFound, "The player has left the session.");
    }
}
