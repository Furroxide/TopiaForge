using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace TopiaForge.Mods.GameBridge
{
    /// <summary>
    /// Queues a native-validated single-scene, zero-entity file for the initial UGC bootstrap.
    /// The ordinary native import and error paths remain active. Unity-free and source-linked;
    /// the authoritative Manager transition is the current production caller.
    /// </summary>
    internal static class UgcNoOpLaunchRequest
    {
        private const string EmptyProject = "{\"version\":\"1.0\",\"name\":\"TopiaForge Empty Bootstrap\",\"created\":\"1970-01-01T00:00:00Z\",\"modified\":\"1970-01-01T00:00:00Z\",\"assets\":{},\"local-assets\":{},\"scenes\":{\"main\":{\"id\":\"main\",\"name\":\"Empty Bootstrap\",\"entities\":{}}}}";
        private static readonly object FolderGate = new object();

        /// <param name="lastRunType">The game's <c>UgcPlayLauncherLastRun</c> type (null degrades to false).</param>
        /// <param name="launchRequestType">The game's <c>UgcPlayLaunchRequest</c> type (null degrades to false).</param>
        /// <param name="emptyFolderName">Per-caller temp folder prefix, so callers do not share import folders.</param>
        /// <param name="logDebug">Debug sink for the (non-fatal) failure path.</param>
        /// <param name="validateExport">Native parser preflight; missing or failed validation never arms a request.</param>
        public static bool TryQueue(Type? lastRunType, Type? launchRequestType, string emptyFolderName, Action<string>? logDebug, Func<string, bool>? validateExport = null)
        {
            try
            {
                if (lastRunType == null || launchRequestType == null || validateExport == null)
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

                var mode = lastRunType.GetField("Mode");
                var folder = lastRunType.GetField("ImportFolderPath");
                var selected = lastRunType.GetField("SelectedExportFilePath");
                var create = launchRequestType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { lastRunType }, null);
                if (mode?.FieldType != typeof(string) || folder?.FieldType != typeof(string)
                    || selected?.FieldType != typeof(string) || create == null) return false;
                var emptyImportFolder = Path.Combine(Path.GetTempPath(), emptyFolderName);
                lock (FolderGate)
                {
                    PrepareEmptyFolder(emptyImportFolder);
                    var export = Path.Combine(emptyImportFolder, "bootstrap.roboworld");
                    File.WriteAllText(export, EmptyProject, new UTF8Encoding(false));
                    if (!validateExport(export)) return false; // Never arm a request the native parser rejects.
                    mode.SetValue(values, "SelectedFile");
                    folder.SetValue(values, emptyImportFolder);
                    selected.SetValue(values, export);
                    create.Invoke(null, new[] { values });
                    return true;
                }
            }
            catch (Exception)
            {
                try
                {
                    logDebug?.Invoke("Could not queue the validated empty UGC bootstrap request.");
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
