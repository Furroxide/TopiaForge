using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.RobotKit
{
    // Provider-owned associations. Native signatures and interception belong exclusively to the runtime host.
    internal sealed class NativeRobotSpeech : IDisposable, IInternalNativeSpeechRouter
    {
        private static readonly Dictionary<AgentHead, RobotAgent> agents = new Dictionary<AgentHead, RobotAgent>();
        private static NativeRobotSpeech? current;
        private IInternalNativeSpeechLease? lease;
        internal static bool IsInstalled => current?.lease?.IsAvailable == true;
        internal static void Register(AgentHead head, RobotAgent agent) => agents[head] = agent;
        internal static void Release(AgentHead head, RobotAgent agent)
        {
            if (agents.TryGetValue(head, out var registered) && ReferenceEquals(registered, agent)) agents.Remove(head);
        }

        internal static void Install(IModContext context)
        {
            if (current != null) throw new InvalidOperationException("Robot speech already has a provider.");
            var provider = new NativeRobotSpeech();
            try
            {
                var host = (context as IInternalNativeSpeechContext)?.NativeSpeech;
                if (host == null) throw new InvalidOperationException();
                provider.lease = host.Install(provider);
                current = provider;
                context.Lifetime.Track(provider);
            }
            catch (Exception)
            {
                provider.Dispose();
                context.Logger.Warn("Robot speech bindings are unavailable; restart with compatible native speech bindings before retrying.");
            }
        }

        public bool TryRoute(object head, string text, CancellationToken token, bool supported,
            out Task<OperationResult<bool>>? result)
        {
            result = null;
            if (!(head is AgentHead native) || !agents.TryGetValue(native, out var agent)) return false;
            try
            {
                result = supported ? agent.RouteSpeech(text, token)
                    : Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "This native speech form is unsupported."));
            }
            catch (Exception)
            {
                result = Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "Native robot speech routing is unavailable."));
            }
            return true;
        }

        public bool IsCurrent(object head, object association) => head is AgentHead native
            && agents.TryGetValue(native, out var agent) && agent.OwnsSpeechAssociation(association);
        public CancellationToken AssociationStopping(object head, object association) => head is AgentHead native
            && agents.TryGetValue(native, out var agent) && agent.OwnsSpeechAssociation(association)
                ? agent.SpeechStoppingToken : new CancellationToken(true);

        internal static IInternalNativeSpeechPlayback? BindPlayback(AgentHead head, RobotAgent agent, object association)
        {
            if (!agents.TryGetValue(head, out var registered) || !ReferenceEquals(registered, agent))
                throw new InvalidOperationException("The native head belongs to another robot.");
            return IsInstalled ? current!.lease!.Register(head, association) : null;
        }

        public void Dispose()
        {
            // Retain associations if unpatch fails: the reserved runtime host rejects owned calls until retry.
            if (lease == null) OnStopped();
            else lease.Dispose();
        }

        public void OnStopped()
        {
            if (ReferenceEquals(current, this))
            {
                current = null;
                agents.Clear();
                NativeRobotVoice.Clear();
            }
        }
    }
}
