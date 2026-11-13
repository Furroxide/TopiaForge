using System;
using System.Linq.Expressions;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.RobotKit
{
    // Verified publications cache their native identities. Unity lifetime is checked every frame,
    // and incomplete samples are retried during a short grace period. A candidate that stays unverified
    // past the grace period stops constructing; its live actors keep running until a definite change.
    internal sealed class RobotComposedCurrentness
    {
        private const double VerificationGraceSeconds = 2;

        private readonly Func<WorldSessionSnapshot?> world;
        private readonly WorldSessionSnapshot original;
        private readonly Component host;
        private readonly object config, sceneRoot;
        private readonly Transform mapRoot;
        private readonly Func<object, object?> readConfig, readSceneRoot, readMapRoot;
        private WorldSessionSnapshot? verified;
        private double? verifyBy;
        private bool retired, expired;

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

        internal bool IsVerified => !retired && !expired && verified != null && ReferenceEquals(verified, world());

        // Catalogue eligibility. Live actors poll IsCurrent instead, so a verification time-out
        // removes the candidate without destroying robots that are already running.
        internal bool AdmitsSpawns() => IsCurrent() && !expired;

        internal void RequireVerified()
        {
            if (IsCurrent() && IsVerified) return;
            RobotKitDiagnostics.ReportComposedDiscovery(original, RobotComposedRefusal.ContextUnverified);
            throw new RobotComposedContextUnverifiedException();
        }

        internal bool IsCurrent()
        {
            if (retired) return false;
            var state = world();
            if (state == null || (state.Phase != WorldSessionPhase.StartingMode && state.Phase != WorldSessionPhase.Running)
                || state.Session?.SessionId != original.Session?.SessionId
                // ContextWorldSessionService rebinds readiness/map wrappers on each publication, retaining Scene
                // and the map's lifetime. The original map must still be alive; a successor cannot adopt this actor.
                || !ReferenceEquals(state.World?.Scene, original.World!.Scene) || original.World.Map?.IsAlive != true
                || state.World?.Scene.InstanceId != original.World.Scene.InstanceId || state.World.Map?.IsAlive != true)
                return Retire(RobotComposedRetirement.WorldChanged);
            if (host == null || mapRoot == null || host.gameObject.scene.handle != state.World.Scene.InstanceId
                || mapRoot.gameObject.scene.handle != state.World.Scene.InstanceId)
                return Retire(RobotComposedRetirement.NativeLifetimeEnded);
            if (ReferenceEquals(verified, state)) return true;

            // Read all three: a null in one must not hide a definite replacement in another.
            object? currentConfig, currentSceneRoot, currentMapRoot;
            try
            {
                currentConfig = readConfig(host);
                currentSceneRoot = readSceneRoot(host);
                currentMapRoot = readMapRoot(sceneRoot);
            }
            catch (Exception)
            {
                return Retire(RobotComposedRetirement.NativeIdentityReadFailed);
            }
            if ((currentConfig != null && !ReferenceEquals(currentConfig, config))
                || (currentSceneRoot != null && !ReferenceEquals(currentSceneRoot, sceneRoot))
                || (currentMapRoot != null && !ReferenceEquals(currentMapRoot, mapRoot)))
                return Retire(RobotComposedRetirement.NativeIdentityChanged);

            if (!expired && verifyBy is double deadline && Time.unscaledTimeAsDouble >= deadline)
            {
                expired = true;
                verifyBy = null;
                RobotKitDiagnostics.ReportComposedRetirement(original, RobotComposedRetirement.VerificationTimedOut);
            }
            if (currentConfig != null && currentSceneRoot != null && currentMapRoot != null)
            {
                verified = state;
                verifyBy = null;
            }
            else
            {
                verified = null;
                if (!expired) verifyBy ??= Time.unscaledTimeAsDouble + VerificationGraceSeconds;
            }
            return true;
        }

        private bool Retire(RobotComposedRetirement reason)
        {
            retired = true;
            RobotKitDiagnostics.ReportComposedRetirement(original, reason);
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

    internal sealed class RobotComposedContextUnverifiedException : Exception { }
}
