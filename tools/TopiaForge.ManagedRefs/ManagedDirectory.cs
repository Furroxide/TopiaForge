using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace TopiaForge.ManagedRefs;

internal interface IManagedDirectoryValidator
{
    void Validate(string path);

    bool IsValid(string path, out string error);
}

internal sealed class ManagedDirectoryValidator : IManagedDirectoryValidator
{
    internal static readonly IReadOnlyDictionary<string, string> RequiredAssemblies =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GameCode.dll"] = "GameCode",
            ["UniTask.dll"] = "UniTask",
            ["Unity.InputSystem.dll"] = "Unity.InputSystem",
            ["Unity.RenderPipelines.Core.Runtime.dll"] = "Unity.RenderPipelines.Core.Runtime",
            ["Unity.RenderPipelines.GPUDriven.Runtime.dll"] = "Unity.RenderPipelines.GPUDriven.Runtime",
            ["Unity.RenderPipelines.HighDefinition.Runtime.dll"] = "Unity.RenderPipelines.HighDefinition.Runtime",
            ["Unity.TextMeshPro.dll"] = "Unity.TextMeshPro",
            ["UnityEngine.dll"] = "UnityEngine",
            ["UnityEngine.AnimationModule.dll"] = "UnityEngine.AnimationModule",
            ["UnityEngine.AssetBundleModule.dll"] = "UnityEngine.AssetBundleModule",
            ["UnityEngine.AudioModule.dll"] = "UnityEngine.AudioModule",
            ["UnityEngine.CoreModule.dll"] = "UnityEngine.CoreModule",
            ["UnityEngine.IMGUIModule.dll"] = "UnityEngine.IMGUIModule",
            ["UnityEngine.InputLegacyModule.dll"] = "UnityEngine.InputLegacyModule",
            ["UnityEngine.PhysicsModule.dll"] = "UnityEngine.PhysicsModule",
            ["UnityEngine.TextCoreFontEngineModule.dll"] = "UnityEngine.TextCoreFontEngineModule",
            ["UnityEngine.TextCoreTextEngineModule.dll"] = "UnityEngine.TextCoreTextEngineModule",
            ["UnityEngine.TextRenderingModule.dll"] = "UnityEngine.TextRenderingModule",
            ["UnityEngine.UI.dll"] = "UnityEngine.UI",
            ["UnityEngine.UIModule.dll"] = "UnityEngine.UIModule",
        };

    public void Validate(string path) => Validate(path, RequiredAssemblies);

    public bool IsValid(string path, out string error)
    {
        try
        {
            Validate(path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static void Validate(string path, IReadOnlyDictionary<string, string> requiredAssemblies)
    {
        PathSafety.RequireRegularDirectory(path, "managed-reference directory");
        foreach (var required in requiredAssemblies)
        {
            var assemblyPath = Path.Combine(path, required.Key);
            PathSafety.RequireRegularFile(assemblyPath, $"managed reference {required.Key}");

            AssemblyName identity;
            try
            {
                identity = AssemblyName.GetAssemblyName(assemblyPath);
            }
            catch (Exception exception) when (exception is FileLoadException or BadImageFormatException)
            {
                throw new BadImageFormatException(
                    $"Managed reference '{assemblyPath}' is not a valid managed PE assembly.",
                    exception);
            }

            if (!string.Equals(identity.Name, required.Value, StringComparison.Ordinal))
            {
                throw new BadImageFormatException(
                    $"Managed reference '{assemblyPath}' has assembly identity '{identity.Name}', expected '{required.Value}'.");
            }
        }
    }
}

internal static class PathSafety
{
    internal static void RequireRegularDirectory(string path, string label)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException($"{label} was not found: {path}");
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{label} must be a regular directory and cannot be a link: {path}");
        }
    }

    internal static void RequireRegularFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"{label} was not found: {path}", path);
        }

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) != 0 || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"{label} must be a regular file and cannot be a link: {path}");
        }
    }

    internal static void DeleteDirectoryIfSafe(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        RequireRegularDirectory(path, "cache directory");
        Directory.Delete(path, recursive: true);
    }
}
