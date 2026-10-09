using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.Prompts
{
    /// <summary>
    /// Adds the effective owner-scoped global directive to native autonomous robot planning requests. The patch is
    /// deliberately a narrow adapter: the game's system prompt, environment, action schema, and personality remain
    /// untouched, and a missing or changed native symbol only disables this bridge.
    /// </summary>
    internal sealed class NativeRobotPromptBridge : IDisposable
    {
        private const string PatchPurpose = "global-robot-directive";

        private static NativeRobotPromptBridge? active;

        private readonly IPromptOverrideRegistry registry;
        private readonly IModLogger logger;
        private readonly IHarmonyLease patches;
        private int disposed;
        private int applyFailureReported;
        private int oversizedDirectiveReported;

        private NativeRobotPromptBridge(
            IPromptOverrideRegistry registry,
            IModLogger logger,
            IHarmonyLease patches)
        {
            this.registry = registry;
            this.logger = logger;
            this.patches = patches;
        }

        public static NativeRobotPromptBridge? TryInstall(IModContext context, IPromptOverrideRegistry registry)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (registry == null)
            {
                throw new ArgumentNullException(nameof(registry));
            }

            IHarmonyLease? patches = null;
            NativeRobotPromptBridge? bridge = null;
            try
            {
                patches = context.CreateHarmonyLease(PatchPurpose);
                var target = ResolvePlanRequestConstructor();
                var postfix = typeof(NativeRobotPromptBridge).GetMethod(
                    nameof(AppendGlobalRobotDirective),
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (target == null || postfix == null)
                {
                    SafeLog(context.Logger, log => log.Warn(
                        "TopiaForge Prompts: native robot directive bridge is unavailable on this game build; " +
                        "prompt registration remains active."));
                    SafeDispose(patches, context.Logger);
                    return null;
                }

                bridge = new NativeRobotPromptBridge(registry, context.Logger, patches);
                if (Interlocked.CompareExchange(ref active, bridge, null) != null)
                {
                    SafeLog(context.Logger, log => log.Warn(
                        "TopiaForge Prompts: a native robot directive bridge is already active; " +
                        "the duplicate bridge was not installed."));
                    SafeDispose(patches, context.Logger);
                    return null;
                }

                try
                {
                    patches.Patch(target, postfix: postfix);
                }
                catch
                {
                    Interlocked.CompareExchange(ref active, null, bridge);
                    throw;
                }

                SafeLog(context.Logger, log => log.Info(
                    "TopiaForge Prompts: native autonomous robot directive bridge is active."));
                return bridge;
            }
            catch (Exception ex)
            {
                if (bridge != null)
                {
                    Interlocked.CompareExchange(ref active, null, bridge);
                }

                SafeLog(context.Logger, log => log.Warn(
                    "TopiaForge Prompts could not install the native robot directive bridge; " +
                    "prompt registration remains active. " + ex.Message));
                if (patches != null)
                {
                    SafeDispose(patches, context.Logger);
                }

                return null;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            Interlocked.CompareExchange(ref active, null, this);
            SafeDispose(patches, logger);
        }

        // Pin only the parameters that actually discriminate the planning constructor, and ignore any the game
        // adds later. An exact full-arity match compiles and loads fine but silently stops matching the moment
        // Robotopia appends one field — the postfix then never applies and robot directives quietly stop
        // reaching planning. Robotopia build 2409's model-provider failover work is exactly that kind of change.
        private static ConstructorInfo? ResolvePlanRequestConstructor()
        {
            ConstructorInfo? best = null;
            foreach (var candidate in typeof(global::RoboAPI.Agent.PlanRequest).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                var parameters = candidate.GetParameters();
                if (parameters.Length < 2
                    || parameters[0].ParameterType != typeof(global::ModelAsset)
                    || parameters[1].ParameterType != typeof(global::AgentEnvironment))
                {
                    continue;
                }

                // Prefer the widest match so a narrower convenience overload never shadows the real one.
                if (best == null || parameters.Length > best.GetParameters().Length)
                {
                    best = candidate;
                }
            }

            return best;
        }

        // Harmony postfix. Every exception is contained here so a registry/provider failure can never break the
        // native request constructor or prevent a robot from planning normally.
        private static void AppendGlobalRobotDirective(global::RoboAPI.Agent.PlanRequest __instance)
        {
            var bridge = Volatile.Read(ref active);
            if (bridge == null || Volatile.Read(ref bridge.disposed) != 0 || __instance == null)
            {
                return;
            }

            try
            {
                bridge.Apply(__instance);
            }
            catch (Exception ex)
            {
                bridge.ReportApplyFailure(ex);
            }
        }

        private void Apply(global::RoboAPI.Agent.PlanRequest request)
        {
            if (!registry.TryGetEffectiveOverride(
                    WellKnownPromptIds.GlobalRobotDirective,
                    out var effective) ||
                effective == null)
            {
                return;
            }

            var outcome = PromptDirectiveComposer.Append(
                request.bioTemplates,
                effective.ReplacementText,
                out var composed);
            if (outcome == PromptDirectiveCompositionOutcome.Appended)
            {
                request.bioTemplates = composed;
            }
            else if (outcome == PromptDirectiveCompositionOutcome.TooLong &&
                     Interlocked.Exchange(ref oversizedDirectiveReported, 1) == 0)
            {
                SafeLog(logger, log => log.Warn(
                    "TopiaForge Prompts ignored an oversized global robot directive; the maximum is " +
                    PromptDirectiveComposer.MaximumDirectiveCharacters + " characters."));
            }
        }

        private void ReportApplyFailure(Exception exception)
        {
            if (Interlocked.Exchange(ref applyFailureReported, 1) != 0)
            {
                return;
            }

            SafeLog(logger, log => log.Error(
                exception,
                "TopiaForge Prompts could not compose a native robot directive; this request was left unchanged."));
        }

        private static void SafeDispose(IHarmonyLease patches, IModLogger logger)
        {
            try
            {
                patches.Dispose();
            }
            catch (Exception ex)
            {
                SafeLog(logger, log => log.Error(
                    ex,
                    "TopiaForge Prompts could not release its native robot directive patch cleanly."));
            }
        }

        private static void SafeLog(IModLogger logger, Action<IModLogger> write)
        {
            try
            {
                write(logger);
            }
            catch
            {
                // Logging must not turn an optional compatibility bridge into a gameplay failure.
            }
        }
    }
}
