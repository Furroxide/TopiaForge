using System;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Xml;

namespace Robotopia.ModManager.Core
{
    public static class JsonUtil
    {
        public const long MaxPersistedFileBytes = 4L * 1024 * 1024;
        public const string BackupSuffix = ".bak";
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(
            encoderShouldEmitUTF8Identifier: false,
            throwOnInvalidBytes: true);

        public static T LoadFile<T>(string path, T fallback)
        {
            if (!File.Exists(path))
            {
                return fallback;
            }

            return ReadBoundedFile<T>(path);
        }

        /// <summary>
        /// Loads trusted manager state/configuration, falling back to the previous atomically replaced
        /// document when the primary file is missing, truncated, oversized, or otherwise unreadable.
        /// Package manifests intentionally use <see cref="LoadFile{T}"/> so an archive cannot supply a
        /// second manifest through the persistence-backup mechanism.
        /// </summary>
        public static T LoadPersistentFile<T>(string path, T fallback)
        {
            return LoadPersistent(path, fallback, ReadBoundedFile<T>, IsRecoverableReadFailure);
        }

        /// <summary>
        /// Reads one bounded strict JSON object, recovering from the atomic-write backup when the primary content
        /// is malformed or oversized. Transient I/O and permission failures surface to the caller so a save cannot
        /// overwrite a temporarily unreadable primary. The original text is retained for forward-field merging.
        /// </summary>
        public static string LoadPersistentJsonObject(string path, string fallback)
        {
            JsonObjectMerge.ValidateObject(fallback);
            return LoadPersistent(path, fallback, ReadBoundedJsonObject, IsJsonObjectContentFailure);
        }

        private static T LoadPersistent<T>(
            string path,
            T fallback,
            Func<string, T> reader,
            Func<Exception, bool> isRecoverable)
        {
            var fullPath = Path.GetFullPath(path);
            var backupPath = fullPath + BackupSuffix;
            Exception? primaryError = null;
            if (File.Exists(fullPath))
            {
                try
                {
                    return reader(fullPath);
                }
                catch (Exception ex) when (isRecoverable(ex))
                {
                    primaryError = ex;
                }
            }

            if (File.Exists(backupPath))
            {
                try
                {
                    return reader(backupPath);
                }
                catch (Exception backupError) when (isRecoverable(backupError))
                {
                    if (primaryError != null)
                    {
                        throw new InvalidDataException(
                            "Both the primary JSON file and its backup are unreadable: " + fullPath,
                            new AggregateException(primaryError, backupError));
                    }

                    throw new InvalidDataException(
                        "The JSON backup is unreadable: " + backupPath,
                        backupError);
                }
            }

            if (primaryError != null)
            {
                throw new InvalidDataException(
                    "The JSON file is unreadable and no valid backup exists: " + fullPath,
                    primaryError);
            }

            return fallback;
        }

        public static void SaveFile<T>(string path, T value)
        {
            SaveFileCore(path, stream => Serialize(stream, value), preserveValidJsonBackup: false);
        }

        /// <summary>
        /// Atomically persists one already-serialized strict JSON object with the standard size bound, without
        /// rotating malformed/oversized primary content over a potentially valid recovery backup.
        /// </summary>
        public static void SaveJsonObject(string path, string json)
        {
            if (json == null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            var byteCount = StrictUtf8.GetByteCount(json);
            if (byteCount > MaxPersistedFileBytes)
            {
                throw new InvalidDataException(
                    "Serialized JSON exceeds the " + MaxPersistedFileBytes + " byte limit.");
            }

            JsonObjectMerge.ValidateObject(json);
            SaveFileCore(path, stream =>
            {
                var bytes = StrictUtf8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
            }, preserveValidJsonBackup: true);
        }

        /// <summary>Serializes to a string while enforcing the persistence bound during serialization.</summary>
        public static string SerializeBounded<T>(T value)
        {
            using (var buffer = new MemoryStream())
            using (var bounded = new SizeLimitedWriteStream(buffer, MaxPersistedFileBytes))
            {
                Serialize(bounded, value);
                bounded.Flush();
                return StrictUtf8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            }
        }

        private static void SaveFileCore(
            string path,
            Action<Stream> write,
            bool preserveValidJsonBackup)
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                throw new InvalidOperationException("JSON file path has no parent directory: " + path);
            }

            Directory.CreateDirectory(directory);
            var tempPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(
                    tempPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.WriteThrough))
                using (var bounded = new SizeLimitedWriteStream(stream, MaxPersistedFileBytes))
                {
                    write(bounded);
                    bounded.Flush();
                    stream.Flush(flushToDisk: true);
                }

