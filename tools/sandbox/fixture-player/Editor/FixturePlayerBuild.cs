using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace TopiaForge
{
    /// <summary>Builds the one-scene fixture player headlessly from the pinned Editor; invoked with -executeMethod.</summary>
    public static class FixturePlayerBuild
    {
        public static void Build()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var host = new GameObject("FixtureQuit");
            host.AddComponent<FixtureQuit>();
            if (!EditorSceneManager.SaveScene(scene, "Assets/Fixture.unity")) throw new InvalidOperationException("Fixture scene could not be saved.");
            var output = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Build", "FixturePlayer.exe"));
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Fixture.unity" },
                locationPathName = output,
                target = BuildTarget.StandaloneWindows64,
                options = BuildOptions.None
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Fixture player build failed: " + report.summary.result + " (" + report.summary.totalErrors + " errors).");
            Debug.Log("FixturePlayerBuild: built " + output + " with " + report.summary.totalErrors + " errors and " + report.summary.totalWarnings + " warnings.");
        }
    }
}
