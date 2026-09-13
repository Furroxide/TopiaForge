using System.Collections.Concurrent;

namespace TopiaForge.Acceptance.Windows;

internal sealed record PersistenceFileFact(string Path, bool Exists, string? Sha256, long Length);
internal sealed record PersistenceFact(PersistenceFileFact[] Files, string[] ChangedPaths, bool Overflow, bool UnexpectedWrite);

/// Watches only the admitted QA game's persistent directory, never a personal profile.
internal sealed class PersistenceObserver : IDisposable
{
    private readonly string root;
    private readonly string[] paths;
    private readonly FileSystemWatcher watcher;
    private readonly ConcurrentDictionary<string, byte> changes = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool overflow;
    internal PersistenceObserver(string root, string[] paths)
    {
        this.root = BoundedJson.PhysicalPath(root);
        if (!Directory.Exists(this.root)) throw new InvalidDataException("Admitted persistent root must already exist.");
        this.paths = paths;
        foreach (var path in paths) _ = BoundedJson.Child(root, path);
        watcher = new(root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 32768 };
        watcher.Changed += Changed; watcher.Created += Changed; watcher.Deleted += Changed;
        watcher.Renamed += (_, item) => { Record(item.OldFullPath); Record(item.FullPath); };
        watcher.Error += (_, _) => overflow = true;
        watcher.EnableRaisingEvents = true;
    }
    private void Changed(object sender, FileSystemEventArgs item) => Record(item.FullPath);
    private void Record(string path)
    {
        if (changes.Count >= 1024) { overflow = true; return; }
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        changes.TryAdd(relative, 0);
    }
    internal PersistenceFact Snapshot()
    {
        BoundedJson.PhysicalPath(root);
        var files = paths.Select(relative =>
        {
            var path = BoundedJson.Child(root, relative);
            if (!File.Exists(path)) return new PersistenceFileFact(relative, false, null, 0);
            var length = new FileInfo(path).Length;
            if (length > 64 * 1024 * 1024) throw new InvalidDataException("Synthetic persistence file exceeds its bound.");
            return new PersistenceFileFact(relative, true, BoundedJson.Hash(path), length);
        }).ToArray();
        var changed = changes.Keys.Order(StringComparer.Ordinal).ToArray();
        // The watch is evidence, not permission to rewrite any path. Even writes
        // outside the explicitly hashed synthetic files are a failed oracle.
        return new(files, changed, overflow, changed.Any(path => !paths.Contains(path, StringComparer.OrdinalIgnoreCase)));
    }
    public void Dispose() => watcher.Dispose();
}
