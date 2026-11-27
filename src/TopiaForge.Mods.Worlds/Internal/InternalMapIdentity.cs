using System;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Internal
{
    // Runtime-owned evidence, never an author-supplied revision or download endpoint.
    internal sealed class InternalMapIdentity
    {
        internal const int MaximumBytes = 8 * 1024 * 1024;
        internal const int MaximumTransferBytes = 4 * 1024 * 1024;
        internal InternalMapIdentity(string mapId, string sceneId, string sha256, int size, string modified)
        {
            if (!IsId(mapId, 64) || !IsId(sceneId, 128) || sha256 == null || sha256.Length != 64
                || size <= 0 || size > MaximumBytes || modified == null || modified.Length > 128)
                throw new ArgumentException("Invalid map content identity.");
            foreach (var c in sha256)
                if (!(c >= '0' && c <= '9') && !(c >= 'a' && c <= 'f')) throw new ArgumentException("Invalid map digest.");
            foreach (var c in modified)
                if (char.IsControl(c)) throw new ArgumentException("Invalid map revision.");
            MapId = mapId; SceneId = sceneId; Sha256 = sha256; Size = size; Modified = modified;
        }
        internal string MapId { get; }
        internal string SceneId { get; }
        internal string Sha256 { get; }
        internal int Size { get; }
        internal string Modified { get; }
        internal bool Matches(byte[] bytes)
        {
            if (bytes.Length != Size) return false;
            using var sha = SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant() == Sha256;
        }
        internal static bool IsId(string? value, int maximum)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > maximum) return false;
            foreach (var c in value)
                if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '_' && c != '-') return false;
            return true;
        }
    }

    internal delegate Task<OperationResult<byte[]>> InternalMapCopy(CancellationToken token, IProgress<long>? progress);

    internal interface IInternalMapFollowContext
    {
        InternalMapIdentity? ExpectedMap { get; }
        InternalMapCopy? ReadHostMap { get; }
        Func<OperationResult<bool>>? BeginMapHost { get; }
    }
}
