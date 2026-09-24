using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace TopiaForge
{
    // Calls the installed game's public parser; no native implementation is copied here.
    public static class UgcNoOpProjectSmoke
    {
        public static void Run()
        {
            var output = Argument("-topiaforgeUgcOutput");
            Directory.CreateDirectory(output);
            try
            {
                var root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
                var managed = Argument("-robotopiaManagedDir");
                var runtime = Path.Combine(root, "src/TopiaForge.ModManager/bin/Release/netstandard2.1");
                AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
                {
                    var name = new AssemblyName(args.Name).Name;
                    var existing = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == name);
                    if (existing != null) return existing;
                    var file = Path.Combine(managed, name + ".dll");
                    if (!File.Exists(file)) file = Path.Combine(runtime, name + ".dll");
                    return File.Exists(file) ? Load(file) : null;
                };
                var manager = Load(Path.Combine(runtime, "TopiaForge.ModManager.dll"));
                var helper = manager.GetType("TopiaForge.Mods.GameBridge.UgcNoOpLaunchRequest", true);
                var json = (string)helper.GetField("EmptyProjectJson", BindingFlags.NonPublic | BindingFlags.Static).GetRawConstantValue();
                var bytes = Encoding.UTF8.GetBytes(json);
                var native = Load(Path.Combine(managed, "GameCode.dll"));
                var loader = native.GetType("UgcExportLoader", true);
                var projectType = native.GetType("UgcExportProject", true);
                var parse = loader.GetMethod("LoadProjectFromBytes", BindingFlags.Public | BindingFlags.Static);
                var resolve = projectType.GetMethod("ResolveScene", BindingFlags.Public | BindingFlags.Instance);
                var project = parse.Invoke(null, new object[] { bytes, "TopiaForge original empty host fixture" });
                Check(Count(project, "assets") == 0 && Count(project, "localAssets") == 0, "Unexpected project assets.");
                Check(Count(project, "scenes") == 1, "Expected one scene.");
                var scene = resolve.Invoke(project, new object[] { null });
                var id = (string)scene.GetType().GetField("id").GetValue(scene);
                Check(id == "topiaforge-empty" && Count(scene, "entities") == 0, "Unexpected resolved scene or entities.");
                Check(ReferenceEquals(scene, resolve.Invoke(project, new object[] { id })), "Explicit scene resolution differs.");
                var file = Path.Combine(output, "topiaforge-empty-scene.json");
                File.WriteAllBytes(file, bytes);
                var fromFile = loader.GetMethod("LoadProject", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { file });
                Check(Count(resolve.Invoke(fromFile, new object[] { null }), "entities") == 0, "Native file import differs.");
                var rejectedEmpty = false;
                try
                {
                    var invalid = parse.Invoke(null, new object[] { Encoding.UTF8.GetBytes("{}"), null });
                    resolve.Invoke(invalid, new object[] { null });
                }
                catch (TargetInvocationException error) when (error.InnerException != null
                    && error.InnerException.Message.Contains("no scenes")) { rejectedEmpty = true; }
                Check(rejectedEmpty, "Negative control did not reject a project without scenes.");
                string hash;
                using (var sha = SHA256.Create()) hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
                File.WriteAllText(Path.Combine(output, "ugc-project-smoke.json"), "{\n  \"passed\": true,\n  \"editor\": \"" + Application.unityVersion
                    + "\",\n  \"jsonSha256\": \"" + hash + "\",\n  \"sceneId\": \"" + id
                    + "\",\n  \"scenes\": 1,\n  \"entities\": 0,\n  \"assets\": 0,\n  \"localAssets\": 0,\n  \"nativeFileLoadPassed\": true,\n  \"negativeControlRejected\": true\n}\n");
                EditorApplication.Exit(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
                Debug.LogException(error); EditorApplication.Exit(1);
            }
        }
        private static int Count(object item, string field) => ((IDictionary)item.GetType().GetField(field).GetValue(item)).Count;
        private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static Assembly Load(string path)
        {
            AssemblyName name = null;
            var bytes = UiSmokeAssemblyFileIo.ReadStableBytes(path, 64 * 1024 * 1024,
                "Native UGC smoke assembly", inspected => name = AssemblyName.GetAssemblyName(inspected));
            return AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(item => item.GetName().Name == name.Name) ?? Assembly.Load(bytes);
        }
        private static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs(); var index = Array.IndexOf(args, key);
            if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("Missing " + key);
            return args[index + 1];
        }
    }
}
