using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// What one scan of the packages folder may select for the player's own play, from the record of server content
    /// (<see cref="ServerContentRecord"/>). Exact pins, which is how every server profile launches, see every version.
    /// </summary>
    /// <remarks>
    /// <para>A version the record hides or withholds stays out of unpinned selection while its install receipt is the one
    /// the record names. A mod the record says a join added starts disabled when the scan finds it with no manager
    /// record, as the join recorded it.</para>
    /// <para>A record that is there but not valid fails closed (<see cref="Frozen"/>): unpinned selection takes only the
    /// version the manager state already names for each mod, and a mod with no manager record does not load. So damage,
    /// a newer launcher's record or a file that cannot be read never exposes a version the record withheld.</para>
    /// <para>The launcher replaces the record by moving it aside and moving the new one in, and Windows can stop it
    /// between the two moves. A record that is missing beside such a <c>server-content.json.&lt;token&gt;.bak</c> is
    /// read from the backups: what any of them withholds stays withheld.</para>
    /// </remarks>
    internal sealed class ServerContentSelection
    {
        private const int MaximumReceiptBytes = 4 * 1024 * 1024;
        private const string ReceiptFileName = "topiaforge.install.json";
        private static readonly Regex BackupName = new Regex(
            @"\Aserver-content\.json\.[0-9]+--?[0-9]+-[0-9]+\.bak\z", RegexOptions.CultureInvariant);

        private readonly IReadOnlyList<ServerContentRecord> records;
        private readonly Dictionary<string, string?> receipts = new Dictionary<string, string?>(StringComparer.Ordinal);

        private ServerContentSelection(IReadOnlyList<ServerContentRecord> records, ServerContentStatus status, string problem)
        {
            this.records = records;
            Status = status;
            Problem = problem;
        }

        /// <summary>Nothing installed for servers: every version is the player's own.</summary>
        public static ServerContentSelection None { get; } =
            new ServerContentSelection(Array.Empty<ServerContentRecord>(), ServerContentStatus.Missing, string.Empty);

        public ServerContentStatus Status { get; }

        /// <summary>Why the record failed closed, for the manager log; empty otherwise.</summary>
        public string Problem { get; }

        /// <summary>Whether the record could not be trusted, so that only the versions the manager state names load.</summary>
        public bool Frozen => Status != ServerContentStatus.Valid && Status != ServerContentStatus.Missing;

        public static ServerContentSelection For(ServerContentRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            return record.Status == ServerContentStatus.Missing
                ? None
                : new ServerContentSelection(new[] { record }, record.Status, record.Problem);
        }

        /// <summary>Reads <c>server-content.json</c> beside the manager state. Never throws for the record's sake.</summary>
        public static ServerContentSelection Load(ManagerPaths paths)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            try
            {
                return LoadRecords(paths);
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                return For(ServerContentRecord.Unusable(ServerContentStatus.Unreadable,
                    "The record of server content could not be read: " + error.Message));
            }
        }

        private static ServerContentSelection LoadRecords(ManagerPaths paths)
        {
            var file = Path.Combine(paths.Root, ServerContentRecord.FileName);
            var primary = ReadFile(file);
            if (primary.Status != ServerContentStatus.Missing) return For(primary);
            string[] backups;
            try
            {
                backups = Directory.Exists(paths.Root)
                    ? Directory.GetFiles(paths.Root, ServerContentRecord.FileName + ".*.bak", SearchOption.TopDirectoryOnly)
                        .Where(path => BackupName.IsMatch(Path.GetFileName(path)))
                        .OrderBy(path => path, StringComparer.Ordinal)
                        .ToArray()
                    : Array.Empty<string>();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return For(ServerContentRecord.Unusable(ServerContentStatus.Unreadable,
                    "The folder of the record of server content could not be listed: " + error.Message));
            }

            if (backups.Length == 0) return None;
            var read = backups.Select(ReadFile).ToList();
            var unusable = read.FirstOrDefault(record => record.Status != ServerContentStatus.Valid);
            return unusable != null
                ? For(unusable.Status == ServerContentStatus.Missing
                    ? ServerContentRecord.Unusable(ServerContentStatus.Unreadable, "A backup of the record of server content vanished while it was read.")
                    : unusable)
                : new ServerContentSelection(read, ServerContentStatus.Valid, string.Empty);
        }

        /// <summary>
        /// Whether unpinned selection must pass over the version folder <paramref name="packagePath"/>, version
        /// <paramref name="version"/> of the mod folder <paramref name="directoryId"/>.
        /// </summary>
        public bool Withholds(string directoryId, string version, string packagePath)
        {
            if (Status != ServerContentStatus.Valid || !records.Any(record => record.Names(directoryId, version))) return false;
            var receipt = ReceiptSha256(packagePath);
            return records.Any(record => record.Withholds(directoryId, version, receipt));
        }

        /// <summary>Whether a mod found with no manager record starts disabled, because a join added it.</summary>
        public bool StartsDisabled(string id) => records.Any(record => record.AddedRecordOf(id));

        private string? ReceiptSha256(string packagePath)
        {
            if (receipts.TryGetValue(packagePath, out var known)) return known;
            string? digest = null;
            try
            {
                var path = Path.Combine(packagePath, ReceiptFileName);
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0)
                {
                    var bytes = ReadBounded(path, MaximumReceiptBytes);
                    if (bytes != null)
                    {
                        using (var sha = SHA256.Create()) digest = Hex(sha.ComputeHash(bytes));
                    }
                }
            }
            catch (Exception error) when (!(error is OutOfMemoryException))
            {
                // A receipt that cannot be read withholds every version the record names, failing closed.
                digest = null;
            }

            receipts[packagePath] = digest;
            return digest;
        }

        private static ServerContentRecord ReadFile(string path)
        {
            try
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                    return ServerContentRecord.Unusable(ServerContentStatus.Damaged, "A link or a folder stands where the record of server content belongs.");
                var bytes = ReadBounded(path, ServerContentRecord.MaximumBytes);
                return bytes == null
                    ? ServerContentRecord.Unusable(ServerContentStatus.Damaged, "The record of server content is larger than its bound.")
                    : ServerContentRecord.Read(bytes);
            }
            catch (Exception error) when (error is FileNotFoundException || error is DirectoryNotFoundException)
            {
                return ServerContentRecord.Missing();
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                return ServerContentRecord.Unusable(ServerContentStatus.Unreadable,
                    "The record of server content could not be read: " + error.Message);
            }
        }

        /// <summary>The file's bytes, or null past <paramref name="maximum"/>. Never blocks the launcher's moves.</summary>
        private static byte[]? ReadBounded(string path, int maximum)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (input.Length > maximum) return null;
                var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
                {
                    if (buffer.Length + read > maximum) return null;
                    buffer.Write(chunk, 0, read);
                }

                return buffer.ToArray();
            }
        }

        private static string Hex(byte[] bytes)
        {
            var characters = new char[bytes.Length * 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                characters[index * 2] = "0123456789abcdef"[bytes[index] >> 4];
                characters[index * 2 + 1] = "0123456789abcdef"[bytes[index] & 15];
            }

            return new string(characters);
        }
    }
}
