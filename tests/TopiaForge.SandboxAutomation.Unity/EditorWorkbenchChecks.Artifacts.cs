using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;

namespace TopiaForge.SandboxAutomation.Unity
{
    public static partial class EditorWorkbenchChecks
    {
        private static void CapturePng(string name)
        {
            // Render the real retained hierarchy through a temporary camera. The production
            // overlay mode is restored synchronously; geometry/input checks remain overlay checks.
            var canvases = Resources.FindObjectsOfTypeAll<Canvas>().Where(value => value.gameObject.scene.IsValid()
                && value.renderMode == RenderMode.ScreenSpaceOverlay).ToArray();
            var go = new GameObject("EditorCaptureCamera", typeof(Camera));
            var camera = go.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.08f, 0.09f, 0.1f, 1f);
            var target = new RenderTexture(Screen.width, Screen.height, 24, RenderTextureFormat.ARGB32);
            var pixels = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
            var active = RenderTexture.active;
            try
            {
                camera.targetTexture = target;
                foreach (var canvas in canvases)
                { canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = 1f; }
                Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active = target;
                pixels.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0); pixels.Apply();
                var colors = pixels.GetPixels32();
                Require(colors.Select(value => ((int)value.r << 16) | ((int)value.g << 8) | value.b).Distinct().Take(64).Count() == 64,
                    "rendered-nonblank:" + name);
                File.WriteAllBytes(Path.Combine(output, name), pixels.EncodeToPNG());
            }
            finally
            {
                foreach (var canvas in canvases) if (canvas != null) { canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.worldCamera = null; }
                RenderTexture.active = active;
                camera.targetTexture = null;
                Object.DestroyImmediate(pixels); Object.DestroyImmediate(target); Object.DestroyImmediate(go);
                Canvas.ForceUpdateCanvases();
            }
        }
        private static string Json(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        private static void WriteResult()
        {
            var artifacts = Directory.GetFiles(output, "*.png").OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            {
                using var hash = SHA256.Create();
                var digest = BitConverter.ToString(hash.ComputeHash(File.ReadAllBytes(path))).Replace("-", "").ToLowerInvariant();
                return "{\"path\":" + Json(Path.GetFileName(path)) + ",\"sha256\":" + Json(digest) + "}";
            });
            var json = "{\n\"schemaVersion\":1,\"kind\":\"sandbox-editor-observations-v1\",\"status\":\"passed\",\"qualifiesRelease\":false,"
                + "\"inputMode\":\"synthetic-unity-eventsystem\",\"renderMode\":\"temporary-camera-of-production-overlay-hierarchy\","
                + "\"gameExecution\":false,\"editorVersion\":" + Json(Application.unityVersion)
                + ",\"width\":" + Screen.width + ",\"height\":" + Screen.height + ",\"dpi\":" + Screen.dpi.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ",\"graphicsDevice\":" + Json(SystemInfo.graphicsDeviceName) + ",\"graphicsApi\":" + Json(SystemInfo.graphicsDeviceType.ToString())
                + ",\"checks\":[" + string.Join(",", Passed.Select(Json)) + "],\"negativeDetections\":[" + string.Join(",", Negatives.Select(Json))
                + "],\"artifacts\":[" + string.Join(",", artifacts) + "],\"editorWorkbenchCycles\":10,\"existingUiSmokeCycles\":0,"
                + "\"nativeGameProof\":false,\"humanVisualApproval\":false}\n";
            File.WriteAllText(Path.Combine(output, "editor-observations.json"), json, new UTF8Encoding(false));
        }
    }
}
