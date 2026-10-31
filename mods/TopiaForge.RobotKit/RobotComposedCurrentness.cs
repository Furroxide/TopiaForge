using System;
using System.Linq.Expressions;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Observation snapshots are immutable. Native identities are sampled only at publication boundaries;
    // Unity lifetime remains checked every frame. A null sample is incomplete, never proof of replacement.
    internal sealed class RobotComposedCurrentness
    {
        private readonly Func<WorldSessionSnapshot?> world;
        private readonly WorldSessionSnapshot original;
        private readonly Component host;
        private readonly object config, sceneRoot;
        private readonly Transform mapRoot;
        private readonly Func<object, object?> readConfig, readSceneRoot, readMapRoot;
        private WorldSessionSnapshot? verified;
        private bool retired;

        internal RobotComposedCurrentness(Func<WorldSessionSnapshot?> world, WorldSessionSnapshot state,
            Component host, object config, object sceneRoot, Transform mapRoot)
        {
            this.world = world; original = state; this.host = host; this.config = config;
            this.sceneRoot = sceneRoot; this.mapRoot = mapRoot;
            readConfig = Getter(host.GetType(), "RuntimeAssetConfig", config.GetType());
            readSceneRoot = Getter(host.GetType(), "SceneRoot", sceneRoot.GetType());
            readMapRoot = Getter(sceneRoot.GetType(), "ActiveImportRoot", typeof(Transform));
            // Discovery just read and validated these exact identities in this snapshot.
            verified = state;
        }

        internal bool IsVerified => !retired && ReferenceEquals(verified, world());

        internal bool IsCurrent()
        {
            if (retired) return false;
            var state = world();
            if (state == null || (state.Phase != WorldSessionPhase.StartingMode && state.Phase != WorldSessionPhase.Running)
                || state.Session?.SessionId != original.Session?.SessionId
                // ContextWorldSessionService rebinds readiness/map wrappers on each publication, retaining Scene
                // and the map's lifetime. The original map must still be alive; a successor cannot adopt this actor.
                || !ReferenceEquals(state.World?.Scene, original.World!.Scene) || original.World.Map?.IsAlive != true
                || state.World?.Scene.InstanceId != original.World.Scene.InstanceId || state.World.Map?.IsAlive != true
                || host == null || mapRoot == null || host.gameObject.scene.handle != state.World.Scene.InstanceId
                || mapRoot.gameObject.scene.handle != state.World.Scene.InstanceId)
                return Retire();
            if (ReferenceEquals(verified, state)) return true;

            // Read all three: a null in one must not hide a definite replacement in another.
            var currentConfig = readConfig(host);
            var currentSceneRoot = readSceneRoot(host);
            var currentMapRoot = readMapRoot(sceneRoot);
            if ((currentConfig != null && !ReferenceEquals(currentConfig, config))
                || (currentSceneRoot != null && !ReferenceEquals(currentSceneRoot, sceneRoot))
                || (currentMapRoot != null && !ReferenceEquals(currentMapRoot, mapRoot))) return Retire();
            if (currentConfig != null && currentSceneRoot != null && currentMapRoot != null) verified = state;
            // An incomplete publication may keep existing actors, but cannot authorize construction.
            return true;
        }

        private bool Retire()
        {
            retired = true;
            RobotKitDiagnostics.ReportOnce("composed robot context retired", new InvalidOperationException());
            return false;
        }

        private static Func<object, object?> Getter(Type owner, string name, Type result)
        {
            var property = owner.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            var getter = property?.GetGetMethod();
            if (property == null || property.PropertyType != result || property.GetIndexParameters().Length != 0
                || getter == null || getter.IsStatic)
                throw new InvalidOperationException("Native composed robot getter is unavailable.");
            var instance = Expression.Parameter(typeof(object), "instance");
            return Expression.Lambda<Func<object, object?>>(Expression.Convert(
                Expression.Call(Expression.Convert(instance, owner), getter), typeof(object)), instance).Compile();
        }
    }
}
