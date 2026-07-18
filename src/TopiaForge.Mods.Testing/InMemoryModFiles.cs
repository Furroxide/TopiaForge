using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>A deterministic in-memory byte store used by fake package and persistent data operations.</summary>
    public sealed class InMemoryModFileSystem
    {
        private readonly Dictionary<string, byte[]> packageFiles =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private readonly Dictionary<string, byte[]> dataFiles =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        /// <summary>Gets package-relative paths in ordinal order.</summary>
        public IReadOnlyList<string> PackageFiles => SortedKeys(packageFiles);

        /// <summary>Gets data-relative paths in ordinal order.</summary>
        public IReadOnlyList<string> DataFiles => SortedKeys(dataFiles);

        /// <summary>Seeds UTF-8 package content for a test.</summary>
        public void SetPackageText(string relativePath, string content) =>
            SetPackageBytes(relativePath, Encoding.UTF8.GetBytes(content ?? string.Empty));

        /// <summary>Seeds package bytes for a test.</summary>
        public void SetPackageBytes(string relativePath, byte[] content) =>
            Set(packageFiles, relativePath, content);

        /// <summary>Seeds UTF-8 persistent data content for a test.</summary>
        public void SetDataText(string relativePath, string content) =>
            SetDataBytes(relativePath, Encoding.UTF8.GetBytes(content ?? string.Empty));

        /// <summary>Seeds persistent data bytes for a test.</summary>
        public void SetDataBytes(string relativePath, byte[] content) =>
            Set(dataFiles, relativePath, content);

        internal bool PackageExists(string relativePath) => packageFiles.ContainsKey(Normalize(relativePath));
        internal bool DataExists(string relativePath) => dataFiles.ContainsKey(Normalize(relativePath));
        internal bool TryReadPackage(string relativePath, out byte[] content) =>
            TryRead(packageFiles, relativePath, out content);
        internal bool TryReadData(string relativePath, out byte[] content) =>
            TryRead(dataFiles, relativePath, out content);
        internal void WriteData(string relativePath, byte[] content) => Set(dataFiles, relativePath, content);
        internal bool DeleteData(string relativePath) => dataFiles.Remove(Normalize(relativePath));

        internal static string Normalize(string relativePath)
        {
            if (relativePath == null)
            {
                throw new ArgumentNullException(nameof(relativePath));
            }

            var value = relativePath.Replace('\\', '/').Trim();
            if (value.Length == 0 || value.StartsWith("/", StringComparison.Ordinal) || value.IndexOf(':') >= 0)
            {
                throw new ArgumentException("A safe, non-empty relative path is required.", nameof(relativePath));
            }

            foreach (var segment in value.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                {
                    throw new ArgumentException("The relative path contains an unsafe segment.", nameof(relativePath));
                }
            }

            return value;
        }

        private static IReadOnlyList<string> SortedKeys(Dictionary<string, byte[]> source)
        {
            var values = new List<string>(source.Keys);
            values.Sort(StringComparer.Ordinal);
            return values.AsReadOnly();
        }

        private static void Set(Dictionary<string, byte[]> target, string relativePath, byte[] content)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            target[Normalize(relativePath)] = (byte[])content.Clone();
        }

        private static bool TryRead(
            Dictionary<string, byte[]> source,
            string relativePath,
            out byte[] content)
        {
            if (source.TryGetValue(Normalize(relativePath), out var stored))
            {
                content = (byte[])stored.Clone();
                return true;
            }

            content = Array.Empty<byte>();
            return false;
        }
    }

    /// <summary>Provides bounded package and persistent data content without exposing filesystem paths.</summary>
    public sealed class InMemoryModFiles : IModFiles
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly InMemoryModFileSystem fileSystem;
        private readonly FakeModLifetime lifetime;

        /// <summary>Creates in-memory content operations.</summary>
        public InMemoryModFiles(InMemoryModFileSystem fileSystem, FakeModLifetime lifetime)
        {
            this.fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets or sets the maximum content size accepted by reads and writes.</summary>
        public int MaximumFileBytes { get; set; } = 16 * 1024 * 1024;

        /// <inheritdoc/>
        public bool PackageFileExists(string relativePath) => fileSystem.PackageExists(relativePath);

        /// <inheritdoc/>
        public bool DataFileExists(string relativePath) => fileSystem.DataExists(relativePath);

        /// <inheritdoc/>
        public Task<OperationResult<byte[]>> ReadPackageBytesAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            ReadBytesAsync(relativePath, package: true, cancellationToken);

        /// <inheritdoc/>
        public Task<OperationResult<byte[]>> ReadDataBytesAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            ReadBytesAsync(relativePath, package: false, cancellationToken);

        /// <inheritdoc/>
        public async Task<OperationResult<string>> ReadPackageTextAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Decode(await ReadPackageBytesAsync(relativePath, cancellationToken).ConfigureAwait(false));

        /// <inheritdoc/>
        public async Task<OperationResult<string>> ReadDataTextAsync(
            string relativePath,
            CancellationToken cancellationToken = default) =>
            Decode(await ReadDataBytesAsync(relativePath, cancellationToken).ConfigureAwait(false));

        /// <inheritdoc/>
        public Task<OperationResult<bool>> WriteDataBytesAsync(
            string relativePath,
            byte[] content,
            CancellationToken cancellationToken = default)
        {
            InMemoryModFileSystem.Normalize(relativePath);
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            if (IsCancelled(cancellationToken))
            {
                return Cancelled<bool>();
            }

            if (content.Length > MaximumFileBytes)
            {
                return Task.FromResult(OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "Content exceeds the fake file-size limit."));
            }

            fileSystem.WriteData(relativePath, content);
            return Task.FromResult(OperationResult<bool>.Success(true));
        }

        /// <inheritdoc/>
        public Task<OperationResult<bool>> WriteDataTextAsync(
            string relativePath,
            string content,
            CancellationToken cancellationToken = default)
        {
            if (content == null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            return WriteDataBytesAsync(relativePath, StrictUtf8.GetBytes(content), cancellationToken);
        }

        /// <inheritdoc/>
        public Task<OperationResult<bool>> DeleteDataFileAsync(
            string relativePath,
            CancellationToken cancellationToken = default)
        {
            InMemoryModFileSystem.Normalize(relativePath);
            if (IsCancelled(cancellationToken))
            {
                return Cancelled<bool>();
            }

            return Task.FromResult(OperationResult<bool>.Success(fileSystem.DeleteData(relativePath)));
        }

        private Task<OperationResult<byte[]>> ReadBytesAsync(
            string relativePath,
            bool package,
            CancellationToken cancellationToken)
        {
            InMemoryModFileSystem.Normalize(relativePath);
            if (IsCancelled(cancellationToken))
            {
                return Cancelled<byte[]>();
            }

            var found = package
                ? fileSystem.TryReadPackage(relativePath, out var content)
                : fileSystem.TryReadData(relativePath, out content);
            if (!found)
            {
                return Task.FromResult(OperationResult<byte[]>.Failure(
                    ModErrorCode.NotFound,
                    "The in-memory file does not exist."));
            }

            if (content.Length > MaximumFileBytes)
            {
                return Task.FromResult(OperationResult<byte[]>.Failure(
                    ModErrorCode.Io,
                    "Content exceeds the fake file-size limit."));
            }

            return Task.FromResult(OperationResult<byte[]>.Success(content));
        }

        private static OperationResult<string> Decode(OperationResult<byte[]> bytes)
        {
            if (!bytes.TryGetValue(out var content))
            {
                return OperationResult<string>.Failure(bytes.ErrorCode, bytes.ErrorMessage);
            }

            try
            {
                return OperationResult<string>.Success(StrictUtf8.GetString(content));
            }
            catch (DecoderFallbackException)
            {
                return OperationResult<string>.Failure(ModErrorCode.Io, "Content is not strict UTF-8.");
            }
        }

        private bool IsCancelled(CancellationToken caller) =>
            caller.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested;

        private static Task<OperationResult<T>> Cancelled<T>() where T : notnull =>
            Task.FromResult(OperationResult<T>.Failure(
                ModErrorCode.Cancelled,
                "The in-memory file operation was cancelled."));
    }

    /// <summary>Provides typed in-memory key/value persistence.</summary>
    public sealed class InMemoryModStorageService : IModStorageService
    {
        private readonly Dictionary<string, object> values =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> storyFlags =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        /// <summary>Gets the number of stored keys.</summary>
        public int Count => values.Count + storyFlags.Count;

        /// <inheritdoc/>
        public bool Contains(string key) => values.ContainsKey(ValidateKey(key));

        /// <inheritdoc/>
        public OperationResult<T> Load<T>(string key) where T : class
        {
            var normalized = ValidateKey(key);
            if (!values.TryGetValue(normalized, out var value))
            {
                return OperationResult<T>.Failure(ModErrorCode.NotFound, "The storage key does not exist.");
            }

            if (!(value is T typed))
            {
                return OperationResult<T>.Failure(ModErrorCode.InvalidState, "The stored value has a different type.");
            }

            return OperationResult<T>.Success(typed);
        }

        /// <inheritdoc/>
        public OperationResult<bool> Save<T>(string key, T value) where T : class
        {
            values[ValidateKey(key)] = value ?? throw new ArgumentNullException(nameof(value));
            return OperationResult<bool>.Success(true);
        }

        /// <inheritdoc/>
        public OperationResult<bool> Delete(string key) =>
            OperationResult<bool>.Success(values.Remove(ValidateKey(key)));

        /// <inheritdoc/>
        public bool TryGetStoryFlag(string key, out bool value) =>
            storyFlags.TryGetValue(ValidateKey(key), out value);

        /// <inheritdoc/>
        public OperationResult<bool> SetStoryFlag(string key, bool value)
        {
            storyFlags[ValidateKey(key)] = value;
            return OperationResult<bool>.Success(true);
        }

        /// <inheritdoc/>
        public OperationResult<bool> DeleteStoryFlag(string key) =>
            OperationResult<bool>.Success(storyFlags.Remove(ValidateKey(key)));

        /// <summary>Removes every stored key.</summary>
        public void Clear()
        {
            values.Clear();
            storyFlags.Clear();
        }

        private static string ValidateKey(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                throw new ArgumentException("A storage key is required.", nameof(key));
            }

            return key;
        }
    }
}
