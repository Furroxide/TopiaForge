using System;
using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.RobotKit
{
    // Publishes IRobotConversationService: a thin, reusable multi-turn layer over the single-shot
    // IRobotBrainQueryService. Each conversation carries its own transcript and re-sends a compact history every turn
    // (the backend brain call is stateless), so the robot answers in its own words and picks a structured reaction
    // across several exchanges. Unity-free — it only touches the SDK contracts and the pure ConversationPrompt — so
    // the whole flow unit-tests on net8.0 against a fake brain service. The completion of each turn is observed on the
    // service Tick (after the brain service's own Tick), mirroring the brain-query lifecycle.
    internal sealed class RobotConversationService : IRobotConversationService, IDisposable
    {
        private readonly IRobotBrainQueryService brains;
        private readonly IModLogger logger;
        private readonly List<RobotConversation> active = new List<RobotConversation>();

        private bool disposed;
        private bool loggedAvailability;

        public RobotConversationService(IRobotBrainQueryService brains, IModLogger logger)
        {
            this.brains = brains;
            this.logger = logger;
        }

        public bool IsAvailable => !disposed && brains != null && brains.IsAvailable;

        public IRobotConversation BeginConversation(RobotConversationRequest request)
        {
            var conversation = new RobotConversation(brains, request);
            if (!disposed)
            {
                active.Add(conversation);
                LogAvailabilityOnce();
            }
            else
            {
                conversation.End();
            }

            return conversation;
        }

        // Advance every live conversation and drop finished ones. Call after the brain service's Tick so a turn that
        // completed this frame is observed the same frame.
        public void Tick(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            for (var index = active.Count - 1; index >= 0; index--)
            {
                var conversation = active[index];
                conversation.Pump();
                if (conversation.Ended && !conversation.IsThinking)
                {
                    active.RemoveAt(index);
                }
            }
        }

        public void OnSceneChanged()
        {
            // A scene change tears down any in-flight conversations (the robots they were about are gone).
            for (var index = 0; index < active.Count; index++)
            {
                active[index].End();
            }

            active.Clear();
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            for (var index = 0; index < active.Count; index++)
            {
                active[index].End();
            }

            active.Clear();
        }

        private void LogAvailabilityOnce()
        {
            if (loggedAvailability)
            {
                return;
            }

            loggedAvailability = true;
            logger.Info("RobotKit: conversations enabled — robots can be talked to over several turns (llama-3.3-70b).");
        }
    }

    // The pollable conversation handle. State is written by Pump (main thread, off the service tick) and read by the
    // consumer (main thread); the only cross-thread state is the underlying brain query's Task. Never throws.
    internal sealed class RobotConversation : IRobotConversation
    {
        private static readonly IReadOnlyDictionary<string, string> EmptyValues = new Dictionary<string, string>();

        private readonly IRobotBrainQueryService brains;
        private readonly RobotConversationRequest config;
        private readonly List<ConversationTurn> history = new List<ConversationTurn>();

        private IRobotBrainQuery? pending;
        private string pendingPlayerText = string.Empty;
        private string lastReply = string.Empty;
        private string lastDecision = string.Empty;
        private IReadOnlyDictionary<string, string> lastValues = EmptyValues;
        private string? lastError;
        private int turnCount;
        private bool turnReady;
        private bool ended;

        public RobotConversation(IRobotBrainQueryService brains, RobotConversationRequest config)
        {
            this.brains = brains;
            this.config = config ?? new RobotConversationRequest(string.Empty, Array.Empty<string>());
        }

        public bool IsAvailable => brains != null && brains.IsAvailable;

        public bool IsThinking
        {
            get
            {
                Pump();
                return pending != null;
            }
        }

        public bool TurnReady
        {
            get
            {
                Pump();
                return turnReady;
            }
        }

        public bool Ended => ended;

        public int TurnCount => turnCount;

        public int MaxTurns => Math.Max(1, config.MaxTurns);

        public string LastReply => lastReply;

        public string LastDecision => lastDecision;

        public IReadOnlyDictionary<string, string> LastValues => lastValues;

        public string? LastError => lastError;

        public void Submit(string playerText)
        {
            Pump();
            if (ended || pending != null || brains == null || string.IsNullOrWhiteSpace(playerText))
            {
                return;
            }

            turnReady = false;
            lastError = null;
            pendingPlayerText = ConversationPrompt.Sanitize(playerText);
            var request = ConversationPrompt.BuildRequest(config, history, playerText);
            pending = brains.BeginQuery(request);
        }

        public void End()
        {
            pending = null;
            ended = true;
        }

        // Drain a finished turn into the latched reply/decision and advance the transcript. Idempotent and cheap;
        // called by the service Tick and defensively by the read members so state is never stale.
        public void Pump()
        {
            if (pending == null || !pending.IsComplete)
            {
                return;
            }

            var result = pending.Result;
            pending = null;

            string reply = string.Empty;
            string decision = string.Empty;
            if (result != null && result.Available && result.Succeeded)
            {
                result.TryGet(ConversationPrompt.ReplyField, out reply);
                result.TryGet(ConversationPrompt.DecisionField, out decision);
                var values = new Dictionary<string, string>(result.Values.Count);
                foreach (var pair in result.Values)
                {
                    values[pair.Key] = pair.Value;
                }

                lastValues = values;
                lastError = null;
            }
            else
            {
                lastValues = EmptyValues;
                lastError = result?.Error ?? "unavailable";
            }

            lastReply = ClampReply(reply);
            lastDecision = decision ?? string.Empty;
            history.Add(new ConversationTurn(pendingPlayerText, lastReply, lastDecision));
            turnCount++;
            turnReady = true;

            if (turnCount >= MaxTurns)
            {
                ended = true;
            }
        }

        private string ClampReply(string? reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return string.Empty;
            }

            var max = config.MaxReplyChars > 0 ? config.MaxReplyChars : 200;
            return reply!.Length > max ? reply.Substring(0, max) : reply;
        }
    }
}
