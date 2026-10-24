namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitEventSystemTests
    {
        // A contract double, not Unity implementation code: enabled-system ordering, scene roots, object lifetime,
        // and caller-owned list overloads only. The array-returning search deliberately allocates like its API requires.
        private const string EventSystemUnityContract = """
            using System;
            using System.Collections.Generic;
            using UnityEngine.SceneManagement;

            namespace UnityEngine
            {
                public enum FindObjectsInactive { Exclude }
                public enum FindObjectsSortMode { None }
                public class Object
                {
                    public bool Destroyed;
                    private static bool Dead(Object? value) => ReferenceEquals(value, null) || value.Destroyed
                        || value is Component component && component.gameObject.Destroyed;
                    public static bool operator ==(Object? a, Object? b) => Dead(a) ? Dead(b) : !Dead(b) && ReferenceEquals(a, b);
                    public static bool operator !=(Object? a, Object? b) => !(a == b);
                    public override bool Equals(object? other) => ReferenceEquals(this, other);
                    public override int GetHashCode() => base.GetHashCode();
                    public static void Destroy(GameObject target)
                    {
                        target.Destroyed = true;
                        target.scene.Data.Roots.Remove(target);
                    }
                    public static void DontDestroyOnLoad(GameObject target)
                    {
                        target.scene.Data.Roots.Remove(target);
                        target.scene = SceneManager.Persistent;
                        target.scene.Data.Roots.Add(target);
                    }
                    public static T[] FindObjectsByType<T>(FindObjectsInactive inactive, FindObjectsSortMode sort) where T : Object
                    {
                        var found = new List<T>();
                        foreach (var system in EventSystems.EventSystem.All)
                            if (system != null && system.gameObject.activeInHierarchy && system is T match) found.Add(match);
                        return found.ToArray();
                    }
                }
                public class Component : Object
                {
                    public GameObject gameObject = null!;
                }
                public class Behaviour : Component
                {
                    public bool enabled = true;
                    public bool isActiveAndEnabled => this != null && enabled && gameObject.activeInHierarchy;
                }
                public class GameObject : Object
                {
                    private readonly List<Component> components = new List<Component>();
                    private bool active = true;
                    public Scene scene;
                    public GameObject(string name)
                    {
                        scene = SceneManager.GetSceneAt(0);
                        scene.Data.Roots.Add(this);
                    }
                    public bool activeInHierarchy => !Destroyed && active;
                    public void SetActive(bool value) => active = value;
                    public T AddComponent<T>() where T : Component, new()
                    {
                        var component = new T { gameObject = this };
                        components.Add(component);
                        if (component is EventSystems.EventSystem system) EventSystems.EventSystem.All.Add(system);
                        return component;
                    }
                    public void GetComponentsInChildren<T>(bool includeInactive, List<T> into) where T : Component
                    {
                        into.Clear();
                        if (!includeInactive && !activeInHierarchy) return;
                        foreach (var component in components) if (component is T item) into.Add(item);
                    }
                }
                public static class Time { public static double unscaledTimeAsDouble; }
            }
            namespace UnityEngine.EventSystems
            {
                public class EventSystem : Behaviour
                {
                    public static readonly List<EventSystem> All = new List<EventSystem>();
                    public static EventSystem? current
                    {
                        get
                        {
                            foreach (var system in All) if (system.isActiveAndEnabled) return system;
                            return null;
                        }
                    }
                }
                public class StandaloneInputModule : Behaviour { }
            }
            namespace UnityEngine.InputSystem.UI { public class InputSystemUIInputModule : Behaviour { } }
            namespace UnityEngine.UI { public class GraphicRaycaster : Behaviour { } }
            namespace UnityEngine.SceneManagement
            {
                public enum LoadSceneMode { Single }
                public sealed class SceneData { public readonly List<GameObject> Roots = new List<GameObject>(); }
                public struct Scene
                {
                    public SceneData Data;
                    public bool IsValid() => Data != null;
                    public bool isLoaded => IsValid();
                    public void GetRootGameObjects(List<GameObject> into)
                    {
                        into.Clear();
                        into.AddRange(Data.Roots);
                    }
                }
                public static class SceneManager
                {
                    private static Scene loaded = new Scene { Data = new SceneData() };
                    public static Scene Persistent = new Scene { Data = new SceneData() };
                    public static int sceneCount => 1;
                    public static Scene GetSceneAt(int index) => loaded;
                    public static event Action<Scene, LoadSceneMode>? sceneLoaded;
                    public static int Subscribers => sceneLoaded?.GetInvocationList().Length ?? 0;
                    public static void Load() => sceneLoaded?.Invoke(loaded, LoadSceneMode.Single);
                    public static void Reset()
                    {
                        loaded = new Scene { Data = new SceneData() };
                        Persistent = new Scene { Data = new SceneData() };
                    }
                }
            }
            namespace TopiaForge.Mods.UnityUi
            {
                internal static class TopiaForgeInput { public static bool LegacyAvailable => true; }
                internal static class TopiaForgeLog { public static void Info(string value) { } }
            }
            """;
    }
}
