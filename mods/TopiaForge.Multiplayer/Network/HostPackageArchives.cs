using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// One hosting run's fixed archive allowlist. Only descriptor hashes form cache names, before any request arrives.
    /// Each open verifies the same read-only handle it returns; it runs on an endpoint worker, never a game/network turn.
    /// No archive is reconstructed from installed files: their hash cannot identify the original ZIP bytes.
    /// </summary>
    internal sealed class HostPackageArchives
    {
        internal const long MaximumBytes = 512L * 1024 * 1024;
        private readonly Dictionary<string, Entry> archives = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> warnings = new ConcurrentQueue<string>();
        private readonly Func<bool> developerMode;

        internal HostPackageArchives(IReadOnlyList<DescriptorPackage> packages, string? cache, Func<bool> developerMode)
        {
            this.developerMode = developerMode;
            foreach (var package in packages)
            {
                if (archives.ContainsKey(package.Sha256)) continue;
                var path = cache == null ? null : Path.Combine(cache, package.Sha256 + ".topiaforgemod");
                var entry = new Entry(package.Id + " " + package.Version, path);
                archives.Add(package.Sha256, entry);
                if (path == null || !File.Exists(path)) Warn(entry, "its original archive is missing from the launcher cache");
            }
        }

        internal Stream? Open(string sha256, CancellationToken cancellationToken)
        {
            if (!developerMode() || !archives.TryGetValue(sha256, out var entry)) return null;
            FileStream? stream = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Path == null) return null;
                if ((File.GetAttributes(entry.Path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    Warn(entry, "its cached archive is not a regular file");
                    return null;
                }

                stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
                if (stream.Length <= 0 || stream.Length > MaximumBytes)
                {
                    Warn(entry, "its cached archive is empty or exceeds the package size limit");
                    return null;
                }

                using (var hash = SHA256.Create())
                {
                    var buffer = new byte[64 * 1024];
                    var remaining = stream.Length;
                    while (remaining > 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                        if (read == 0) break;
                        remaining -= read;
                        hash.TransformBlock(buffer, 0, read, buffer, 0);
                    }

                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    if (remaining != 0 || stream.ReadByte() != -1 || !string.Equals(WireText.ToHex(hash.Hash!), sha256, StringComparison.Ordinal))
                    {
                        Warn(entry, "its cached archive no longer matches the session's SHA-256");
                        return null;
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (!developerMode()) return null;
                stream.Position = 0;
                var verified = stream;
                stream = null;
                return verified;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException || exception is System.Security.SecurityException)
            {
                // Never retain the exception: its message and stack can name a local file.
                Warn(entry, "its cached archive is missing, in use or cannot be read");
                return null;
            }
            finally
            {
                stream?.Dispose();
            }
        }

        /// <summary>Drains bounded, once-per-package warnings on the main thread.</summary>
        internal void ReportWarnings(Action<string> report)
        {
            while (warnings.TryDequeue(out var warning)) report(warning);
        }

        private void Warn(Entry entry, string reason)
        {
            if (Interlocked.Exchange(ref entry.Warned, 1) == 0)
                warnings.Enqueue("Cannot share " + entry.Name + ": " + reason + ". Restore the exact archive in the launcher cache and try the join again.");
        }

        /// <summary>Follows the launcher's data_root.dart; a configured root is inherited by the game process.</summary>
        internal static string? LauncherCache() => CacheFor(Environment.GetEnvironmentVariable, Environment.OSVersion.Platform == PlatformID.Win32NT, Environment.CurrentDirectory);

        internal static string? CacheFor(Func<string, string?> environment, bool windows, string currentDirectory)
        {
            try
            {
                var configured = environment("TOPIAFORGE_DATA_ROOT")?.Trim();
                var roaming = environment("APPDATA");
                var root = !string.IsNullOrEmpty(configured) ? configured
                    : windows && !string.IsNullOrEmpty(roaming) ? Path.Combine(roaming, "TopiaForgeLauncher")
                    : Path.Combine(environment("HOME") ?? environment("USERPROFILE") ?? currentDirectory, ".topiaforge_launcher");
                return Path.GetFullPath(Path.Combine(currentDirectory, root!, "package-cache"));
            }
            catch (Exception exception) when (exception is ArgumentException || exception is IOException || exception is NotSupportedException || exception is System.Security.SecurityException)
            {
                return null;
            }
        }

        private sealed class Entry
        {
            internal Entry(string name, string? path) { Name = name; Path = path; }
            internal string Name { get; }
            internal string? Path { get; }
            internal int Warned;
        }
    }
}
