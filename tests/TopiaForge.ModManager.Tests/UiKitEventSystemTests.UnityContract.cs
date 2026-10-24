namespace TopiaForge.ModManager.Tests
{
    internal static partial class UiKitEventSystemTests
    {
        // A contract double, not Unity implementation code: enabled-system ordering, scene roots, parenting, object
        // lifetime, caller-owned list overloads, and a selection that needs a current event system only. The array-returning
        // search deliberately allocates like its API requires.
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
                    // As in Unity, destroying an object destroys its children with it.
                    public static void Destroy(GameObject target)
                    {
                        target.transform.Detach();
                        target.MarkDestroyed();
                    }
                    public static void DontDestroyOnLoad(GameObject target)
                    {
                        target.transform.Detach();
                        target.MoveTo(SceneManager.Persistent);
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
                    public Transform transform => gameObject.transform;
                }
                public class Behaviour : Component
                {
                    public bool enabled = true;
                    public bool isActiveAndEnabled => this != null && enabled && gameObject.activeInHierarchy;
                }
                // Parenting as in Unity: a child is in its parent's scene and active only while its parent is, and once
                // unparented it is a root again in the same scene.
                public class Transform : Component
                {
                    internal readonly List<Transform> Children = new List<Transform>();
                    public Transform? parent { get; private set; }
                    public int childCount => Children.Count;
                    public Transform GetChild(int index) => Children[index];
                    public void SetParent(Transform? value)
                    {
                        Detach();
                        parent = value;
                        if (value == null)
                        {
                            gameObject.scene.Data.Roots.Add(gameObject);
                        }
                        else
                        {
                            value.Children.Add(this);
                            gameObject.MoveTo(value.gameObject.scene);
                        }
                    }
                    internal void Detach()
                    {
                        if (parent != null) parent.Children.Remove(this);
                        else gameObject.scene.Data.Roots.Remove(gameObject);
                        parent = null;
                    }
                }
                public class GameObject : Object
                {
                    private readonly List<Component> components = new List<Component>();
                    private bool active = true;
                    public Scene scene;
                    public string name;
                    public GameObject(string name)
                    {
                        this.name = name;
                        transform = new Transform { gameObject = this };
                        scene = SceneManager.GetSceneAt(0);
                        scene.Data.Roots.Add(this);
                    }
                    public Transform transform { get; }
                    public bool activeInHierarchy =>
                        !Destroyed && active && (transform.parent == null || transform.parent.gameObject.activeInHierarchy);
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
                        Collect(includeInactive, into);
                    }
                    private void Collect<T>(bool includeInactive, List<T> into) where T : Component
                    {
                        if (!includeInactive && !activeInHierarchy) return;
                        foreach (var component in components) if (component is T item) into.Add(item);
                        foreach (var child in transform.Children) child.gameObject.Collect(includeInactive, into);
                    }
                    internal void MoveTo(Scene value)
                    {
                        scene = value;
                        foreach (var child in transform.Children) child.gameObject.MoveTo(value);
                    }
                    internal void MarkDestroyed()
                    {
                        Destroyed = true;
                        foreach (var child in transform.Children) child.gameObject.MarkDestroyed();
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
                    public GameObject? currentSelectedGameObject { get; private set; }
                    public void SetSelectedGameObject(GameObject? selected) => currentSelectedGameObject = selected;
                }
                public class StandaloneInputModule : Behaviour { }
            }
            namespace UnityEngine.InputSystem.UI { public class InputSystemUIInputModule : Behaviour { } }
            namespace UnityEngine.UI
            {
                public class GraphicRaycaster : Behaviour { }
                // As in Unity, selecting does nothing while no event system is current.
                public class Selectable : Behaviour
                {
                    public void Select()
                    {
                        var current = EventSystems.EventSystem.current;
                        if (current != null) current.SetSelectedGameObject(gameObject);
                    }
                }
            }
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
