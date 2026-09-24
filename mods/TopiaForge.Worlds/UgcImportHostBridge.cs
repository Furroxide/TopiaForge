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
    internal sealed partial class UgcImportHostBridge : ILocalWorldImportHost
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags AnyInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly IModLogger logger;
        private readonly Type? importHostConfigType;
        private readonly Type? importHostControllerType;
        private readonly Type? exportLoaderType;
        private readonly Type? exportProjectType;
        private readonly Type? importFileIndexType;


        public UgcImportHostBridge(IModLogger logger)
        {
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            importHostConfigType = Type.GetType("UgcImportHostConfig, GameCode", throwOnError: false);
            importHostControllerType = Type.GetType("UgcImportHostSceneController, GameCode", throwOnError: false);
            exportLoaderType = Type.GetType("UgcExportLoader, GameCode", throwOnError: false);
            exportProjectType = Type.GetType("UgcExportProject, GameCode", throwOnError: false);
            importFileIndexType = Type.GetType("UgcImportFileIndex, GameCode", throwOnError: false);
        }

        /// <summary>Gets whether the game still exposes enough of the importer to load a local export.</summary>
        public bool IsAvailable => importHostControllerType != null && ResolveExportLoader()?.ReturnType == typeof(bool);

        /// <summary>
        /// Gets whether the game still exposes the folder scanner backing <see cref="ScanFolder"/>.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="IsAvailable"/> on purpose: importing a known path does not need the
        /// scanner, so a build that lost only the index can still load a world the caller names. Listing
        /// cannot, and must say so rather than return an empty folder.
        /// </remarks>
        public bool CanScanFolder => importFileIndexType?.GetMethod("Scan", PublicStatic, null, new[] { typeof(string) }, null) != null;

        /// <summary>
        /// Gets the folder the game itself scans by default, or an empty string when the game no longer
        /// says. Used as the default for the Worlds local-world folder so the two agree out of the box.
        /// </summary>
        public string GetDefaultImportFolder()
        {
            try
            {
                var value = importHostConfigType
                    ?.GetMethod("GetDefaultImportFolderPath", PublicStatic, null, Type.EmptyTypes, null)
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

            var tryLoad = ResolveExportLoader();
            if (tryLoad == null || tryLoad.ReturnType != typeof(bool))
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
            var scan = importFileIndexType?.GetMethod("Scan", PublicStatic, null, new[] { typeof(string) }, null);
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
                throw new InvalidOperationException("The native local export scanner failed.", Unwrap(ex));
            }

            return found;
        }

        private MethodInfo? ResolveExportLoader() => exportProjectType == null ? null : exportLoaderType?.GetMethod(
            "TryLoadProject", PublicStatic, null,
            new[] { typeof(string), exportProjectType.MakeByRefType(), typeof(string).MakeByRefType() }, null);
        public void Dispose() { }
        private static string ReadProjectName(object? project) =>
            project?.GetType().GetField("name", AnyInstance)?.GetValue(project) as string ?? string.Empty;
        private static string ReadStringField(Type type, object instance, string field) =>
            type.GetField(field, AnyInstance)?.GetValue(instance) as string ?? string.Empty;
        private static Exception Unwrap(Exception exception) =>
            exception is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : exception;
    }

}
