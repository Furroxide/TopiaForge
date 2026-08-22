using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Clean-room reflection bridge onto build 2409's local export importer, so a <c>.roboworld</c> a player
    /// already has on disk can be loaded into the running game.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The game's importer has two reachable halves. <c>UgcImportHostConfig</c> is a ScriptableObject holding
    /// the serialized <c>ImportFolderOverride</c> / <c>SelectedExportFilePath</c> / <c>SelectedSceneId</c>
    /// selection, and <c>UgcImportHostSceneController</c> is the live scene component that scans that folder
    /// and builds the scene from a chosen file. <c>ConfigureRuntimeImportFolder</c> and <c>ImportFile</c> are
    /// both public and both take nothing but a path.
    /// </para>
    /// <para>
    /// That matters because it is the whole feature: the local path needs no Discord sign-in, no publish, and
    /// no backend call. This bridge deliberately touches none of the cloud entry points — not
    /// <c>UgcPublishedProjectLoader</c>, not <c>UgcAutomergeSyncClient</c>, not <c>UgcLaunchUrlStartup</c>.
    /// A world it loads came off the player's own disk.
    /// </para>
    /// <para>
    /// Every binding here is <c>Degraded</c>: if the importer moves, local worlds stop loading and the caller
    /// gets a reason, but nothing in Worlds throws. Main thread only — it reads live scene objects.
    /// </para>
    /// </remarks>
    internal sealed class UgcImportHostBridge : IDisposable
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags AnyInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly IModLogger logger;
        private readonly Type? importHostConfigType;
        private readonly Type? importHostControllerType;
        private readonly Type? exportLoaderType;
        private readonly Type? importFileIndexType;

        private ConfigSelection? restoreSelection;
        private bool disposed;

        public UgcImportHostBridge(IModLogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            importHostConfigType = Type.GetType("UgcImportHostConfig, GameCode", throwOnError: false);
            importHostControllerType = Type.GetType("UgcImportHostSceneController, GameCode", throwOnError: false);
            exportLoaderType = Type.GetType("UgcExportLoader, GameCode", throwOnError: false);
            importFileIndexType = Type.GetType("UgcImportFileIndex, GameCode", throwOnError: false);
        }

        /// <summary>Gets whether the game still exposes enough of the importer to load a local export.</summary>
        public bool IsAvailable => importHostControllerType != null && exportLoaderType != null;

        /// <summary>
        /// Gets the folder the game itself scans by default, or an empty string when the game no longer
        /// says. Used as the default for the Worlds local-world folder so the two agree out of the box.
        /// </summary>
        public string GetDefaultImportFolder()
        {
            try
            {
                var value = importHostConfigType
                    ?.GetMethod("GetDefaultImportFolderPath", PublicStatic)
                    ?.Invoke(null, Array.Empty<object>()) as string;
                return value ?? string.Empty;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not read the game's default import folder: " + ex.Message);
                return string.Empty;
            }
        }

        /// <summary>
        /// Parses an export with the game's own loader, without touching the scene.
        /// </summary>
        /// <remarks>
        /// This runs before any import so a malformed file is refused while the current world is still
        /// intact, and so the reason the player sees is the game's own wording rather than ours.
        /// </remarks>
        public bool TryValidateExport(string filePath, out string projectName, out string error)
        {
            projectName = string.Empty;
            error = string.Empty;

            var tryLoad = exportLoaderType?.GetMethod("TryLoadProject", PublicStatic);
            if (tryLoad == null)
            {
                error = "This game build does not expose the local export loader.";
                return false;
            }

            try
            {
                var arguments = new object?[] { filePath, null, null };
                var loaded = tryLoad.Invoke(null, arguments) is bool result && result;
                if (!loaded)
                {
                    error = arguments[2] as string ?? "The export could not be read.";
                    return false;
                }

                projectName = ReadProjectName(arguments[1]);
                return true;
            }
            catch (Exception ex)
            {
                error = "The export could not be read: " + Unwrap(ex).Message;
                return false;
            }
        }

        /// <summary>Lists the exports the game's own scanner finds in <paramref name="folderPath"/>.</summary>
        public IReadOnlyList<RoboWorldFile> ScanFolder(string folderPath)
        {
            var found = new List<RoboWorldFile>();
            var scan = importFileIndexType?.GetMethod("Scan", PublicStatic);
            if (scan == null)
            {
                return found;
            }

            try
            {
                if (!(scan.Invoke(null, new object?[] { folderPath }) is IEnumerable records))
                {
                    return found;
                }

                foreach (var record in records)
                {
                    if (record == null)
                    {
                        continue;
                    }

                    var type = record.GetType();
                    found.Add(new RoboWorldFile(
                        ReadStringField(type, record, "path"),
                        ReadStringField(type, record, "fileName"),
                        ReadStringField(type, record, "projectName"),
                        ReadStringField(type, record, "loadError")));
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not scan the local world folder: " + ex.Message);
            }

            return found;
        }

        /// <summary>
        /// Points the live importer at the planned folder and imports the planned file.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The config selection is snapshotted before it is overwritten and restored on
        /// <see cref="Dispose"/>. <c>UgcImportHostConfig</c> is a shipped asset shared with the game's own
        /// import host; leaving our folder in it would silently change what the game does after our session
        /// ends. In a player build the asset is in-memory only, so restoring costs nothing and is correct
        /// anyway.
        /// </para>
        /// <para>
        /// An import that fails after the override is applied restores immediately rather than waiting for
        /// disposal, and the two symbols this needs are resolved before anything is written, so a build that
        /// no longer exposes them refuses without having touched the game's selection at all.
        /// </para>
        /// </remarks>
        public bool TryImport(RoboWorldImportPlan plan, out string error)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            error = string.Empty;

            if (importHostControllerType == null)
            {
                error = "This game build does not expose the local world importer.";
                return false;
            }

            var controller = UnityEngine.Object.FindAnyObjectByType(importHostControllerType);
            if (controller == null)
            {
                error = "No import host is present in the active scene.";
                return false;
            }

            if (!TryValidateExport(plan.FilePath, out var projectName, out error))
            {
                return false;
            }

            // Both methods are resolved before anything is written, because the config they would be
            // driving is the game's own shared selection. Failing the second lookup after overriding it
            // would leave our folder in the game's import UI for the rest of the session.
            var configureFolder = importHostControllerType.GetMethod(
                "ConfigureRuntimeImportFolder", PublicInstance);
            if (configureFolder == null)
            {
                error = "This game build does not expose the runtime import-folder override.";
                return false;
            }

            var importFile = importHostControllerType.GetMethod("ImportFile", PublicInstance);
            if (importFile == null)
            {
                error = "This game build does not expose a local export import.";
                return false;
            }

            try
            {
                ApplyConfigSelection(controller, plan);

                configureFolder.Invoke(controller, new object?[] { plan.FolderPath });
                importHostControllerType.GetMethod("RefreshImportFiles", PublicInstance)
                    ?.Invoke(controller, Array.Empty<object>());

                importFile.Invoke(controller, new object?[] { plan.FilePath });

                // ImportFile returns void and swallows its own failures, so "it was called" is not "it
                // worked". The importer records what it last built; if that is still null the import did not
                // produce a scene and the caller must not be told it did.
                var importedScene = importHostControllerType
                    .GetProperty("LastImportedScene", PublicInstance)?.GetValue(controller);
                if (importedScene == null)
                {
                    error = "'" + plan.FileName + "' produced no scene. Check the game log for details.";
                    RestoreConfigSelection();
                    return false;
                }

                var label = importHostControllerType
                    .GetProperty("LastImportSourceLabel", PublicInstance)?.GetValue(controller) as string;
                logger.Info(
                    "Worlds imported local world '"
                    + (projectName.Length > 0 ? projectName : plan.FileName)
                    + "'" + (string.IsNullOrEmpty(label) ? string.Empty : " (" + label + ")") + ".");
                return true;
            }
            catch (Exception ex)
            {
                error = "'" + plan.FileName + "' could not be imported: " + Unwrap(ex).Message;
                RestoreConfigSelection();
                return false;
            }
        }

        /// <summary>Restores the game's own import selection.</summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            RestoreConfigSelection();
        }

        /// <summary>Puts the game's own import selection back, if this bridge ever overrode it.</summary>
        private void RestoreConfigSelection()
        {
            var selection = restoreSelection;
            restoreSelection = null;
            if (selection == null)
            {
                return;
            }

            try
            {
                selection.Restore();
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not restore the game's import selection: " + ex.Message);
            }
        }

        private void ApplyConfigSelection(object controller, RoboWorldImportPlan plan)
        {
            // The controller keeps its ScriptableObject in a private `config` field. Writing the override
            // there — rather than only calling ConfigureRuntimeImportFolder — is what survives the
            // controller's OnEnable, which re-reads the config and would otherwise pull the folder back to
            // the game's default on the next scene activation.
            var config = importHostControllerType?.GetField("config", AnyInstance)?.GetValue(controller);
            if (config == null || importHostConfigType == null)
            {
                return;
            }

            var folder = importHostConfigType.GetProperty("ImportFolderOverride", PublicInstance);
            var selectedFile = importHostConfigType.GetProperty("SelectedExportFilePath", PublicInstance);
            var selectedScene = importHostConfigType.GetProperty("SelectedSceneId", PublicInstance);
            if (folder == null || selectedFile == null)
            {
                return;
            }

            restoreSelection ??= new ConfigSelection(
                config,
                folder,
                selectedFile,
                selectedScene,
                folder.GetValue(config) as string,
                selectedFile.GetValue(config) as string,
                selectedScene?.GetValue(config) as string);

            folder.SetValue(config, plan.FolderPath);
            selectedFile.SetValue(config, plan.FilePath);

            // Leave SelectedSceneId empty on purpose: an empty request makes ResolveScene pick the project's
            // own first scene, which is what "load this world" means. Naming a scene is a later feature.
            selectedScene?.SetValue(config, string.Empty);
        }

        private string ReadProjectName(object? project)
        {
            try
            {
                return project?.GetType().GetField("name", AnyInstance)?.GetValue(project) as string
                    ?? string.Empty;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not read the export's project name: " + ex.Message);
                return string.Empty;
            }
        }

        private static string ReadStringField(Type type, object instance, string field)
        {
            try
            {
                return type.GetField(field, AnyInstance)?.GetValue(instance) as string ?? string.Empty;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }

        private static Exception Unwrap(Exception exception) =>
            exception is TargetInvocationException invocation && invocation.InnerException != null
                ? invocation.InnerException
                : exception;

        /// <summary>The game's import selection as it was before Worlds overrode it.</summary>
        private sealed class ConfigSelection
        {
            private readonly object config;
            private readonly PropertyInfo folder;
            private readonly PropertyInfo selectedFile;
            private readonly PropertyInfo? selectedScene;
            private readonly string? previousFolder;
            private readonly string? previousSelectedFile;
            private readonly string? previousSelectedScene;

            public ConfigSelection(
                object config,
                PropertyInfo folder,
                PropertyInfo selectedFile,
                PropertyInfo? selectedScene,
                string? previousFolder,
                string? previousSelectedFile,
                string? previousSelectedScene)
            {
                this.config = config;
                this.folder = folder;
                this.selectedFile = selectedFile;
                this.selectedScene = selectedScene;
                this.previousFolder = previousFolder;
                this.previousSelectedFile = previousSelectedFile;
                this.previousSelectedScene = previousSelectedScene;
            }

            public void Restore()
            {
                folder.SetValue(config, previousFolder ?? string.Empty);
                selectedFile.SetValue(config, previousSelectedFile ?? string.Empty);
                selectedScene?.SetValue(config, previousSelectedScene ?? string.Empty);
            }
        }
    }

    /// <summary>One local export the game's scanner found, reduced to what Worlds shows a player.</summary>
    internal sealed class RoboWorldFile
    {
        public RoboWorldFile(string path, string fileName, string projectName, string loadError)
        {
            Path = path ?? string.Empty;
            FileName = fileName ?? string.Empty;
            ProjectName = projectName ?? string.Empty;
            LoadError = loadError ?? string.Empty;
        }

        /// <summary>Gets the absolute path of the export.</summary>
        public string Path { get; }

        /// <summary>Gets the export's file name.</summary>
        public string FileName { get; }

        /// <summary>Gets the project name declared inside the export, when it could be read.</summary>
        public string ProjectName { get; }

        /// <summary>Gets the scanner's own error for this file, or an empty string when it parsed.</summary>
        public string LoadError { get; }

        /// <summary>Gets whether the game's scanner could read this export.</summary>
        public bool IsLoadable => LoadError.Length == 0;
    }
}