                if (File.Exists(fullPath))
                {
                    if (preserveValidJsonBackup && ShouldSkipJsonBackupRotation(fullPath))
                    {
                        File.Replace(tempPath, fullPath, destinationBackupFileName: null);
                    }
                    else
                    {
                        File.Replace(tempPath, fullPath, fullPath + BackupSuffix);
                    }
                }
                else
                {
                    File.Move(tempPath, fullPath);
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Preserve the persistence error; a same-directory orphan cannot be mistaken for state.
                }
            }
        }

        public static T Deserialize<T>(string json)
        {
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                return Deserialize<T>(stream);
            }
        }

        public static T Deserialize<T>(Stream stream)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
            var value = serializer.ReadObject(stream);
            if (value == null)
            {
                throw new InvalidDataException("JSON document produced a null value.");
            }

            return (T)value;
        }

        public static string Serialize<T>(T value)
        {
            using (var stream = new MemoryStream())
            {
                Serialize(stream, value);
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public static void Serialize<T>(Stream stream, T value)
        {
            var serializer = new DataContractJsonSerializer(typeof(T), new DataContractJsonSerializerSettings
            {
                UseSimpleDictionaryFormat = true
            });
            serializer.WriteObject(stream, value);
        }

        public static T Clone<T>(T value)
        {
            return Deserialize<T>(Serialize(value));
        }

        private static T ReadBoundedFile<T>(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > MaxPersistedFileBytes)
                {
                    throw new InvalidDataException(
                        "JSON file exceeds the " + MaxPersistedFileBytes + " byte limit: " + path);
                }

                using (var buffer = new MemoryStream((int)input.Length))
                {
                    var chunk = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (total > MaxPersistedFileBytes - read)
                        {
                            throw new InvalidDataException(
                                "JSON file grew beyond the " + MaxPersistedFileBytes + " byte limit: " + path);
                        }

                        buffer.Write(chunk, 0, read);
                        total += read;
                    }

                    buffer.Position = 0;
                    return Deserialize<T>(buffer);
                }
            }
        }

        private static string ReadBoundedJsonObject(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length > MaxPersistedFileBytes)
                {
                    throw new InvalidDataException(
                        "JSON file exceeds the " + MaxPersistedFileBytes + " byte limit: " + path);
                }

                using (var buffer = new MemoryStream((int)input.Length))
                {
                    var chunk = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                    {
                        if (total > MaxPersistedFileBytes - read)
                        {
                            throw new InvalidDataException(
                                "JSON file grew beyond the " + MaxPersistedFileBytes + " byte limit: " + path);
                        }

                        buffer.Write(chunk, 0, read);
                        total += read;
                    }

                    var json = StrictUtf8.GetString(
                        buffer.GetBuffer(),
                        0,
                        checked((int)buffer.Length));
                    if (json.Length > 0 && json[0] == '\uFEFF')
                    {
                        json = json.Substring(1);
                    }

                    JsonObjectMerge.ValidateObject(json);
                    return json;
                }
            }
        }

        private static bool IsRecoverableReadFailure(Exception error)
        {
            return error is IOException ||
                   error is UnauthorizedAccessException ||
                   error is SerializationException ||
                   error is XmlException ||
                   error is FormatException ||
                   error is ArgumentException;
        }

        private static bool IsJsonObjectContentFailure(Exception error)
        {
            return error is InvalidDataException
                || error is SerializationException
                || error is XmlException
                || error is FormatException
                || error is DecoderFallbackException
                || error is ArgumentException;
        }

        private static bool ShouldSkipJsonBackupRotation(string fullPath)
        {
            try
            {
                ReadBoundedJsonObject(fullPath);
                return false;
            }
            catch (Exception primaryError) when (IsJsonObjectContentFailure(primaryError))
            {
                // Never rotate malformed/oversized content over the last backup. With no backup this also avoids
                // retaining a known-unusable document as though it were a recovery candidate.
                return true;
            }
        }

        private sealed class SizeLimitedWriteStream : Stream
        {
            private readonly Stream inner;
            private readonly long maximumBytes;
            private long bytesWritten;

            public SizeLimitedWriteStream(Stream inner, long maximumBytes)
            {
                this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
                this.maximumBytes = maximumBytes;
            }

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => inner.Length;

            public override long Position
            {
                get => inner.Position;
                set => throw new NotSupportedException();
            }

            public override void Flush()
            {
                inner.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                if (count < 0 || bytesWritten > maximumBytes - count)
                {
                    throw new InvalidDataException(
                        "Serialized JSON exceeds the " + maximumBytes + " byte limit.");
                }

                inner.Write(buffer, offset, count);
                bytesWritten += count;
            }

            public override void WriteByte(byte value)
            {
                if (bytesWritten >= maximumBytes)
                {
                    throw new InvalidDataException(
                        "Serialized JSON exceeds the " + maximumBytes + " byte limit.");
                }

                inner.WriteByte(value);
                bytesWritten++;
            }

            protected override void Dispose(bool disposing)
            {
                // The caller owns and durably flushes the underlying FileStream.
                base.Dispose(disposing);
            }
        }
    }
}
