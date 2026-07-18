using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    public sealed partial class ModRuntime
    {
        private void DispatchSceneActivatedCore(SceneLoadEvent scene)
        {
            RefreshRuntimeCapabilities();
            var count = loadedMods.Count;
            for (var index = 0; index < count && index < loadedMods.Count; index++)
            {
                var loaded = loadedMods[index];
                try
                {
                    // Activation is delivered only to the optional detailed stream. Legacy string-only subscribers
                    // retain their one-callback-per-load behavior.
                    loaded.Context.RaiseSceneActivated(scene);
                    sceneFailureLogged.Remove(loaded.Manifest.Id);
                }
                catch (Exception ex)
                {
                    if (sceneFailureLogged.Add(loaded.Manifest.Id))
                    {
                        logger.Error(ex, "Mod failed during SceneActivated '" + scene.SceneName + "': " + loaded.Manifest.Id);
                    }
                }
            }
        }

        private void DispatchFixedUpdate(GameTimeSample sample)
        {
            UnityMainThreadGuard.AssertCurrent();
            var count = loadedMods.Count;
            for (var index = 0; index < count; index++)
            {
                loadedMods[index].Context.RaiseFixedUpdate(sample);
            }
        }

        private void DispatchLateUpdate(GameTimeSample sample)
        {
            UnityMainThreadGuard.AssertCurrent();
            var count = loadedMods.Count;
            for (var index = 0; index < count; index++)
            {
                loadedMods[index].Context.RaiseLateUpdate(sample);
            }
        }

        private void RefreshRuntimeCapabilities()
        {
            UnityMainThreadGuard.AssertCurrent();
            RuntimeCapabilityProbe.Refresh(runtimeInfo, serviceRegistry);
        }

        private Assembly? ResolveAssembly(object sender, ResolveEventArgs args)
        {
            AssemblyName requested;
            try
            {
                requested = new AssemblyName(args.Name);
            }
            catch
            {
                return null;
            }

            var requesterOwner = ResolveRequesterOwner(args.RequestingAssembly) ?? loadingOwnerId;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                AssemblyName definition;
                try
                {
                    definition = assembly.GetName();
                }
                catch
                {
                    continue;
                }

                if (!ModAssemblyResolutionCatalog.IdentityMatches(requested, definition))
                {
                    continue;
                }

                if (!assemblyOwners.TryGetValue(assembly, out var candidateOwner))
                {
                    candidateOwner = ResolveRequesterOwner(assembly);
                }

                // Runtime/framework assemblies are globally visible. Private mod assemblies are visible only
                // to their owner and that owner's explicit dependency consumers.
                if (candidateOwner == null ||
                    (requesterOwner != null &&
                     assemblyCatalog?.IsOwnerVisible(requesterOwner, candidateOwner) == true))
                {
                    return assembly;
                }
            }

            var candidate = assemblyCatalog?.FindCandidate(requesterOwner, requested);
            if (candidate == null)
            {
                return null;
            }

            var resolved = Assembly.LoadFrom(candidate);
            if (assemblyCatalog!.TryGetOwner(candidate, out var resolvedOwner))
            {
                RegisterAssemblyOwner(resolved, resolvedOwner);
            }

            return resolved;
        }

        private static Exception CombineCleanupFailures(Exception? current, Exception next)
        {
            return current == null ? next : new AggregateException(current, next);
        }

        private static Exception UnwrapInvocationException(Exception exception)
        {
            while (exception is TargetInvocationException invocation && invocation.InnerException != null)
            {
                exception = invocation.InnerException;
            }

            return exception;
        }

        private string? ResolveRequesterOwner(Assembly? assembly)
        {
            if (assembly == null)
            {
                return null;
            }

            if (assemblyOwners.TryGetValue(assembly, out var owner))
            {
                return owner;
            }

            try
            {
                return assemblyCatalog != null && assemblyCatalog.TryGetOwner(assembly.Location, out owner)
                    ? owner
                    : null;
            }
            catch
            {
                return null;
            }
        }

        private void RegisterAssemblyOwner(Assembly assembly, string owner)
        {
            if (assemblyOwners.TryGetValue(assembly, out var existingOwner) &&
                !string.Equals(existingOwner, owner, StringComparison.OrdinalIgnoreCase))
            {
                throw new FileLoadException("Assembly '" + assembly.FullName + "' is already owned by "
                    + existingOwner + " and cannot also be loaded for " + owner + ".");
            }

            assemblyOwners[assembly] = owner;
        }

        private void CleanupOwnedFrameworkServices(string ownerModId)
        {
            try
            {
                sceneCoordinator.ReleaseOwner(ownerModId);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Scene claim cleanup failed for " + ownerModId + ".");
            }
        }
    }
}
