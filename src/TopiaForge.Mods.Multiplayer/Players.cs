using System;
using System.Collections.Generic;

namespace TopiaForge.Mods
{
    /// <summary>
    /// The players in the current session, for code that treats each player individually. In single-player play the
    /// directory holds exactly the local player, so player-aware code is the same code with or without company.
    /// Members are main-thread only.
    /// </summary>
    public interface IPlayerDirectory
    {
        /// <summary>Gets the players currently present, in stable join order. A headless server is not a player.</summary>
        IReadOnlyList<IPlayer> Players { get; }

        /// <summary>Gets this process's player, or null on a headless server.</summary>
        IPlayer? Local { get; }

        /// <summary>Tries to get a present player by participant identity.</summary>
        bool TryGet(ParticipantId id, out IPlayer? player);

        /// <summary>Subscribes to players joining. Players already present are not replayed; read <see cref="Players"/> first.</summary>
        IDisposable SubscribeJoined(Action<IPlayer> handler);

        /// <summary>Subscribes to players leaving. The handle already reports <see cref="IPlayer.IsPresent"/> false.</summary>
        IDisposable SubscribeLeft(Action<IPlayer> handler);

        /// <summary>
        /// Registers a scoreboard column that the owning mod fills per player. Values set by the session authority are
        /// shown to everyone; values set elsewhere fail with <see cref="ModErrorCode.NotAuthoritative"/>.
        /// </summary>
        OperationResult<IScoreboardColumn> RegisterScoreboardColumn(ScoreboardColumnDefinition definition);
    }

    /// <summary>
    /// A main-thread handle to one player. Reads come from replicated state; actions are routed to the process that
    /// owns the player and confirmed through replication. After the player leaves, reads return false and actions
    /// fail with <see cref="ModErrorCode.NotFound"/>.
    /// </summary>
    public interface IPlayer
    {
        /// <summary>Gets the session-scoped participant identity.</summary>
        ParticipantId Id { get; }

        /// <summary>Gets the player's key, stable across sessions on this host.</summary>
        MultiplayerPlayerKey Key { get; }

        /// <summary>Gets the bounded player-facing name.</summary>
        string DisplayName { get; }

        /// <summary>Gets whether this player belongs to the current process.</summary>
        bool IsLocal { get; }

        /// <summary>Gets whether this player is hosting the session.</summary>
        bool IsHost { get; }

        /// <summary>Gets whether the player is still in the session.</summary>
        bool IsPresent { get; }

        /// <summary>Gets the player's avatar entity, usable wherever an entity target is accepted, or null while none is spawned.</summary>
        IEntity? Entity { get; }

        /// <summary>Tries to read the player's latest position and aim.</summary>
        bool TryGetSnapshot(out PlayerSnapshot? snapshot);

        /// <summary>Tries to read the player's latest health.</summary>
        bool TryGetHealth(out PlayerHealthSnapshot? health);

        /// <summary>
        /// Damages the player. The owning process applies it; for a remote player the returned health is the
        /// expected result, and the confirmed value arrives through <see cref="TryGetHealth"/>.
        /// </summary>
        OperationResult<PlayerHealthSnapshot> Damage(PlayerDamageRequest request);

        /// <summary>Heals the player. Confirmation follows the same rule as <see cref="Damage"/>.</summary>
        OperationResult<PlayerHealthSnapshot> Heal(float amount, string source);

        /// <summary>Moves the player, optionally facing <paramref name="yawDegrees"/>. The owning process applies it.</summary>
        OperationResult<bool> Teleport(Vec3 position, float? yawDegrees = null);

        /// <summary>Gets the player's replicated input.</summary>
        IPlayerInput Input { get; }

        /// <summary>Gets UI addressed to this player.</summary>
        IPlayerUi Ui { get; }

        /// <summary>Gets host-controlled changes delivered only to this player's view.</summary>
        [RequiresTopiaForge("0.2.0")]
        IViewControls View { get; }

        /// <summary>Gets host-controlled, scope-owned appearance, nameplate and cosmetic pose changes.</summary>
        [RequiresTopiaForge("0.2.0")]
        IPlayerAppearance Appearance { get; }
    }

    /// <summary>
    /// One player's input actions as the session authority sees them. Each process samples its own registered
    /// actions and replicates their edges, so press and release edges are reconstructed per canonical tick.
    /// Action names are those the owning mod registered through <see cref="IInputService.RegisterAction"/>.
    /// </summary>
    public interface IPlayerInput
    {
        /// <summary>Gets whether the action is held.</summary>
        bool IsHeld(string actionName);

        /// <summary>Gets whether the action was pressed during the current tick.</summary>
        bool WasPressed(string actionName);

        /// <summary>Gets whether the action was released during the current tick.</summary>
        bool WasReleased(string actionName);
    }

    /// <summary>
    /// UI addressed to one player. For the local player it is the ordinary UI service. For a remote player the request
    /// travels to that player's process; windows and modals stay interactive, and their callbacks run here after the
    /// response is checked against the player it was sent to.
    /// </summary>
    public interface IPlayerUi
    {
        /// <summary>Shows a toast to this player.</summary>
        OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral);

        /// <summary>Shows a modal to this player and reports their choice.</summary>
        OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed);

        /// <summary>Creates a HUD surface or window shown only to this player.</summary>
        OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request);
    }

    /// <summary>Declares one scoreboard column.</summary>
    public sealed class ScoreboardColumnDefinition
    {
        /// <summary>Gets the maximum id and title length.</summary>
        public const int MaximumTextLength = 64;

        /// <summary>Creates a scoreboard column declaration.</summary>
        /// <param name="id">A mod-local column id.</param>
        /// <param name="title">The player-facing column title.</param>
        /// <param name="order">Columns sort by ascending order, then by title.</param>
        public ScoreboardColumnDefinition(string id, string title, int order = 0)
        {
            Id = MultiplayerIdentityValidation.Require(id, nameof(id));
            if (Id.Length > MaximumTextLength) throw new ArgumentException("A column id must be at most 64 characters.", nameof(id));
            Title = MultiplayerIdentityValidation.RequireDisplayName(title, nameof(title));
            if (Title.Length > MaximumTextLength) throw new ArgumentException("A column title must be at most 64 characters.", nameof(title));
            Order = order;
        }

        /// <summary>Gets the mod-local column id.</summary>
        public string Id { get; }

        /// <summary>Gets the player-facing title.</summary>
        public string Title { get; }

        /// <summary>Gets the sort order.</summary>
        public int Order { get; }
    }

    /// <summary>A lifetime-owned scoreboard column registration.</summary>
    public interface IScoreboardColumn : IDisposable
    {
        /// <summary>Gets the mod-local column id.</summary>
        string Id { get; }

        /// <summary>Gets whether the column remains registered.</summary>
        bool IsActive { get; }

        /// <summary>
        /// Sets one player's value. <paramref name="displayValue"/> is at most 32 characters; the scoreboard sorts the
        /// column by <paramref name="sortKey"/>.
        /// </summary>
        OperationResult<bool> SetValue(ParticipantId player, string displayValue, long sortKey = 0);

        /// <summary>Clears one player's value.</summary>
        OperationResult<bool> Clear(ParticipantId player);
    }
}
