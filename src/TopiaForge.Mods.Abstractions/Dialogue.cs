using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods
{
    /// <summary>
    /// A multi-turn <b>conversation</b> with a robot's LLM brain — the reusable "talk to a robot over several
    /// exchanges" primitive built on top of the single-shot <see cref="IRobotBrainQueryService"/>. Where a brain
    /// query asks one structured question, a conversation remembers what was said and lets the player and the robot
    /// go back and forth, with the robot answering <i>in its own words</i> and <i>choosing</i> a structured reaction
    /// each turn.
    /// </summary>
    /// <remarks>
    /// Published by the <c>TopiaForge.RobotKit</c> framework mod and resolved with
    /// <c>context.RequireExtension&lt;IRobotConversationService&gt;()</c>, exactly like
    /// <see cref="IRobotBrainQueryService"/> and <see cref="IRobotAgentService"/>.
    /// <para>
    /// The backend brain call is single-shot and stateless, so the conversation carries its own transcript and
    /// re-sends a compact history each turn. Await <see cref="IRobotConversation.SubmitAsync"/>; it never blocks a
    /// game frame.
    /// </para>
    /// <para>
    /// <b>Dual channel.</b> Each turn yields a free-text <see cref="RobotConversationTurnResult.Reply"/> and a
    /// <see cref="RobotConversationTurnResult.Decision"/> drawn from the closed
    /// set the caller supplied (<see cref="RobotConversationRequest.DecisionOptions"/>). <b>Only the decision should
    /// drive game state.</b> The conversation does not interpret or gate the decision — the consumer owns the win
    /// condition (e.g. clamp a powerful decision behind a disposition threshold) so eloquent player text can never be
    /// an "I-win" button.
    /// </para>
    /// <para>
    /// Everything degrades gracefully: when the backend is unreachable, <see cref="IsAvailable"/> is <c>false</c>
    /// and a submitted turn completes with a stable unavailable result, so the consumer can choose its own
    /// deterministic outcome. Each turn spends one backend call against the player's token, so conversations are naturally
    /// short — bound them with <see cref="RobotConversationRequest.MaxTurns"/>.
    /// </para>
    /// </remarks>
    public interface IRobotConversationService
    {
        /// <summary>
        /// <c>true</c> when a conversation turn can currently be served (the backend token resolved and the service
        /// is live). <c>false</c> means <see cref="BeginConversation"/> still returns a usable handle that simply
        /// reports itself unavailable, so callers never special-case the offline path. Cheap to poll.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Begins a conversation with a robot's brain and returns a lifetime-owned handle.
        /// </summary>
        OperationResult<IRobotConversation> BeginConversation(RobotConversationRequest request);
    }

    /// <summary>
    /// A lifetime-owned, asynchronous multi-turn conversation handle.
    /// </summary>
    public interface IRobotConversation : IDisposable
    {
        /// <summary>Gets whether the conversation has ended.</summary>
        bool IsEnded { get; }

        /// <summary>Number of completed turns so far (0 before the first reply lands).</summary>
        int TurnCount { get; }

        /// <summary>The hard cap on completed turns for this conversation (from the request).</summary>
        int MaxTurns { get; }

        /// <summary>Submits one player line and asynchronously returns the immutable robot turn.</summary>
        Task<OperationResult<RobotConversationTurnResult>> SubmitAsync(
            string playerText,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Immutable output from one completed robot conversation turn.</summary>
    public sealed class RobotConversationTurnResult
    {
        /// <summary>Creates a completed conversation turn.</summary>
        public RobotConversationTurnResult(
            string reply,
            string decision,
            IReadOnlyDictionary<string, string> values)
        {
            Reply = reply ?? string.Empty;
            Decision = decision ?? string.Empty;
            Values = new ReadOnlyDictionary<string, string>(
                values == null
                    ? new Dictionary<string, string>()
                    : new Dictionary<string, string>(values, StringComparer.Ordinal));
        }

        /// <summary>Gets the robot's spoken line.</summary>
        public string Reply { get; }

        /// <summary>Gets the closed-set decision.</summary>
        public string Decision { get; }

        /// <summary>Gets every structured output value.</summary>
        public IReadOnlyDictionary<string, string> Values { get; }
    }

    /// <summary>
    /// The setup for a conversation: who the robot is, what is authoritatively true about it, and the closed set of
    /// reactions it may choose from. The persona/voice live in <see cref="SystemFrame"/>; the
    /// <see cref="GroundTruthFacts"/> are injected as authoritative state the robot cannot be argued out of.
    /// </summary>
    public sealed class RobotConversationRequest
    {
        /// <summary>Creates a conversation request.</summary>
        /// <param name="systemFrame">The persona/voice/rules framing for the robot (who it is, the fiction, tone, what NOT to do).</param>
        /// <param name="decisionOptions">The closed set of reactions the robot must choose from each turn (the decision enum).</param>
        /// <param name="groundTruthFacts">Optional immutable authoritative facts included on every turn.</param>
        /// <param name="liveFacts">Optional callback that supplies facts immediately before each turn.</param>
        /// <param name="maxTurns">Maximum number of completed turns.</param>
        /// <param name="temperature">Sampling temperature; zero is the most deterministic.</param>
        /// <param name="usage">A stable diagnostic label for the conversation.</param>
        /// <param name="replyGuidance">Optional guidance for the spoken reply.</param>
        /// <param name="decisionGuidance">Optional guidance defining the available decisions.</param>
        /// <param name="maxReplyChars">Maximum number of characters accepted in a reply.</param>
        /// <param name="extraOutputs">Optional additional structured output fields.</param>
        public RobotConversationRequest(
            string systemFrame,
            IReadOnlyList<string> decisionOptions,
            IReadOnlyDictionary<string, string>? groundTruthFacts = null,
            Func<IReadOnlyDictionary<string, string>?>? liveFacts = null,
            int maxTurns = 3,
            float temperature = 0.7f,
            string usage = "robot-conversation",
            string? replyGuidance = null,
            string? decisionGuidance = null,
            int maxReplyChars = 200,
            IReadOnlyList<BrainOutputField>? extraOutputs = null)
        {
            SystemFrame = systemFrame ?? string.Empty;
            DecisionOptions = decisionOptions == null
                ? Array.Empty<string>()
                : new ReadOnlyCollection<string>(new List<string>(decisionOptions));
            GroundTruthFacts = groundTruthFacts == null
                ? null
                : new ReadOnlyDictionary<string, string>(
                    new Dictionary<string, string>(groundTruthFacts, StringComparer.Ordinal));
            LiveFacts = liveFacts;
            MaxTurns = maxTurns;
            Temperature = temperature;
            Usage = usage ?? string.Empty;
            ReplyGuidance = replyGuidance;
            DecisionGuidance = decisionGuidance;
            MaxReplyChars = maxReplyChars;
            ExtraOutputs = extraOutputs == null
                ? null
                : new ReadOnlyCollection<BrainOutputField>(new List<BrainOutputField>(extraOutputs));
        }

        /// <summary>The persona/voice/rules framing for the robot. Owns tone and the "stay in character" guardrails.</summary>
        public string SystemFrame { get; }

        /// <summary>The closed set of reactions the robot must choose from each turn (e.g. <c>COMPLY/REFUSE/FLEE/CONVERT</c>).</summary>
        public IReadOnlyList<string> DecisionOptions { get; }

        /// <summary>
        /// Authoritative facts about the robot/situation, injected each turn as ground truth the robot cannot be
        /// gaslit about (e.g. <c>hp</c>, <c>faction</c>, <c>was-just-zapped</c>). Keys/values are short strings.
        /// Optional.
        /// </summary>
        public IReadOnlyDictionary<string, string>? GroundTruthFacts { get; }

        /// <summary>
        /// Live facts recomputed at the start of every submitted turn and merged OVER
        /// <see cref="GroundTruthFacts"/> (a live key wins), so per-turn state such as target positions stays
        /// fresh across a multi-turn conversation. A <c>null</c> return or a throwing provider degrades to the
        /// static facts only. Optional.
        /// </summary>
        public Func<IReadOnlyDictionary<string, string>?>? LiveFacts { get; }

        /// <summary>Hard cap on completed turns before the conversation auto-ends. Default 3.</summary>
        public int MaxTurns { get; }

        /// <summary>Sampling temperature for the robot's replies (0 = most deterministic). Clamped by the backend.</summary>
        public float Temperature { get; }

        /// <summary>Telemetry/debug label for the backend. Optional; defaults to a generic label.</summary>
        public string Usage { get; }

        /// <summary>How to steer the spoken line (e.g. "a short in-character line, max ~14 words"). Optional.</summary>
        public string? ReplyGuidance { get; }

        /// <summary>How to steer the decision (what each option means). Optional.</summary>
        public string? DecisionGuidance { get; }

        /// <summary>Hard cap on the robot's spoken line length, in characters. Default 200.</summary>
        public int MaxReplyChars { get; }

        /// <summary>
        /// Additional structured output fields the robot must fill each turn beyond the built-in reply/decision —
        /// e.g. a closed-set <c>target</c> field naming what a chosen action applies to. Keep the set small and
        /// closed-set where possible (each field costs the brain accuracy and latency). Fields named
        /// <c>reply</c>/<c>decision</c> are ignored. Read the values from the returned turn result. Optional.
        /// </summary>
        public IReadOnlyList<BrainOutputField>? ExtraOutputs { get; }
    }

    /// <summary>
    /// Captures what the player <i>says</i> to a robot, the same two ways the base game does: typed text, or voice
    /// (push-to-talk → speech-to-text). The voice path records the microphone and transcribes it through the game's
    /// own backend so a mod does not re-derive the audio plumbing; the typed path is handled by the consumer's UI with
    /// the shared <see cref="TextInputBuffer"/> helper.
    /// </summary>
    /// <remarks>
    /// Published by <c>TopiaForge.RobotKit</c> and resolved with
    /// <c>context.RequireExtension&lt;IPlayerDialogueInputService&gt;()</c>. Voice degrades gracefully: when no microphone
    /// is present or the backend is unreachable, <see cref="IsVoiceAvailable"/> is <c>false</c> and the consumer falls
    /// back to typed text.
    /// </remarks>
    public interface IPlayerDialogueInputService
    {
        /// <summary><c>true</c> when a microphone is present and the backend can transcribe (so push-to-talk is usable).</summary>
        bool IsVoiceAvailable { get; }

        /// <summary>
        /// Begins microphone capture and returns a lifetime-owned handle, or a stable unavailable result.
        /// </summary>
        OperationResult<IVoiceCapture> BeginVoiceCapture();
    }

    /// <summary>
    /// A lifetime-owned push-to-talk capture. Await <see cref="StopAsync"/> when the key is released.
    /// </summary>
    public interface IVoiceCapture : IDisposable
    {
        /// <summary><c>true</c> while the microphone is still recording.</summary>
        bool IsRecording { get; }

        /// <summary>Stops recording and asynchronously returns a transcript with stable failure codes.</summary>
        Task<OperationResult<VoiceTranscriptResult>> StopAsync(
            CancellationToken cancellationToken = default);
    }

    /// <summary>Immutable successful voice transcription.</summary>
    public sealed class VoiceTranscriptResult
    {
        /// <summary>Creates a voice transcription.</summary>
        public VoiceTranscriptResult(string text)
        {
            Text = text ?? string.Empty;
        }

        /// <summary>Gets the transcribed text.</summary>
        public string Text { get; }
    }
}
