using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;
using UnityEngine;

namespace TopiaForge.SandboxAcceptance.Native
{
    // This test-only observer reads a fixed allowlist of production fields. Missing/version-changed
    // fields are unavailable evidence, never a substitute zero or a successful lifecycle claim.
    internal static class NativeOwnerObservations
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static object? Unwrap(object? value, string expected)
        {
            if (value?.GetType().FullName == expected + "+OwnerFacade")
                value = value.GetType().GetField("service", Private)?.GetValue(value);
            return value?.GetType().FullName == expected ? value : null;
        }
        internal static object[] Content(IModContext context, object? facade, List<string> unavailable)
        {
            var service = Unwrap(facade, "TopiaForge.CreatorContent.CreatorContentService");
            var sessions = service?.GetType().GetField("sessions", Private)?.GetValue(service) as IEnumerable;
            if (sessions == null) { unavailable.Add("sandbox-content-observation-unavailable"); return Array.Empty<object>(); }
            var result = new List<object>();
            foreach (var session in sessions)
            {
                if (session?.GetType().FullName != "TopiaForge.CreatorContent.CreatorSession") throw new InvalidDataException("Unexpected creator session type.");
                if (!Equals(session.GetType().GetField("ownerId", Private)?.GetValue(session), NativeSandboxObservations.SandboxOwner)) continue;
                var handles = session.GetType().GetField("instances", Private)?.GetValue(session) as IEnumerable;
                if (handles == null) { unavailable.Add("sandbox-instance-observation-unavailable"); continue; }
                foreach (var handle in handles)
                {
                    if (!(handle is ICreatorSpawnHandle spawn)) throw new InvalidDataException("Unexpected creator spawn handle.");
                    if (result.Count == 4096) throw new InvalidDataException("Creator observation bound exceeded.");
                    var item = Entity(context, spawn.Entity, unavailable);
                    item["contentId"] = spawn.Descriptor.ContentId;
                    item["scope"] = "sandbox-owner";
                    result.Add(item);
                }
            }
            return result.ToArray();
        }
        internal static object[] Robots(IModContext context, List<string> unavailable)
        {
            if (!context.Extensions.TryGet<IRobotAgentService>(out var service) || service == null)
            { unavailable.Add("robot-agent-observation-unavailable"); return Array.Empty<object>(); }
            if (service.ActiveAgents.Count > 4096) throw new InvalidDataException("Robot observation bound exceeded.");
            return service.ActiveAgents.Select(agent => { var item = Entity(context, agent, unavailable); item["scope"] = "global-robotkit";
                item["brainMode"] = agent.BrainMode.ToString(); return (object)item; }).ToArray();
        }
        private static Dictionary<string, object?> Entity(IModContext context, IEntity entity, List<string> unavailable)
        {
            var native = context.RequireUnityInterop().TryGetGameObject(entity, out var go) && go != null ? go : null;
            if (entity.IsAlive && native == null) unavailable.Add("live-entity-native-identity-unavailable:" + entity.Id);
            return new Dictionary<string, object?> { ["id"] = entity.Id, ["alive"] = entity.IsAlive,
                ["instanceId"] = native != null ? native.GetInstanceID() : (object?)null,
                ["sceneHandle"] = native != null ? native.scene.handle : (object?)null,
                ["transform"] = native != null ? NativeFixtureObjects.Transform(native.transform) : Array.Empty<float>() };
        }
        internal static int? PlayerControls(IModContext context, List<string> unavailable)
        {
            try
            {
                object value = context.LocalPlayer;
                if (value.GetType().FullName != "TopiaForge.ModManager.ModContext+LifetimePlayerService") throw new InvalidDataException();
                value = value.GetType().GetField("inner", Private)!.GetValue(value)!;
                if (value.GetType().FullName != "TopiaForge.ModManager.OwnerPlayerService") throw new InvalidDataException();
                value = value.GetType().GetField("backend", Private)!.GetValue(value)!;
                if (value.GetType().FullName != "TopiaForge.ModManager.UnityPlayerBackend") throw new InvalidDataException();
                return (int)value.GetType().GetField("leases", Private)!.GetValue(value)!;
            }
            catch { unavailable.Add("player-control-counter-unavailable"); return null; }
        }
        internal static object[] Interactions()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "TopiaForge.ModManager");
            var type = assembly?.GetType("TopiaForge.ModManager.UnityInteractionBridge", false);
            if (type == null) throw new InvalidDataException("Production interaction bridge type is unavailable.");
            var components = Resources.FindObjectsOfTypeAll(type);
            if (components.Length > 4096) throw new InvalidDataException("Interaction observation bound exceeded.");
            return components.OfType<Component>().Where(c => c != null).Select(c => (object)new Dictionary<string, object?> {
                ["instanceId"] = c.GetInstanceID(), ["entityInstanceId"] = c.gameObject.GetInstanceID(),
                ["active"] = (bool)type.GetProperty("IsActive")!.GetValue(c)!, ["scope"] = "global-runtime" }).ToArray();
        }
        internal static Task<(bool Ok, string Message)> StartSandbox()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "TopiaForge.ModManager");
            var type = assembly?.GetType("TopiaForge.ModManager.TopiaForgeModManagerPlugin", false);
            if (type == null) throw new InvalidDataException("Actual manager launch control unavailable.");
            var plugins = Resources.FindObjectsOfTypeAll(type);
            if (plugins.Length != 1) throw new InvalidDataException("One actual manager is required.");
            var method = type.GetMethod("LaunchTarget", new[] { typeof(string), typeof(string), typeof(string) });
            // Fixed call to the same public manager command used by GamemodesTab. No wire-provided
            // target, reflection name, world override or transition can enter this narrow operation.
            return method?.Invoke(plugins[0], new object?[] { SandboxAcceptanceMod.SandboxTargetId, null, null }) as Task<(bool, string)>
                ?? throw new InvalidDataException("Existing manager Sandbox launch command unavailable.");
        }
        internal static string ManagerSession()
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().SingleOrDefault(a => a.GetName().Name == "TopiaForge.ModManager");
            var type = assembly?.GetType("TopiaForge.ModManager.TopiaForgeModManagerPlugin", false);
            if (type == null) throw new InvalidDataException("Actual manager type is unavailable.");
            var plugins = Resources.FindObjectsOfTypeAll(type);
            if (plugins.Length != 1) throw new InvalidDataException("One actual manager instance is required.");
            if (!(type.GetProperty("Paths")?.GetValue(plugins[0]) is ManagerPaths paths)) throw new InvalidDataException("Actual manager paths are unavailable.");
            // The path comes from the live manager, never from a wire request or fixture configuration.
            using var stream = new FileStream(paths.LastRunFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length < 2 || stream.Length > 524288) throw new InvalidDataException("Manager report bound exceeded.");
            using var reader = new StreamReader(stream, SandboxWireCodec.Utf8);
            var report = JsonUtil.Deserialize<LastRunReport>(reader.ReadToEnd());
            if (report.SchemaVersion != 1 || !Guid.TryParseExact(report.SessionId, "N", out _)) throw new InvalidDataException("Actual manager session identity is unavailable.");
            return report.SessionId;
        }
    }
}
