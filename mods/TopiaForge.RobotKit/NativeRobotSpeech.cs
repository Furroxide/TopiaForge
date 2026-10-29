using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.RobotKit
{
    // Intercept only RobotKit-owned heads. Every original native author call takes the consuming context's route.
    internal sealed class NativeRobotSpeech : IDisposable
    {
        private static readonly Dictionary<AgentHead, RobotAgent> agents = new Dictionary<AgentHead, RobotAgent>();
        [ThreadStatic] private static bool playing;
        private readonly IHarmonyLease lease;
        internal static bool IsInstalled { get; private set; }
        private NativeRobotSpeech(IHarmonyLease lease) { this.lease = lease; }
        internal static void Register(AgentHead head, RobotAgent agent) => agents[head] = agent;
        internal static void Release(AgentHead head) => agents.Remove(head);

        internal static void Install(IModContext context)
        {
            IHarmonyLease? lease = null;
            try
            {
                lease = context.CreateHarmonyLease("robot-speech");
                var simple = typeof(AgentHead).GetMethod("Say", new[] { typeof(string), typeof(HintGroup), typeof(TaskGraph),
                    typeof(CancellationToken), typeof(FMODAudioClipAsset), typeof(bool) });
                var named = typeof(AgentHead).GetMethod("Say", new[] { typeof(string), typeof(string), typeof(HintGroup), typeof(TaskGraph),
                    typeof(CancellationToken), typeof(FMODAudioClipAsset), typeof(bool) });
                if (simple == null || named == null) throw new InvalidOperationException();
                lease.Patch(simple, prefix: typeof(NativeRobotSpeech).GetMethod(nameof(Say), BindingFlags.NonPublic | BindingFlags.Static));
                lease.Patch(named, prefix: typeof(NativeRobotSpeech).GetMethod(nameof(SayNamed), BindingFlags.NonPublic | BindingFlags.Static));
                context.Lifetime.Track(new NativeRobotSpeech(lease));
                IsInstalled = true;
            }
            catch (Exception)
            {
                lease?.Dispose(); IsInstalled = false;
                context.Logger.Warn("Robot speech bindings are unavailable; restart with compatible native speech bindings before retrying.");
            }
        }
        private static bool Say(AgentHead __instance, string text, CancellationToken ct, FMODAudioClipAsset overrideSynth,
            bool subtitlesOnly, ref UniTask<AgentHead.SayResult> __result)
        {
            if (playing || !agents.TryGetValue(__instance, out var agent)) return true;
            __result = overrideSynth != null || subtitlesOnly ? UniTask.FromResult(AgentHead.SayResult.InvalidTarget) : Route(agent, text, ct);
            return false;
        }
        private static bool SayNamed(AgentHead __instance, string text, CancellationToken ct, FMODAudioClipAsset overrideSynth,
            bool subtitlesOnly, ref UniTask __result)
        {
            if (playing || !agents.TryGetValue(__instance, out var agent)) return true;
            __result = overrideSynth != null || subtitlesOnly ? UniTask.CompletedTask : RouteNamed(agent, text, ct);
            return false;
        }
        private static async UniTask<AgentHead.SayResult> Route(RobotAgent agent, string text, CancellationToken token)
        {
            try
            {
                var result = await agent.RouteSpeech(text, token);
                return result.TryGetValue(out var heard) ? heard ? AgentHead.SayResult.Ok : AgentHead.SayResult.TooFar : AgentHead.SayResult.InvalidTarget;
            }
            catch (Exception) { return AgentHead.SayResult.InvalidTarget; }
        }
        private static async UniTask RouteNamed(RobotAgent agent, string text, CancellationToken token) => await Route(agent, text, token);

        internal static async Task Play(AgentHead head, string text, CancellationToken token)
        {
            UniTask task;
            // Only the initial native entry bypasses routing. No callback, native task graph or audio enters a packet.
            var previous = playing; playing = true;
            try { task = head.Say(head.CurrentSpeakerName, text, HintGroup.FromAssets(Array.Empty<HintAsset>()), new TaskGraph("Robot speech"), token, null, false); }
            finally { playing = previous; }
            await task;
        }
        public void Dispose() { IsInstalled = false; agents.Clear(); NativeRobotVoice.Clear(); lease.Dispose(); }
    }
}
