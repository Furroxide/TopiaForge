using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace TopiaForge.Mods.GameBridge
{
    /// <summary>
    /// Shared clean-room reflection helper: queues an original, empty local project through the game's
    /// <c>UgcPlayLaunchRequest</c>. The native bootstrap still runs and spawns the player without importing
    /// user content. Build 2409 rejects a missing selected file or an empty LatestFile folder. Source-linked into every
    /// mod that loads the play scene (TopiaForge.Worlds) so the request shape lives in
    /// exactly one place. Unity-free on purpose; degrades silently when the game symbols are missing.
    /// </summary>
    internal static class UgcNoOpLaunchRequest
    {
        private static readonly object FolderGate = new object();
        internal const string EmptyExportName = "topiaforge-empty-scene.json";
        // Original data following the observed native JSON schema; no native implementation is copied.
        internal const string EmptyProjectJson = "{\"version\":\"1\",\"name\":\"TopiaForge empty host\",\"assets\":{},\"local-assets\":{},"
            + "\"scenes\":{\"topiaforge-empty\":{\"id\":\"topiaforge-empty\",\"name\":\"TopiaForge empty host\",\"environment\":\"day\",\"entities\":{}}}}";

        /// <param name="lastRunType">The game's <c>UgcPlayLauncherLastRun</c> type (null degrades to false).</param>
        /// <param name="launchRequestType">The game's <c>UgcPlayLaunchRequest</c> type (null degrades to false).</param>
        /// <param name="emptyFolderName">Per-caller temp folder prefix, so callers do not share import folders.</param>
        /// <param name="logDebug">Debug sink for the (non-fatal) failure path.</param>
        public static bool TryQueue(Type? lastRunType, Type? launchRequestType, string emptyFolderName, Action<string>? logDebug)
        {
            try
            {
                if (lastRunType == null || launchRequestType == null)
                {
                    return false;
                }

                var values = Activator.CreateInstance(lastRunType);
                if (values == null)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(emptyFolderName)
                    || emptyFolderName == "." || emptyFolderName == ".."
                    || !string.Equals(Path.GetFileName(emptyFolderName), emptyFolderName, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The empty-folder name must be one non-empty path segment.", nameof(emptyFolderName));
                }

                // Reuse one stable folder per caller instead of leaking a GUID-named directory on every start/stop.
                // Clear it immediately before arming the request so a stale export left by an interrupted run can
                // never be imported. Caller-specific names keep Worlds, live sync, and stopped-request state apart.
                var tempRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var emptyImportFolder = Path.GetFullPath(Path.Combine(tempRoot, emptyFolderName));
                if (!emptyImportFolder.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("The import folder must remain inside temporary storage.", nameof(emptyFolderName));
                PrepareEmptyFolder(emptyImportFolder);
                lastRunType.GetField("Mode")?.SetValue(values, "SelectedFile");
                lastRunType.GetField("ImportFolderPath")?.SetValue(values, emptyImportFolder);
                lastRunType.GetField("SelectedExportFilePath")?.SetValue(values, Path.Combine(emptyImportFolder, EmptyExportName));

                var create = launchRequestType.GetMethod(
                    "Create", BindingFlags.Public | BindingFlags.Static, null, new[] { lastRunType }, null);
                create?.Invoke(null, new[] { values });
                return create != null;
            }
            catch (Exception ex)
            {
                try
                {
                    logDebug?.Invoke("Could not queue a no-op UGC launch request: " + ex.Message);
                }
                catch
                {
                    // This helper is a best-effort compatibility shim; logging cannot make its fallback fatal.
                }

                return false;
            }
        }

        private static void PrepareEmptyFolder(string path)
        {
            lock (FolderGate)
            {
                Directory.CreateDirectory(path);
                RejectLinks(path);
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    Directory.Delete(directory, recursive: true);
                }
                File.WriteAllText(Path.Combine(path, EmptyExportName), EmptyProjectJson, new UTF8Encoding(false));
            }
        }

        private static void RejectLinks(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked import paths are not supported.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked import entries are not supported.");
                if ((attributes & FileAttributes.Directory) != 0) RejectLinks(entry);
            }
        }
    }
}
