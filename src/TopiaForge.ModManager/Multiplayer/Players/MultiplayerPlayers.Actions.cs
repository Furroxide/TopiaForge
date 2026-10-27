using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class MultiplayerPlayers
    {
        internal OperationResult<PlayerHealthSnapshot> ChangeHealth(PlayerContext context, ParticipantId id, float amount, string source, bool heal)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!Finite(amount) || amount <= 0 || amount > Presence.Policy.MaximumHealth || string.IsNullOrWhiteSpace(source) || source.Length > 128)
                return OperationResult<PlayerHealthSnapshot>.Failure(ModErrorCode.InvalidArgument, "Use a positive, bounded health amount and a short source label.");
            if (!Present(id) || context.Lifetime.IsStopping) return Gone<PlayerHealthSnapshot>();
            if (!IsHost && !IsLocal(id)) return Authority<PlayerHealthSnapshot>();
            if (IsLocal(id)) return heal ? context.Local.Heal(amount, source) : context.Local.Damage(new PlayerDamageRequest(amount, source));
            if (!context.Networked) return Unshared<PlayerHealthSnapshot>();
            if (!context.Health(id, out var health)) return PlayerDirectory.Unavailable<PlayerHealthSnapshot>();
            BeginAction(context, id, heal ? (byte)2 : (byte)1);
            Writer.WriteSingle(amount);
            Writer.WriteString(source);
            var sent = Send(id);
            if (!sent.Succeeded) return OperationResult<PlayerHealthSnapshot>.Failure(sent.ErrorCode, sent.ErrorMessage);
            // The allowance covers only the health the heal can add; the rest would wait, unspent, for a later false report.
            if (heal && Presence.TryGetPeer(id, out var peer)) peer!.HealthCheck?.AllowGain(Math.Min(amount, health!.Maximum - health.Current));
            return OperationResult<PlayerHealthSnapshot>.Success(new PlayerHealthSnapshot(health!.Current + (heal ? amount : -amount), health.Maximum));
        }

        internal OperationResult<bool> Teleport(PlayerContext context, ParticipantId id, Vec3 position, float? yaw)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (!ValidPosition(position) || (yaw.HasValue && (!Finite(yaw.Value) || Math.Abs(yaw.Value) > 360000)))
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Use a finite position inside the world's bounds and a finite facing angle.");
            if (!Present(id) || context.Lifetime.IsStopping) return Gone<bool>();
            if (!IsHost && !IsLocal(id)) return Authority<bool>();
            var feet = new NetVector3(position.X, position.Y, position.Z);
            if (IsLocal(id)) return MoveLocal(feet, yaw);
            if (!context.Networked) return Unshared<bool>();
            BeginAction(context, id, 3);
            Writer.WriteSingle(position.X); Writer.WriteSingle(position.Y); Writer.WriteSingle(position.Z);
            Writer.WriteByte(yaw.HasValue ? (byte)1 : (byte)0); Writer.WriteSingle(yaw ?? 0);
            var sent = Send(id);
            if (sent.Succeeded && Presence.TryGetPeer(id, out var peer)) Presence.Host.ExpectPlacement(peer!, NetQuantization.QuantizePosition(feet), Now);
            return sent;
        }

        private void BeginAction(PlayerContext context, ParticipantId id, byte action)
        {
            Begin(2, context.Mod);
            Writer.WriteString(id.Value);
            Writer.WriteByte(action);
        }

        private bool ReceiveAction(PlayerContext context, ParticipantId sender, WorldWireReader r)
        {
            // A client never asks the host to apply player actions: only the host may send this frame.
            if (!FromHost(sender)) return false;
            if (!r.TryReadString(128, out var target) || Snapshot?.LocalParticipantId?.Value != target || !r.TryReadByte(out var action)) return false;
            if (action == 1 || action == 2)
            {
                if (!r.TryReadSingle(out var amount) || !Finite(amount) || amount <= 0 || amount > Presence.Policy.MaximumHealth ||
                    !r.TryReadString(512, out var source) || string.IsNullOrWhiteSpace(source) || source.Length > 128 || !r.AtEnd) return false;
                if (action == 1) context.Local.Damage(new PlayerDamageRequest(amount, source));
                else context.Local.Heal(amount, source);
                return true;
            }
            if (action != 3 || !r.TryReadSingle(out var x) || !r.TryReadSingle(out var y) || !r.TryReadSingle(out var z) ||
                !r.TryReadByte(out var facing) || facing > 1 || !r.TryReadSingle(out var yaw) || !Finite(yaw) || Math.Abs(yaw) > 360000 || !r.AtEnd) return false;
            var position = new Vec3(x, y, z);
            if (!ValidPosition(position)) return false;
            MoveLocal(new NetVector3(x, y, z), facing == 1 ? yaw : (float?)null);
            return true;
        }

        private OperationResult<bool> MoveLocal(NetVector3 feet, float? yaw)
        {
            // Presence is not told to expect this jump. The host's expected placement takes the landing pose whether or not it
            // is flagged as a teleport, and an expectation that a move shorter than a jump never spends would stay armed and
            // hide the player's next teleport of its own, which the host would then correct as speed.
            if (!Presence.Backend.TeleportLocalPlayer(feet, yaw ?? 0, !yaw.HasValue)) return PlayerDirectory.Unavailable<bool>();
            return OperationResult<bool>.Success(true);
        }

        private bool ValidPosition(Vec3 value)
        {
            if (!Finite(value.X) || !Finite(value.Y) || !Finite(value.Z) || Math.Abs(value.X) > 1_000_000 || Math.Abs(value.Y) > 1_000_000 || Math.Abs(value.Z) > 1_000_000) return false;
            return !Presence.Backend.TryGetWorldBounds(out var bounds) || bounds.Contains(NetQuantization.QuantizePosition(new NetVector3(value.X, value.Y, value.Z)));
        }
        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static OperationResult<T> Gone<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.NotFound, "The player or mod scope has left the session.");
        internal static OperationResult<T> Authority<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.NotAuthoritative, "Only the host can act on another player.");

        // Traffic names the mod, and another player's game has no copy of a client-local or server-only one to receive it.
        internal static OperationResult<T> Unshared<T>() where T : notnull => OperationResult<T>.Failure(ModErrorCode.Unavailable,
            "Other players do not run this client-local or server-only mod, so its actions and windows reach only this game's " +
            "player. To reach every player, make the mod shared: leave the multiplayer block out of its manifest, or use the session mode.");
    }
}
