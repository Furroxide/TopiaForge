using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnitySceneBackend
    {
        private CheckpointSnapshot? ResolveCheckpoint()
        {
            try
            {
                var type = ResolveCheckpointManagerType();
                if (type == null)
                {
                    return null;
                }

                var manager = ReadMember(null, type, "Instance", "instance", "Current")
                    ?? Resources.FindObjectsOfTypeAll(type).FirstOrDefault();
                var checkpoint = ReadMember(manager, type, "CurrentCheckpoint", "currentCheckpoint", "Checkpoint")
                    ?? ReadMember(null, type, "CurrentCheckpoint", "currentCheckpoint", "Checkpoint");
                if (checkpoint == null)
                {
                    return null;
                }

                var checkpointType = checkpoint.GetType();
                var id = ReadString(checkpoint, checkpointType, "CheckpointId", "checkpointId", "Id", "id");
                if (string.IsNullOrWhiteSpace(id) && checkpoint is UnityEngine.Object native)
                {
                    id = native.name;
                }

                if (string.IsNullOrWhiteSpace(id))
                {
                    return null;
                }

                var sceneName = ReadString(checkpoint, checkpointType, "SceneName", "sceneName");
                if (string.IsNullOrWhiteSpace(sceneName))
                {
                    sceneName = SceneManager.GetActiveScene().name ?? string.Empty;
                }

                var position = checkpoint is Component component
                    ? component.transform.position
                    : ReadVector(checkpoint, checkpointType, "Position", "position");
                return new CheckpointSnapshot(id, sceneName, UnityPhysicsBackend.FromUnity(position));
            }
            catch
            {
                return null;
            }
        }

        private Type? ResolveCheckpointManagerType()
        {
            if (checkpointTypeResolved)
            {
                return checkpointManagerType;
            }

            checkpointTypeResolved = true;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    checkpointManagerType = assembly.GetTypes().FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, "CheckpointManager", StringComparison.Ordinal));
                    if (checkpointManagerType != null)
                    {
                        break;
                    }
                }
                catch (ReflectionTypeLoadException exception)
                {
                    checkpointManagerType = exception.Types.FirstOrDefault(candidate => candidate != null
                        && string.Equals(candidate.Name, "CheckpointManager", StringComparison.Ordinal));
                    if (checkpointManagerType != null)
                    {
                        break;
                    }
                }
            }

            return checkpointManagerType;
        }

        private static object? ReadMember(object? instance, Type type, params string[] names)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic
                | (instance == null ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var name in names)
            {
                var value = type.GetProperty(name, flags)?.GetValue(instance, null)
                    ?? type.GetField(name, flags)?.GetValue(instance);
                if (value != null)
                {
                    return value;
                }
            }

            return null;
        }

        private static string ReadString(object instance, Type type, params string[] names) =>
            ReadMember(instance, type, names) as string ?? string.Empty;

        private static Vector3 ReadVector(object instance, Type type, params string[] names) =>
            ReadMember(instance, type, names) is Vector3 value ? value : Vector3.zero;

        private sealed class CheckpointSubscription : IDisposable
        {
            private Action? unsubscribe;

            public CheckpointSubscription(Action? unsubscribe)
            {
                this.unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
            }
        }
    }
}
