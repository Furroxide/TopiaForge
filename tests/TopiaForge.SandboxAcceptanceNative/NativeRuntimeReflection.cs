using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.SandboxAcceptance.Native
{
    // Allowlisted reflection into production runtime state with exact type names and fixed member names. Every
    // missing or renamed link is an unavailable reason; no branch substitutes a default value for a measurement.
    internal static class NativeRuntimeReflection
    {
        private const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private const BindingFlags Public = BindingFlags.Instance | BindingFlags.Public;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>The live SandboxController reached through the current Worlds session, or null while no Sandbox controller runs.</summary>
        internal static object? Controller(IWorldSessionService? worlds, List<string> unavailable)
        {
            if (worlds == null) { NativeOwnerObservations.AddOnce(unavailable, "world-session-service-unavailable"); return null; }
            var current = worlds.Current;
            if (current.Phase == WorldSessionPhase.Idle || current.Session == null) return null;
            try
            {
                var session = Expect(current.Session, "TopiaForge.ModManager.ContextWorldSessionService+BoundSession");
                var inner = Expect(Field(session, "session"), "TopiaForge.ModManager.GamemodeSessionOrchestrator+BoundWorldSession");
                var owner = Expect(Field(inner, "owner"), "TopiaForge.ModManager.GamemodeSessionOrchestrator");
                var record = Field(owner, "current");
                if (record == null) return null;
                Expect(record, "TopiaForge.ModManager.GamemodeSessionOrchestrator+SessionRecord");
                if (!(Field(record, "Identity") is SessionIdentity identity)) throw new InvalidDataException("Session identity is unavailable.");
                if (identity.SessionId != current.Session.SessionId) return null;
                var controller = Field(record, "Controller");
                if (controller == null) return null;
                if (controller.GetType().FullName != "TopiaForge.Sandbox.SandboxController")
                {
                    NativeOwnerObservations.AddOnce(unavailable, "sandbox-controller-type-mismatch:" + controller.GetType().Name);
                    return null;
                }
                return controller;
            }
            catch (InvalidDataException) { NativeOwnerObservations.AddOnce(unavailable, "sandbox-controller-observation-unavailable"); return null; }
        }

        internal static int? ControllerInstanceId(object? controller) => controller == null ? (int?)null : RuntimeHelpers.GetHashCode(controller);

        internal static object? Workbench(object? controller, List<string> unavailable)
        {
            if (controller == null) return null;
            try { return Expect(Field(controller, "workbench"), "TopiaForge.CreatorTools.Shared.CreatorWorkbench"); }
            catch (InvalidDataException) { NativeOwnerObservations.AddOnce(unavailable, "sandbox-workbench-observation-unavailable"); return null; }
        }

        internal static bool? ProjectRunning(object? workbench, List<string> unavailable)
        {
            if (workbench == null) { NativeOwnerObservations.AddOnce(unavailable, "sandbox-workbench-unavailable"); return null; }
            try
            {
                var runner = Field(workbench, "runner");
                if (runner == null) return false;
                Expect(runner, "TopiaForge.CreatorTools.Shared.CreatorEventGraphRunner");
                if (!(runner.GetType().GetProperty("IsRunning", Public)?.GetValue(runner) is bool running)) throw new InvalidDataException("Runner state is unavailable.");
                return running;
            }
            catch (InvalidDataException) { NativeOwnerObservations.AddOnce(unavailable, "project-runner-observation-unavailable"); return null; }
        }

        internal static int? UndoDepth(object? workbench, List<string> unavailable)
        {
            if (workbench == null) { NativeOwnerObservations.AddOnce(unavailable, "sandbox-workbench-unavailable"); return null; }
            try
            {
                var history = Field(workbench, "history") ?? throw new InvalidDataException("History is unavailable.");
                if (!(history.GetType().GetProperty("Count", Public)?.GetValue(history) is int count) || count < 0 || count > 65536)
                    throw new InvalidDataException("History count is unavailable.");
                return count;
            }
            catch (InvalidDataException) { NativeOwnerObservations.AddOnce(unavailable, "workbench-history-observation-unavailable"); return null; }
        }

        // GameCode UIState.InteractTarget: the production bridge sets the interact prompt from this listenable.
        private static readonly Lazy<MemberInfo?> InteractTargetMember = new Lazy<MemberInfo?>(() =>
        {
            try
            {
                var type = Type.GetType("UIState, GameCode", throwOnError: false);
                if (type == null)
                {
                    var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "GameCode");
                    type = assembly?.GetType("UIState", false);
                }
                return (MemberInfo?)type?.GetProperty("InteractTarget", Static) ?? type?.GetField("InteractTarget", Static);
            }
            catch (Exception) { return null; }
        });

        /// <summary>Reads the game's current interact target; false when the bridge cannot be observed at all.</summary>
        internal static bool TryGetInteractTarget(out object? target)
        {
            target = null;
            var member = InteractTargetMember.Value;
            if (member == null) return false;
            var listenable = member is PropertyInfo property ? property.GetValue(null) : ((FieldInfo)member).GetValue(null);
            if (listenable == null) return false;
            var value = listenable.GetType().GetProperty("Value", Public);
            if (value != null) { target = value.GetValue(listenable); return true; }
            var field = listenable.GetType().GetField("Value", Any);
            if (field == null) return false;
            target = field.GetValue(listenable);
            return true;
        }

        internal static int? InteractableInstanceId(object? interactable)
        {
            if (interactable is Component component) return component != null ? component.gameObject.GetInstanceID() : (int?)null;
            var native = interactable?.GetType().GetProperty("GameObject", Public)?.GetValue(interactable) as GameObject;
            return native != null ? native.GetInstanceID() : (int?)null;
        }

        internal static int[]? PersonalityAssetIds(List<string> unavailable)
        {
            Type? type;
            try { type = Type.GetType("PersonalityAsset, GameCode", throwOnError: false); }
            catch (Exception) { type = null; }
            if (type == null) { NativeOwnerObservations.AddOnce(unavailable, "personality-asset-type-unavailable"); return null; }
            var assets = Resources.FindObjectsOfTypeAll(type);
            if (assets.Length > NativeFactShapes.ListBound) { NativeOwnerObservations.AddOnce(unavailable, "personality-asset-bound-exceeded"); return null; }
            return assets.Where(asset => asset != null).Select(asset => asset.GetInstanceID()).OrderBy(id => id).ToArray();
        }

        private static object? Field(object? owner, string name)
        {
            if (owner == null) throw new InvalidDataException("Missing owner for field " + name + ".");
            var field = owner.GetType().GetField(name, Any) ?? throw new InvalidDataException("Missing field " + name + ".");
            return field.GetValue(owner);
        }

        private static object Expect(object? value, string fullName)
        {
            if (value == null || value.GetType().FullName != fullName) throw new InvalidDataException("Expected " + fullName + ".");
            return value;
        }
    }
}
