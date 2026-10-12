using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>A canonical outcome as it travels from host to client: response bytes, or an error code and message.</summary>
    internal readonly struct WireOutcome
    {
        private WireOutcome(bool succeeded, byte[]? payload, ModErrorCode code, string message)
        {
            Succeeded = succeeded;
            Payload = payload;
            Code = code;
            Message = message;
        }

        internal bool Succeeded { get; }
        internal byte[]? Payload { get; }
        internal ModErrorCode Code { get; }
        internal string Message { get; }

        internal static WireOutcome Success(byte[] payload) => new WireOutcome(true, payload, ModErrorCode.None, string.Empty);

        internal static WireOutcome Failure(ModErrorCode code, string message) =>
            new WireOutcome(false, null, code == ModErrorCode.None ? ModErrorCode.Unknown : code, message ?? string.Empty);
    }

    internal sealed class SubmissionMessage
    {
        internal SubmissionMessage(ulong epoch, ulong sequence, string target, string wireId, byte[] payload)
        {
            Epoch = epoch;
            Sequence = sequence;
            Target = target;
            WireId = wireId;
            Payload = payload;
        }

        internal ulong Epoch { get; }
        internal ulong Sequence { get; }

        /// <summary>Gets the owner mod id for a command, or the object id for an object input.</summary>
        internal string Target { get; }

        /// <summary>Gets the command id, or an empty string for an object input.</summary>
        internal string WireId { get; }

        internal byte[] Payload { get; }
    }

    internal sealed class SubmissionResultMessage
    {
        internal SubmissionResultMessage(ulong sequence, ulong confirmedAt, WireOutcome outcome)
        {
            Sequence = sequence;
            ConfirmedAt = confirmedAt;
            Outcome = outcome;
        }

        internal ulong Sequence { get; }
        internal ulong ConfirmedAt { get; }
        internal WireOutcome Outcome { get; }
    }

    internal sealed class ObjectWireMessage
    {
        internal string Owner = string.Empty;
        internal string TypeId = string.Empty;
        internal string ObjectId = string.Empty;
        internal string? OwnerParticipant;
        internal ulong Version;
        internal byte[] Bytes = Array.Empty<byte>();
    }

    /// <summary>Encoders and decoders for submissions, canonical results, replicated values and events.</summary>
    internal static class ReplicationMessages
    {
        internal static void WriteCommand(
            ProtocolWriter frame,
            ProtocolWriter body,
            InternWriter intern,
            ulong epoch,
            ulong sequence,
            string owner,
            string commandId,
            byte[] payload)
        {
            body.Reset();
            body.WriteVarUInt(epoch);
            body.WriteVarUInt(sequence);
            intern.Write(body, owner);
            intern.Write(body, commandId);
            body.WriteBytes(payload);
            frame.WriteEnvelope(MessageType.Command, body);
        }

        internal static SubmissionMessage ReadCommand(ProtocolReader reader, InternReader intern) =>
            new SubmissionMessage(
                reader.ReadVarUInt(),
                reader.ReadVarUInt(),
                intern.Read(reader),
                intern.Read(reader),
                reader.ReadBytes(ProtocolLimits.MaximumPayloadBytes));

        internal static void WriteObjectInput(
            ProtocolWriter frame,
            ProtocolWriter body,
            ulong epoch,
            ulong sequence,
            string objectId,
            byte[] payload)
        {
            body.Reset();
            body.WriteVarUInt(epoch);
            body.WriteVarUInt(sequence);
            body.WriteString(objectId);
            body.WriteBytes(payload);
            frame.WriteEnvelope(MessageType.ObjectInput, body);
        }

        internal static SubmissionMessage ReadObjectInput(ProtocolReader reader) =>
            new SubmissionMessage(
                reader.ReadVarUInt(),
                reader.ReadVarUInt(),
                reader.ReadIdentity(),
                string.Empty,
                reader.ReadBytes(ProtocolLimits.MaximumPayloadBytes));

        internal static void WriteResult(
            ProtocolWriter frame,
            ProtocolWriter body,
            byte type,
            ulong sequence,
            ulong confirmedAt,
            WireOutcome outcome)
        {
            body.Reset();
            body.WriteVarUInt(sequence);
            body.WriteVarUInt(confirmedAt);
            body.WriteBool(outcome.Succeeded);
            if (outcome.Succeeded)
            {
                body.WriteBytes(outcome.Payload!);
            }
            else
            {
                body.WriteVarUInt((ulong)outcome.Code);
                body.WriteString(ProtocolLimits.BoundMessage(outcome.Message));
            }

            frame.WriteEnvelope(type, body);
        }

        internal static SubmissionResultMessage ReadResult(ProtocolReader reader)
        {
            var sequence = reader.ReadVarUInt();
            var confirmedAt = reader.ReadVarUInt();
            if (reader.ReadBool())
            {
                return new SubmissionResultMessage(
                    sequence,
                    confirmedAt,
                    WireOutcome.Success(reader.ReadBytes(ProtocolLimits.MaximumPayloadBytes)));
            }

            var rawCode = reader.ReadVarUInt(int.MaxValue);
            var code = (ModErrorCode)(int)rawCode;
            if (code == ModErrorCode.None || !Enum.IsDefined(typeof(ModErrorCode), code)) code = ModErrorCode.Unknown;
            return new SubmissionResultMessage(
                sequence,
                confirmedAt,
                WireOutcome.Failure(code, reader.ReadString(ProtocolLimits.MessageCharacters)));
        }

        internal static void WriteStateDelta(
            ProtocolWriter frame,
            ProtocolWriter body,
            InternWriter intern,
            string owner,
            string stateId,
            ulong version,
            byte[] bytes)
        {
            body.Reset();
            intern.Write(body, owner);
            intern.Write(body, stateId);
            body.WriteVarUInt(version);
            body.WriteBytes(bytes);
            frame.WriteEnvelope(MessageType.StateDelta, body);
        }

        internal static void ReadStateDelta(
            ProtocolReader reader,
            InternReader intern,
            out string owner,
            out string stateId,
            out ulong version,
            out byte[] bytes)
        {
            owner = intern.Read(reader);
            stateId = intern.Read(reader);
            version = reader.ReadVarUInt();
            bytes = reader.ReadBytes(ProtocolLimits.MaximumPayloadBytes);
        }

        internal static void WriteObjectSpawned(
            ProtocolWriter frame,
            ProtocolWriter body,
            InternWriter intern,
            ObjectWireMessage message)
        {
            body.Reset();
            intern.Write(body, message.Owner);
            intern.Write(body, message.TypeId);
            body.WriteString(message.ObjectId);
            body.WriteOptionalString(message.OwnerParticipant);
            body.WriteVarUInt(message.Version);
            body.WriteBytes(message.Bytes);
            frame.WriteEnvelope(MessageType.ObjectSpawned, body);
        }

        internal static ObjectWireMessage ReadObjectSpawned(ProtocolReader reader, InternReader intern) =>
            new ObjectWireMessage
            {
                Owner = intern.Read(reader),
                TypeId = intern.Read(reader),
                ObjectId = reader.ReadIdentity(),
                OwnerParticipant = reader.ReadOptionalIdentity(),
                Version = reader.ReadVarUInt(),
                Bytes = reader.ReadBytes(ProtocolLimits.MaximumPayloadBytes)
            };

        internal static void WriteObjectChanged(ProtocolWriter frame, ProtocolWriter body, ObjectWireMessage message)
        {
            body.Reset();
            body.WriteString(message.ObjectId);
            body.WriteOptionalString(message.OwnerParticipant);
            body.WriteVarUInt(message.Version);
            body.WriteBytes(message.Bytes);
            frame.WriteEnvelope(MessageType.ObjectChanged, body);
        }

        internal static ObjectWireMessage ReadObjectChanged(ProtocolReader reader) =>
            new ObjectWireMessage
            {
                ObjectId = reader.ReadIdentity(),
                OwnerParticipant = reader.ReadOptionalIdentity(),
                Version = reader.ReadVarUInt(),
                Bytes = reader.ReadBytes(ProtocolLimits.MaximumPayloadBytes)
            };

        internal static void WriteObjectDespawned(ProtocolWriter frame, ProtocolWriter body, string objectId, ulong version, byte[] bytes)
        {
            body.Reset();
            body.WriteString(objectId);
            body.WriteVarUInt(version);
            body.WriteBytes(bytes);
            frame.WriteEnvelope(MessageType.ObjectDespawned, body);
        }

        internal static ObjectWireMessage ReadObjectDespawned(ProtocolReader reader) =>
            new ObjectWireMessage
            {
                ObjectId = reader.ReadIdentity(),
                Version = reader.ReadVarUInt(),
                Bytes = reader.ReadBytes(ProtocolLimits.MaximumPayloadBytes)
            };

        internal static void WritePresentation(
            ProtocolWriter frame,
            ProtocolWriter body,
            InternWriter intern,
            string owner,
            string eventId,
            byte[] payload)
        {
            body.Reset();
            intern.Write(body, owner);
            intern.Write(body, eventId);
            body.WriteBytes(payload);
            frame.WriteEnvelope(MessageType.PresentationEvent, body);
        }

        internal static void ReadPresentation(
            ProtocolReader reader,
            InternReader intern,
            out string owner,
            out string eventId,
            out byte[] payload)
        {
            owner = intern.Read(reader);
            eventId = intern.Read(reader);
            payload = reader.ReadBytes(ProtocolLimits.MaximumPayloadBytes);
        }
    }
}
