using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    internal static class MotionMessages
    {
        internal const byte Grab = 67;
        internal const byte Sample = 68;
        internal const byte Release = 69;
        internal const byte Handback = 70;
        internal const byte Verdict = 98;
        internal const byte State = 99;
    }

    /// <summary>One bounded message, reused by the receiver. Sender identity comes only from the channel.</summary>
    internal sealed class MotionMessage
    {
        internal MotionTarget Target;
        internal uint Request;
        internal uint Epoch;
        internal int Slot;
        internal OwnershipState State;
        internal OwnershipResult Result;
        internal NetPose Pose;
        internal NetVector3 AngularVelocity;
        internal NetVector3 PlayerPosition;
        internal long TimeMicros;
    }

    internal static class MotionWire
    {
        internal static void Write(WorldWireWriter writer, byte type, MotionMessage value)
        {
            writer.Begin(type);
            writer.WriteByte(value.Target.IsScene ? (byte)1 : (byte)0);
            if (value.Target.IsScene) writer.WriteUInt64(value.Target.SceneKey.Value);
            else writer.WriteVarUInt(value.Target.Id.Value);
            writer.WriteVarUInt(value.Request);
            writer.WriteVarUInt(value.Epoch);
            if (type == MotionMessages.Grab)
            {
                WriteVector(writer, value.PlayerPosition);
                return;
            }

            if (type == MotionMessages.Verdict || type == MotionMessages.State)
            {
                writer.WriteVarUInt((uint)value.Slot);
                writer.WriteByte((byte)value.State);
                writer.WriteByte((byte)value.Result);
                writer.WriteVarUInt((ulong)value.TimeMicros);
            }
            else WriteVector(writer, value.PlayerPosition);
            writer.WriteTransform(value.Pose);
            WriteVector(writer, value.Pose.HasVelocity ? value.Pose.Velocity : NetVector3.Zero);
            WriteVector(writer, value.AngularVelocity);
        }

        internal static bool Read(WorldWireReader reader, byte type, MotionMessage value)
        {
            if (type != MotionMessages.Grab && type != MotionMessages.Sample && type != MotionMessages.Release &&
                type != MotionMessages.Handback && type != MotionMessages.Verdict && type != MotionMessages.State) return false;
            if (!reader.TryReadByte(out var targetKind) || targetKind > 1) return false;
            if (targetKind == 1)
            {
                if (!reader.TryReadUInt64(out var key)) return false;
                value.Target = new MotionTarget(new SceneObjectKey(key));
            }
            else
            {
                if (!reader.TryReadUInt32(uint.MaxValue, out var id) || !NetworkEntityId.TryCreate(id, out var networkId)) return false;
                value.Target = new MotionTarget(networkId);
            }

            if (!reader.TryReadUInt32(uint.MaxValue, out value.Request) || !reader.TryReadUInt32(uint.MaxValue, out value.Epoch)) return false;
            if (type == MotionMessages.Grab)
                return value.Request != 0 && ReadVector(reader, out value.PlayerPosition) && MotionRules.IsValid(value.PlayerPosition) && reader.AtEnd;
            if (value.Target.IsScene && type == MotionMessages.State) return false;
            if (type == MotionMessages.Verdict || type == MotionMessages.State)
            {
                if (!reader.TryReadInt32(MotionRules.HostSlot, out value.Slot) || !reader.TryReadByte(out var state) ||
                    (state != 0 && state != 2 && state != 3) || !reader.TryReadByte(out var result) || result > (byte)OwnershipResult.Yielding ||
                    !reader.TryReadVarUInt((ulong)WorldMessages.MaximumTimeMicros, out var time)) return false;
                value.State = (OwnershipState)state;
                value.Result = (OwnershipResult)result;
                value.TimeMicros = (long)time;
                if ((value.Slot == 0) != (value.State == OwnershipState.Server)) return false;
            }
            else if (!ReadVector(reader, out value.PlayerPosition) || !MotionRules.IsValid(value.PlayerPosition)) return false;
            if (!reader.TryReadTransform(out var pose) || !ReadVector(reader, out var velocity) ||
                !ReadVector(reader, out value.AngularVelocity) || !MotionRules.IsValid(pose.Position) ||
                !MotionRules.IsValidVelocity(velocity) || !MotionRules.IsValidVelocity(value.AngularVelocity)) return false;
            value.Pose = new NetPose(pose.Position, pose.Rotation, velocity);
            return reader.AtEnd;
        }

        private static void WriteVector(WorldWireWriter writer, NetVector3 vector)
        {
            writer.WriteSingle(vector.X);
            writer.WriteSingle(vector.Y);
            writer.WriteSingle(vector.Z);
        }

        private static bool ReadVector(WorldWireReader reader, out NetVector3 vector)
        {
            vector = default;
            if (!reader.TryReadSingle(out var x) || !reader.TryReadSingle(out var y) || !reader.TryReadSingle(out var z)) return false;
            vector = new NetVector3(x, y, z);
            return vector.IsFinite;
        }
    }
}
