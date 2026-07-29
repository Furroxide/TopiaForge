using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext
    {
        private sealed class ModFiles : IModFiles
        {
            private const int MaximumBytes = 16 * 1024 * 1024;
            private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
            private readonly string packageRoot;
            private readonly string dataRoot;
            private readonly IModLifetime lifetime;

            public ModFiles(string packageRoot, string dataRoot, IModLifetime lifetime)
            {
                this.packageRoot = packageRoot;
                this.dataRoot = dataRoot;
                this.lifetime = lifetime;
            }

            public bool PackageFileExists(string relativePath) => TryResolve(packageRoot, relativePath, out var path) && File.Exists(path);
            public bool DataFileExists(string relativePath) => TryResolve(dataRoot, relativePath, out var path) && File.Exists(path);

            public Task<OperationResult<byte[]>> ReadPackageBytesAsync(string relativePath, CancellationToken cancellationToken = default)
                => ReadBytesAsync(packageRoot, relativePath, cancellationToken);

            public Task<OperationResult<byte[]>> ReadDataBytesAsync(string relativePath, CancellationToken cancellationToken = default)
                => ReadBytesAsync(dataRoot, relativePath, cancellationToken);

            public async Task<OperationResult<string>> ReadPackageTextAsync(string relativePath, CancellationToken cancellationToken = default)
                => Decode(await ReadPackageBytesAsync(relativePath, cancellationToken).ConfigureAwait(false));

            public async Task<OperationResult<string>> ReadDataTextAsync(string relativePath, CancellationToken cancellationToken = default)
                => Decode(await ReadDataBytesAsync(relativePath, cancellationToken).ConfigureAwait(false));

            public Task<OperationResult<bool>> WriteDataBytesAsync(
                string relativePath,
                byte[] content,
                CancellationToken cancellationToken = default)
            {
                if (content == null) throw new ArgumentNullException(nameof(content));
                if (content.Length > MaximumBytes)
                {
                    return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "File content exceeds 16 MiB."));
                }

                var copy = (byte[])content.Clone();
                return RunAsync(dataRoot, relativePath, cancellationToken, (path, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    AtomicWrite(path, copy);
                    return OperationResult<bool>.Success(true);
                });
            }

            public Task<OperationResult<bool>> WriteDataTextAsync(
                string relativePath,
                string content,
                CancellationToken cancellationToken = default)
            {
                if (content == null) throw new ArgumentNullException(nameof(content));
                return WriteDataBytesAsync(relativePath, StrictUtf8.GetBytes(content), cancellationToken);
            }

            public Task<OperationResult<bool>> DeleteDataFileAsync(string relativePath, CancellationToken cancellationToken = default)
            {
                return RunAsync(dataRoot, relativePath, cancellationToken, (path, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    if (File.Exists(path)) File.Delete(path);
                    return OperationResult<bool>.Success(true);
                });
            }

            private Task<OperationResult<byte[]>> ReadBytesAsync(string root, string relativePath, CancellationToken cancellationToken)
            {
                return RunAsync(root, relativePath, cancellationToken, (path, token) =>
                {
                    if (!File.Exists(path))
                    {
                        return OperationResult<byte[]>.Failure(ModErrorCode.NotFound, "File '" + relativePath + "' was not found.");
                    }

                    token.ThrowIfCancellationRequested();
                    var info = new FileInfo(path);
                    if (info.Length > MaximumBytes)
                    {
                        return OperationResult<byte[]>.Failure(ModErrorCode.Io, "File exceeds the 16 MiB SDK limit.");
                    }

                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        return OperationResult<byte[]>.Failure(
                            ModErrorCode.Io,
                            "The requested file must be a regular file and cannot be a symbolic link.");
                    }

                    using (var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        81920,
                        FileOptions.SequentialScan))
                    {
                        if (stream.Length > MaximumBytes)
                        {
                            return OperationResult<byte[]>.Failure(ModErrorCode.Io, "File exceeds the 16 MiB SDK limit.");
                        }

                        var expectedLength = checked((int)stream.Length);
                        var bytes = new byte[expectedLength];
                        var total = 0;
                        while (total < bytes.Length)
                        {
                            token.ThrowIfCancellationRequested();
                            var read = stream.Read(bytes, total, bytes.Length - total);
                            if (read == 0) break;
                            total += read;
                        }

                        token.ThrowIfCancellationRequested();
                        if (stream.ReadByte() >= 0)
                        {
                            return OperationResult<byte[]>.Failure(ModErrorCode.Io, "File grew while it was being read.");
                        }

                        if (total != bytes.Length)
                        {
                            Array.Resize(ref bytes, total);
                        }

                        return OperationResult<byte[]>.Success(bytes);
                    }
                });
            }

            private async Task<OperationResult<T>> RunAsync<T>(
                string root,
                string relativePath,
                CancellationToken callerToken,
                Func<string, CancellationToken, OperationResult<T>> operation) where T : notnull
            {
                if (!TryResolve(root, relativePath, out var path))
                {
                    return OperationResult<T>.Failure(ModErrorCode.InvalidArgument, "The path must be a safe relative child path.");
                }

                var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.StoppingToken, callerToken);
                IDisposable tracking;
                try
                {
                    tracking = lifetime.Track(linked);
                }
                catch (ObjectDisposedException)
                {
                    linked.Dispose();
                    return OperationResult<T>.Failure(ModErrorCode.Cancelled, "The mod is stopping.");
                }

                try
                {
                    return await Task.Run(() => operation(path, linked.Token), linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return OperationResult<T>.Failure(ModErrorCode.Cancelled, "The file operation was cancelled.");
                }
                catch (Exception exception)
                {
                    return OperationResult<T>.Failure(ModErrorCode.Io, exception.Message);
                }
                finally
                {
                    tracking.Dispose();
                }
            }

            private static OperationResult<string> Decode(OperationResult<byte[]> bytes)
            {
                if (!bytes.TryGetValue(out var value))
                {
                    return OperationResult<string>.Failure(bytes.ErrorCode, bytes.ErrorMessage);
                }

                try
                {
                    return OperationResult<string>.Success(StrictUtf8.GetString(value));
                }
                catch (DecoderFallbackException exception)
                {
                    return OperationResult<string>.Failure(ModErrorCode.Io, "File is not strict UTF-8: " + exception.Message);
                }
            }

            private static bool TryResolve(string root, string relativePath, out string path)
            {
                try
                {
                    path = PathSafety.CombineRelativeChild(root, relativePath);
                    return !string.IsNullOrWhiteSpace(relativePath);
                }
                catch
                {
                    path = string.Empty;
                    return false;
                }
            }

            private static void AtomicWrite(string path, byte[] content)
            {
                var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Data file has no parent directory.");
                Directory.CreateDirectory(directory);
                var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        stream.Write(content, 0, content.Length);
                        stream.Flush(true);
                    }

                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }
    }
}
