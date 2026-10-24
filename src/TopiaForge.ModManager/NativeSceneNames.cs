using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The scene the game's own loader is given. <c>SceneUtil.LoadScene</c> takes a scene's name, as the game's checkpoints
    /// give it, and finds the scene again by that name once it has loaded. A build path such as
    /// <c>Assets/Levels/Arena.unity</c> loads the scene, but the loader never finds it again and reports that the scene
    /// could not be loaded, after it arrived. So a build path goes to the loader as its scene's name, and only while no other
    /// build scene shares that name, so the name still means the scene that was chosen.
    /// </summary>
    internal static class NativeSceneNames
    {
        private const string SceneExtension = ".unity";

        /// <param name="scene">A scene's name, or its path in the build settings.</param>
        /// <param name="buildScenePaths">The path of every scene in the build settings.</param>
        internal static OperationResult<string> ForGameLoader(string scene, IEnumerable<string> buildScenePaths)
        {
            if (string.IsNullOrWhiteSpace(scene)) throw new ArgumentException("A native scene is required.", nameof(scene));
            if (buildScenePaths == null) throw new ArgumentNullException(nameof(buildScenePaths));
            var isPath = scene.IndexOf('/') >= 0 || scene.IndexOf('\\') >= 0;
            var name = NameOf(scene);
            var named = 0;
            var listed = false;
            foreach (var path in buildScenePaths)
            {
                // The game matches scene names without regard to case, so two names that differ only in case are one name.
                if (string.IsNullOrWhiteSpace(path) || !string.Equals(NameOf(path), name, StringComparison.OrdinalIgnoreCase)) continue;
                named++;
                listed |= string.Equals(path, scene, StringComparison.Ordinal);
            }

            if (named > 1)
                return OperationResult<string>.Failure(ModErrorCode.Conflict, "The native scene name identifies multiple build scenes.");
            if (isPath && !listed)
                return OperationResult<string>.Failure(ModErrorCode.NotFound, "The native scene is unavailable: " + scene);
            return OperationResult<string>.Success(name);
        }

        private static string NameOf(string scene)
        {
            var file = scene.Substring(Math.Max(scene.LastIndexOf('/'), scene.LastIndexOf('\\')) + 1);
            return file.EndsWith(SceneExtension, StringComparison.OrdinalIgnoreCase)
                ? file.Substring(0, file.Length - SceneExtension.Length) : file;
        }
    }
}
