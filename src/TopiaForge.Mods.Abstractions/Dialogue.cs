using System;
using System.Collections.Generic;

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
    /// <c>context.GetService&lt;IRobotConversationService&gt;()</c>, exactly like <see cref="IRobotBrainQueryService"/>
    /// and <see cref="IRobotAgentService"/>.
    /// <para>
    /// The backend brain call is single-shot and stateless, so the conversation carries its own transcript and
    /// re-sends a compact history each turn. Every turn is <b>asynchronous and never blocks a frame</b>: call
    /// <see cref="IRobotConversation.Submit"/>, then poll <see cref="IRobotConversation.IsThinking"/> /
    /// <see cref="IRobotConversation.TurnReady"/> each frame (the completion is marshalled back on the service tick,
    /// same idiom as <see cref="IRobotBrainQuery"/>).
    /// </para>
    /// <para>
    /// <b>Dual channel.</b> Each turn yields two things: a free-text <see cref="IRobotConversation.LastReply"/> (the
    /// robot's spoken line — HUD flavour) and a <see cref="IRobotConversation.LastDecision"/> drawn from the closed
    /// set the caller supplied (<see cref="RobotConversationRequest.DecisionOptions"/>). <b>Only the decision should
    /// drive game state.</b> The conversation does not interpret or gate the decision — the consumer owns the win
    /// condition (e.g. clamp a powerful decision behind a disposition threshold) so eloquent player text can never be
    /// an "I-win" button.
    /// </para>
    /// <para>
    /// Everything degrades gracefully: when the backend is unreachable, <see cref="IsAvailable"/> is <c>false</c>,
    /// a submitted turn completes with an empty reply/decision, and the consumer falls back to its own deterministic
    /// outcome. Each turn spends one backend call against the player's token, so conversations are naturally
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
        /// Begins a conversation with a robot's brain. Returns a handle immediately; nothing is sent until the first
        /// <see cref="IRobotConversation.Submit"/>. Abandon a conversation with <see cref="IRobotConversation.End"/>
        /// (or just drop the handle). Never throws.
        /// </summary>
        IRobotConversation BeginConversation(RobotConversationRequest request);
    }

    /// <summary>
    /// A live, multi-turn conversation handle. Drive it by <see cref="Submit"/>-ting the player's line, then polling
    /// <see cref="IsThinking"/> until <see cref="TurnReady"/> latches the robot's <see cref="LastReply"/> and
    /// <see cref="LastDecision"/>. Safe to keep polling; never throws.
    /// </summary>
    public interface IRobotConversation
    {
        /// <summary><c>true</c> when the backend was reachable for this conversation; mirrors the service.</summary>
        bool IsAvailable { get; }

        /// <summary><c>true</c> while a submitted turn is still in flight (the brain is "thinking").</summary>
        bool IsThinking { get; }

        /// <summary>
        /// <c>true</c> once a submitted turn has completed and its <see cref="LastReply"/>/<see cref="LastDecision"/>
        /// are readable. Stays <c>true</c> until the next <see cref="Submit"/> (so a poller can react on any frame).
        /// </summary>
        bool TurnReady { get; }

        /// <summary>
        /// <c>true</c> once the conversation is finished — either <see cref="End"/> was called or
        /// <see cref="MaxTurns"/> completed turns were reached. A finished conversation ignores further
        /// <see cref="Submit"/>s.
        /// </summary>
        bool Ended { get; }

        /// <summary>Number of completed turns so far (0 before the first reply lands).</summary>
        int TurnCount { get; }

        /// <summary>The hard cap on completed turns for this conversation (from the request).</summary>
        int MaxTurns { get; }

        /// <summary>The robot's spoken line from the most recent completed turn (free text); empty before then.</summary>
        string LastReply { get; }

        /// <summary>
        /// The robot's chosen reaction from the most recent completed turn — one of
        /// <see cref="RobotConversationRequest.DecisionOptions"/>, or empty when the brain produced none / was
        /// unavailable. The consumer maps and gates this; the conversation does not.
        /// </summary>
        string LastDecision { get; }

        /// <summary>
        /// Every raw output value from the most recent completed turn — the reply, the decision, and any
        /// <see cref="RobotConversationRequest.ExtraOutputs"/> fields, keyed by field name. Empty before the first
        /// turn and when the turn failed. As with the decision, the consumer maps and gates these values; the
        /// conversation does not.
        /// </summary>
        IReadOnlyDictionary<string, string> LastValues { get; }

        /// <summary>A short diagnostic for the most recent turn when it did not produce a usable answer; else <c>null</c>.</summary>
        string? LastError { get; }

        /// <summary>
        /// Submit the player's line and begin a turn. No-op when the conversation has <see cref="Ended"/>, is already
        /// <see cref="IsThinking"/>, or the text is empty. The text is treated as untrusted and is wrapped/sanitised
        /// before it reaches the brain.
        /// </summary>
        void Submit(string playerText);

        /// <summary>Finish the conversation. Idempotent; abandons any in-flight turn. After this <see cref="Ended"/> is <c>true</c>.</summary>
        void End();
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
        public RobotConversationRequest(string systemFrame, IReadOnlyList<string> decisionOptions)
        {
            SystemFrame = systemFrame ?? string.Empty;
            DecisionOptions = decisionOptions ?? System.Array.Empty<string>();
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
        public IReadOnlyDictionary<string, string>? GroundTruthFacts { get; set; }

        /// <summary>
        /// Live facts recomputed at the start of every submitted turn and merged OVER
        /// <see cref="GroundTruthFacts"/> (a live key wins), so per-turn state such as target positions stays
        /// fresh across a multi-turn conversation. A <c>null</c> return or a throwing provider degrades to the
        /// static facts only. Optional.
        /// </summary>
        public Func<IReadOnlyDictionary<string, string>?>? LiveFacts { get; set; }

        /// <summary>Hard cap on completed turns before the conversation auto-ends. Default 3.</summary>
        public int MaxTurns { get; set; } = 3;

        /// <summary>Sampling temperature for the robot's replies (0 = most deterministic). Clamped by the backend.</summary>
        public float Temperature { get; set; } = 0.7f;

        /// <summary>Telemetry/debug label for the backend. Optional; defaults to a generic label.</summary>
        public string Usage { get; set; } = "robot-conversation";

        /// <summary>How to steer the spoken line (e.g. "a short in-character line, max ~14 words"). Optional.</summary>
        public string? ReplyGuidance { get; set; }

        /// <summary>How to steer the decision (what each option means). Optional.</summary>
        public string? DecisionGuidance { get; set; }

        /// <summary>Hard cap on the robot's spoken line length, in characters. Default 200.</summary>
        public int MaxReplyChars { get; set; } = 200;

        /// <summary>
        /// Additional structured output fields the robot must fill each turn beyond the built-in reply/decision —
        /// e.g. a closed-set <c>target</c> field naming what a chosen action applies to. Keep the set small and
        /// closed-set where possible (each field costs the brain accuracy and latency). Fields named
        /// <c>reply</c>/<c>decision</c> are ignored. Read the values from
        /// <see cref="IRobotConversation.LastValues"/>. Optional.
        /// </summary>
        public IReadOnlyList<BrainOutputField>? ExtraOutputs { get; set; }
    }

    /// <summary>
    /// Captures what the player <i>says</i> to a robot, the same two ways the base game does: typed text, or voice
    /// (push-to-talk → speech-to-text). The voice path records the microphone and transcribes it through the game's
    /// own backend so a mod does not re-derive the audio plumbing; the typed path is handled by the consumer's UI with
    /// the shared <see cref="TextInputBuffer"/> helper.
    /// </summary>
    /// <remarks>
    /// Published by <c>TopiaForge.RobotKit</c> and resolved with
    /// <c>context.GetService&lt;IPlayerDialogueInputService&gt;()</c>. Voice degrades gracefully: when no microphone
    /// is present or the backend is unreachable, <see cref="IsVoiceAvailable"/> is <c>false</c> and the consumer falls
    /// back to typed text.
    /// </remarks>
    public interface IPlayerDialogueInputService
    {
        /// <summary><c>true</c> when a microphone is present and the backend can transcribe (so push-to-talk is usable).</summary>
        bool IsVoiceAvailable { get; }

        /// <summary>
        /// Begin capturing from the microphone immediately (push-to-talk down). Returns a pollable handle; call
        /// <see cref="IVoiceCapture.Stop"/> when the key is released to end recording and start transcription, then
        /// poll <see cref="IVoiceCapture.IsComplete"/> for the text. Returns a handle that completes immediately as
        /// unavailable when voice is off; never throws.
        /// </summary>
        IVoiceCapture BeginVoiceCapture();
    }

    /// <summary>
    /// A pollable push-to-talk capture: record while the key is held, <see cref="Stop"/> on release to transcribe,
    /// then poll <see cref="IsComplete"/> and read <see cref="Text"/>. Never throws.
    /// </summary>
    public interface IVoiceCapture
    {
        /// <summary><c>true</c> while the microphone is still recording (before <see cref="Stop"/>/<see cref="Cancel"/>).</summary>
        bool IsRecording { get; }

        /// <summary><c>true</c> once transcription has finished (whether or not any words came back, or it failed).</summary>
        bool IsComplete { get; }

        /// <summary><c>true</c> when a non-empty transcript came back. Valid once <see cref="IsComplete"/> is true.</summary>
        bool Found { get; }

        /// <summary>The transcript. Empty until <see cref="IsComplete"/>, and empty when nothing usable came back.</summary>
        string Text { get; }

        /// <summary>A short diagnostic when transcription failed; else <c>null</c>.</summary>
        string? Error { get; }

        /// <summary>Stop recording (push-to-talk released) and begin transcription. Idempotent.</summary>
        void Stop();

        /// <summary>Abandon the capture entirely: stop recording with no transcription. Idempotent.</summary>
        void Cancel();
    }
}
