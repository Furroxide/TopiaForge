using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>The provider's persistent data folder, through the SDK's file service, as the network layer reads and writes it.</summary>
    internal sealed class ModDataFiles : INetworkDataFiles
    {
        private readonly IModFiles files;

        internal ModDataFiles(IModFiles files)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
        }

        public async Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken)
        {
            if (!files.DataFileExists(relativePath)) return null;
            var result = await files.ReadDataBytesAsync(relativePath, cancellationToken).ConfigureAwait(false);
            if (result.TryGetValue(out var bytes)) return bytes;
            if (result.ErrorCode == ModErrorCode.NotFound) return null;
            throw new InvalidOperationException("A multiplayer data file could not be read: " + result.ErrorMessage);
        }

        public async Task WriteAsync(string relativePath, byte[] content, CancellationToken cancellationToken)
        {
            var result = await files.WriteDataBytesAsync(relativePath, content, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded) throw new InvalidOperationException("A multiplayer data file could not be written: " + result.ErrorMessage);
        }
    }
}
