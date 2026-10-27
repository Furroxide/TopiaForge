using System;
using TopiaForge.Mods.Internal;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace TopiaForge.Worlds.Maps
{
    /// <summary>Content-addressed cache. Every read verifies bytes; neither index text nor request text supplies a path.</summary>
    internal sealed class MapCache
    {
        internal const long MaximumBytes = 256L * 1024 * 1024;
        private readonly string root;
        private readonly long maximum;
        private readonly Func<DateTime> utcNow;
        internal MapCache(string root, long maximum = MaximumBytes, Func<DateTime>? utcNow = null)
        {
            this.root = Path.GetFullPath(root);
            this.maximum = maximum > 0 && maximum <= MaximumBytes ? maximum : throw new ArgumentOutOfRangeException(nameof(maximum));
            this.utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        internal CachedMap? Read(string id)
        {
            RequireId(id);
            try
            {
                var record = ReadRecord(id);
                if (record == null) return null;
                var path = Blob(record.Sha256);
                var bytes = ReadOrdinary(path, MapDocument.MaximumPackedBytes);
                if (MapDocument.Hash(bytes) != record.Sha256) { Remove(id); return null; }
                File.SetLastWriteTimeUtc(path, utcNow());
                return new CachedMap(bytes, record);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException || error is SerializationException)
            { return null; }
        }

        internal void Put(string id, MapDocument document, string? etag, string? modified)
        {
            RequireId(id);
            if (document.Packed.LongLength > maximum) return;
            CheckAncestry(root);
            Directory.CreateDirectory(root);
            CheckAncestry(root);
            Atomic(Blob(document.Sha256), document.Packed);
            var record = new MapCacheRecord
            {
                MapId = id,
                Sha256 = document.Sha256,
                Etag = SafeValidator(etag),
                LastModified = SafeValidator(modified),
                SavedUtc = utcNow().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            };
            using var output = new MemoryStream();
            new DataContractJsonSerializer(typeof(MapCacheRecord)).WriteObject(output, record);
            Atomic(Index(id), output.ToArray());
            DeleteOrdinary(Tombstone(id));
            Trim(document.Sha256);
        }

        internal void Remove(string id)
        {
            RequireId(id);
            CheckAncestry(root);
            var record = ReadReference(id);
            // Invalidate before unlinking. A deletion failure must not leave an
            // offline-playable index when this process restarts.
            if (!Directory.Exists(root)) return;
            Atomic(Tombstone(id), Array.Empty<byte>());
            DeleteOrdinary(Index(id));
            if (record != null && !Referenced(record.Sha256)) DeleteOrdinary(Blob(record.Sha256));
            DeleteOrdinary(Tombstone(id));
        }

        private MapCacheRecord? ReadRecord(string id)
        {
            if (File.Exists(Tombstone(id))) return null;
            var path = Index(id);
            if (!File.Exists(path)) return null;
            var json = MapJson.Read(ReadOrdinary(path, 4096));
            var record = new MapCacheRecord
            {
                MapId = MapDocument.Text(json, "mapId", 64),
                Sha256 = MapDocument.Text(json, "sha256", 64),
                Etag = SafeValidator(MapDocument.Text(json, "etag", 1024)),
                LastModified = SafeValidator(MapDocument.Text(json, "lastModified", 1024)),
                SavedUtc = MapDocument.Text(json, "savedUtc", 128),
            };
            return record.MapId == id && IsHash(record.Sha256) ? record : null;
        }
        private void Trim(string retained)
        {
            var blobs = new DirectoryInfo(root).EnumerateFiles("*.map", SearchOption.TopDirectoryOnly)
                .Where(f => IsHash(Path.GetFileNameWithoutExtension(f.Name)) && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(f => f.LastWriteTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).ToList();
            var total = blobs.Sum(f => f.Length);
            foreach (var file in blobs)
            {
                if (total <= maximum) break;
                var hash = Path.GetFileNameWithoutExtension(file.Name);
                if (hash == retained) continue;
                total -= file.Length;
                DeleteOrdinary(file.FullName);
                foreach (var id in IndexIds())
                    if (ReadReference(id)?.Sha256 == hash) DeleteOrdinary(Index(id));
            }
            // Bound small metadata too, including records whose files were removed outside the cache.
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(4096))
                if (file.Name.StartsWith("id-", StringComparison.Ordinal) && MapDocument.IsId(Path.GetFileNameWithoutExtension(file.Name).Substring(3), 64)) DeleteOrdinary(file.FullName);
        }
        private bool Referenced(string hash) => IndexIds().Any(id => ReadReference(id)?.Sha256 == hash);
        private MapCacheRecord? ReadReference(string id)
        {
            try { return ReadRecord(id); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException) { return null; }
        }
        private IEnumerable<string> IndexIds()
        {
            if (!Directory.Exists(root)) yield break;
            foreach (var path in Directory.EnumerateFiles(root, "id-*.json", SearchOption.TopDirectoryOnly))
            {
                string id;
                try { id = MapDocument.Text(MapJson.Read(ReadOrdinary(path, 4096)), "mapId", 64); }
                catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException) { continue; }
                if (MapDocument.IsId(id, 64) && Path.GetFileName(path) == Path.GetFileName(Index(id))) yield return id;
            }
        }

        internal static byte[] ReadOrdinary(string path, int maximum)
        {
            CheckAncestry(path);
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length <= 0 || input.Length > maximum) throw new IOException("The map file is empty or exceeds its size limit.");
            using var output = new MemoryStream();
            var buffer = new byte[16384];
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                if (output.Length + count > maximum) throw new IOException("The map file exceeds its size limit.");
                output.Write(buffer, 0, count);
            }
            return output.ToArray();
        }
        internal static void CheckAncestry(string path)
        {
            for (var current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
            {
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The map cache or file uses a linked path. Choose an ordinary folder.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }
        private static void Atomic(string path, byte[] bytes)
        {
            CheckAncestry(path);
            var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                CheckAncestry(path);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        private static void DeleteOrdinary(string path)
        {
            CheckAncestry(path);
            if (File.Exists(path)) File.Delete(path);
        }
        private static string? SafeValidator(string? value) => !string.IsNullOrEmpty(value) && value!.Length <= 1024 &&
            !value.Any(c => c < ' ' || c == 127) ? value : null;
        private static bool IsHash(string value) => value.Length == 64 && value.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
        private string Blob(string hash) => Path.Combine(root, hash + ".map");
        // Published ids are case-sensitive even on case-insensitive filesystems.
        // Records also retain the exact id; ambiguous legacy indexes are never replayed.
        private static string Key(string id) => MapDocument.Hash(Encoding.UTF8.GetBytes(id));
        private string Index(string id) => Path.Combine(root, "id-" + Key(id) + ".json");
        private string Tombstone(string id) => Path.Combine(root, "id-" + Key(id) + ".gone");
        private static void RequireId(string id)
        { if (!MapDocument.IsId(id, 64)) throw new ArgumentException("A published map id is required."); }
    }

    [DataContract]
    internal sealed class MapCacheRecord
    {
        [DataMember(Name = "mapId")] internal string MapId = string.Empty;
        [DataMember(Name = "sha256")] internal string Sha256 = string.Empty;
        [DataMember(Name = "etag")] internal string? Etag;
        [DataMember(Name = "lastModified")] internal string? LastModified;
        [DataMember(Name = "savedUtc")] internal string SavedUtc = string.Empty;
    }
    internal sealed class CachedMap
    {
        internal CachedMap(byte[] bytes, MapCacheRecord record) { Bytes = bytes; Record = record; }
        internal byte[] Bytes { get; }
        internal MapCacheRecord Record { get; }
    }
}
