using System;
using System.IO;
using System.Reflection;

namespace Robotopia.Mods.GameBridge
{
    /// <summary>
    /// Shared clean-room reflection helper: queues the game's <c>UgcPlayLaunchRequest</c> pointed at an empty
    /// temp folder, so a subsequent load of the UGC play scene imports no user content (the scene's bootstrap
    /// still runs and spawns the player — only the content import is neutralized). Source-linked into every
    /// mod that loads the play scene (Robotopia.Worlds, Robotopia.UgcLiveSync) so the request shape lives in
    /// exactly one place. Unity-free on purpose; degrades silently when the game symbols are missing.
    /// </summary>
    internal static class UgcNoOpLaunchRequest
    {
        private static readonly object FolderGate = new object();

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
                    || !string.Equals(Path.GetFileName(emptyFolderName), emptyFolderName, StringComparison.Ordinal))
                {
                    throw new ArgumentException("The empty-folder name must be one non-empty path segment.", nameof(emptyFolderName));
                }

                // Reuse one stable folder per caller instead of leaking a GUID-named directory on every start/stop.
                // Clear it immediately before arming the request so a stale export left by an interrupted run can
                // never be imported. Caller-specific names keep Worlds, live sync, and stopped-request state apart.
                var emptyImportFolder = Path.Combine(Path.GetTempPath(), emptyFolderName);
                PrepareEmptyFolder(emptyImportFolder);
                lastRunType.GetField("Mode")?.SetValue(values, "SelectedFile");
                lastRunType.GetField("ImportFolderPath")?.SetValue(values, emptyImportFolder);
                lastRunType.GetField("SelectedExportFilePath")?.SetValue(values, string.Empty);

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
                foreach (var file in Directory.EnumerateFiles(path))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                foreach (var directory in Directory.EnumerateDirectories(path))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
        }
    }
}
