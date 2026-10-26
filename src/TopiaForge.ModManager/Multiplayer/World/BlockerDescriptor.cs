using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>The runtime's box description: shape byte, three little-endian floats, flags byte.</summary>
    internal static class BlockerDescriptor
    {
        internal const string Kind = "asset.blocker";
        internal const int Length = 14;

        internal static byte[] Encode(BlockerRequest request)
        {
            var bytes = new byte[Length];
            bytes[0] = 1;
            Write(bytes.AsSpan(1), request.Size.X);
            Write(bytes.AsSpan(5), request.Size.Y);
            Write(bytes.AsSpan(9), request.Size.Z);
            bytes[13] = request.BlocksSight ? (byte)1 : (byte)0;
            return bytes;
        }

        internal static bool TryRead(ReadOnlySpan<byte> bytes, out Vec3 size, out bool blocksSight)
        {
            size = default;
            blocksSight = false;
            if (bytes.Length != Length || bytes[0] != 1 || bytes[13] > 1) return false;
            size = new Vec3(Read(bytes.Slice(1)), Read(bytes.Slice(5)), Read(bytes.Slice(9)));
            blocksSight = bytes[13] == 1;
            return BlockerRequest.ValidSide(size.X) && BlockerRequest.ValidSide(size.Y) && BlockerRequest.ValidSide(size.Z);
        }

        private static void Write(Span<byte> bytes, float value) => BinaryPrimitives.WriteInt32LittleEndian(bytes, BitConverter.SingleToInt32Bits(value));
        private static float Read(ReadOnlySpan<byte> bytes) => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes));
    }

    /// <summary>Engine creation only; the calling context or network world owns the returned handle.</summary>
    internal interface IBlockerBackend
    {
        OperationResult<IBlocker> CreateBlocker(BlockerRequest request);
    }

    /// <summary>
    /// Available before any mod runs on a joined player; no bundle or provider registration is required. Each replica it
    /// builds is listed by id until it is released, so no tool on that player can hold it.
    /// </summary>
    internal sealed class BlockerReplicaFactory : IInternalReplicaFactory
    {
        private readonly IBlockerBackend backend;
        private readonly Dictionary<string, IBlocker> built;

        internal BlockerReplicaFactory(IBlockerBackend backend, Dictionary<string, IBlocker> built)
        {
            this.backend = backend;
            this.built = built;
        }

        public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor)
        {
            if (descriptor.Kind != BlockerDescriptor.Kind ||
                !BlockerDescriptor.TryRead(descriptor.Payload.Span, out var size, out var blocksSight))
                return OperationResult<IEntity>.Failure(ModErrorCode.InvalidArgument,
                    "The host sent an invalid blocker. Leave and join again; if it repeats, update TopiaForge.");
            var result = backend.CreateBlocker(BlockerRequest.Box(descriptor.Transform, size, blocksSight));
            if (!result.TryGetValue(out var blocker) || blocker == null) return OperationResult<IEntity>.Failure(result.ErrorCode, result.ErrorMessage);
            built[blocker.Id] = blocker;
            return OperationResult<IEntity>.Success(blocker);
        }

        public void ReleaseReplica(IEntity entity)
        {
            if (!(entity is IBlocker blocker)) return;
            if (built.TryGetValue(blocker.Id, out var listed) && ReferenceEquals(listed, blocker)) built.Remove(blocker.Id);
            blocker.Dispose();
        }
    }
}
