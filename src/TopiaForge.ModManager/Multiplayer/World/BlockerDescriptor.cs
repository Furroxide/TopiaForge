using System;
using System.Buffers.Binary;
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

    /// <summary>Available before any mod runs on a joined player; no bundle or provider registration is required.</summary>
    internal sealed class BlockerReplicaFactory : IInternalReplicaFactory
    {
        private readonly IBlockerBackend backend;
        internal BlockerReplicaFactory(IBlockerBackend backend) { this.backend = backend; }

        public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor)
        {
            if (descriptor.Kind != BlockerDescriptor.Kind ||
                !BlockerDescriptor.TryRead(descriptor.Payload.Span, out var size, out var blocksSight))
                return OperationResult<IEntity>.Failure(ModErrorCode.InvalidArgument,
                    "The host sent an invalid blocker. Leave and join again; if it repeats, update TopiaForge.");
            var result = backend.CreateBlocker(BlockerRequest.Box(descriptor.Transform, size, blocksSight));
            return result.TryGetValue(out var blocker) ? OperationResult<IEntity>.Success(blocker) :
                OperationResult<IEntity>.Failure(result.ErrorCode, result.ErrorMessage);
        }

        public void ReleaseReplica(IEntity entity) { (entity as IBlocker)?.Dispose(); }
    }
}
