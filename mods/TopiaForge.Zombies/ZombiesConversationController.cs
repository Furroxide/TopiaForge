using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Optional, explicitly enabled RobotKit brain conversation with deterministic game-state gates.</summary>
    internal sealed partial class ZombiesConversationController : IDisposable
    {
        private readonly IModContext context;
        private readonly ZombiesConfig config;
        private readonly ITimeControlService? time;
        private readonly IRobotConversationService? service;
        private readonly IPlayerDialogueInputService? dialogueInput;
        private readonly Action<ZombieEnemy, ConversationDecision, float> resolved;
        private readonly IInputAction? voiceAction;
        private readonly GameplayPause pause;
        private IUiSurface? surface;
        private IRobotConversation? conversation;
        private ZombieEnemy? target;
        private IVoiceCapture? voiceCapture;
        private CancellationTokenSource? cancellation;
        private readonly PendingOperation<RobotConversationTurnResult> turnOperation =
            new PendingOperation<RobotConversationTurnResult>();
        private readonly PendingOperation<VoiceTranscriptResult> voiceOperation =
            new PendingOperation<VoiceTranscriptResult>();
        private string draft = string.Empty;
        private string transcript = string.Empty;
        private float disposition;
        private float remainingSeconds;
        private int lastDisplayedSecond = -1;
        private bool voiceMode;
        private bool suppressResolution;
        private bool disposed;

        public ZombiesConversationController(
            IModContext context,
            ZombiesConfig config,
            ITimeControlService? time,
            Action<ZombieEnemy, ConversationDecision, float> resolved)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.time = time;
            this.resolved = resolved ?? throw new ArgumentNullException(nameof(resolved));
            pause = new GameplayPause(
                context,
                "zombies-jack-in",
                time.AsPauseSource(),
                "ZOMBIES_JACK_IN_PAUSE_FAILED");
            context.Extensions.TryGet<IRobotConversationService>(out service);
            context.Extensions.TryGet<IPlayerDialogueInputService>(out dialogueInput);
            if (config.OverrideEnabled
                && config.ConversationEnabled
                && config.UseLiveBrain
                && config.UseVoiceInput)
            {
                // The JACK IN window owns keyboard focus while this action is useful, so push-to-talk must remain
                // sampleable under UI focus. Mode switching stays in the declarative UI to preserve Tab navigation.
                voiceAction = RegisterAction(
                    "jack-in-voice",
                    "Hold to speak over JACK IN",
                    config.VoiceKey,
                    suppressWhileUiFocused: false);
            }
        }

        public bool IsOpen => surface != null;

        /// <summary>True only when Chronos owns an actual world freeze, not merely the player-control fallback.</summary>
        public bool IsWorldFrozen => pause.Kind == GameplayPauseKind.Preferred;

        public bool IsAvailable => !disposed
            && config.ConversationEnabled
            && config.UseLiveBrain
            && service?.IsAvailable == true;

        public OperationResult<bool> Open(ZombieEnemy enemy, int wave)
        {
            if (!IsAvailable || service == null)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.Unavailable,
                    "Live JACK IN is disabled or the RobotKit brain is unavailable.");
            }

            if (IsOpen)
            {
                return OperationResult<bool>.Failure(ModErrorCode.Conflict, "A JACK IN channel is already open.");
            }

            var request = ConversationDirector.BuildRequest(
                enemy.Archetype.DisplayName,
                enemy.Mind,
                wave,
                enemy.HealthFraction,
                enemy.WasRecentlyShot,
                enemy.IsAlly,
                enemy.Loyalty,
                config.BrainTemperature,
                config.ConversationMaxTurns);
            var begun = service.BeginConversation(request);
            if (!begun.TryGetValue(out var handle) || handle == null)
            {
                return OperationResult<bool>.Failure(begun.ErrorCode, begun.ErrorMessage);
            }

            var created = context.Ui.CreateSurface(new UiSurfaceRequest(
                "zombies-jack-in",
                "JACK IN // " + enemy.Archetype.DisplayName.ToUpperInvariant(),
                string.Empty,
                UiSurfaceKind.Window,
                620f,
                460f));
            if (!created.TryGetValue(out var window) || window == null)
            {
                handle.Dispose();
                return OperationResult<bool>.Failure(created.ErrorCode, created.ErrorMessage);
            }

            target = enemy;
            conversation = handle;
            surface = window;
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.Lifetime.StoppingToken);
            disposition = ConversationDirector.SeedDisposition(enemy.Mind, enemy.Archetype.BaseResistance, Tuning());
            remainingSeconds = config.ConversationWindowSeconds;
            transcript = "DIRECT CHANNEL ESTABLISHED. The horde is frozen, but pressure is building.";
            draft = string.Empty;
            // Start in the universally available mode. Voice remains an explicit opt-in because opening a
            // focused window must never leave the player with a disabled text field and no obvious way to type.
            voiceMode = false;
            var built = Rebuild();
            if (!built.Succeeded)
            {
                ReleaseSession();
                return OperationResult<bool>.Failure(built.ErrorCode, built.ErrorMessage);
            }

            pause.Request();
            window.Show();
            return OperationResult<bool>.Success(true);
        }

        public void Tick(float controlDelta)
        {
            if (!IsOpen || disposed)
            {
                return;
            }

            if (surface != null && !surface.IsVisible)
            {
                // Native window close and Escape are player cancellation, not a transport failure. Unknown is
                // reserved for a broken live-brain link where the deterministic fallback is intentional.
                Finish(ConversationDecision.Refuse);
                return;
            }

            pause.Tick(controlDelta);
            remainingSeconds = Math.Max(0f, remainingSeconds - Math.Max(0f, controlDelta));
            ProcessVoiceInput();
            ProcessVoiceTask();
            ProcessTurnTask();
            if (!IsOpen)
            {
                return;
            }

            if (remainingSeconds <= 0f)
            {
                Finish(ConversationDecision.Refuse);
                return;
            }

            var displayed = (int)Math.Ceiling(remainingSeconds);
            if (displayed != lastDisplayedSecond)
            {
                UpdateStatus();
            }
        }

        public void Close()
        {
            if (!IsOpen)
            {
                return;
            }

            suppressResolution = true;
            ReleaseSession();
            suppressResolution = false;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Close();
            pause.Dispose();
            voiceAction?.Dispose();
        }

        private void Submit()
        {
            if (conversation == null || turnOperation.IsInFlight || voiceOperation.IsInFlight
                || string.IsNullOrWhiteSpace(draft))
            {
                return;
            }

            var line = draft.Trim();
            draft = string.Empty;
            transcript += "\n\nYOU > " + line;
            try
            {
                turnOperation.Begin(
                    token => conversation.SubmitAsync(line, token),
                    context.Lifetime.StoppingToken,
                    (float)context.Time.Frame.ElapsedTime);
                Rebuild();
            }
            catch (Exception exception)
            {
                context.Logger.Warn("Zombies JACK IN submit failed: " + exception.Message);
                Finish(ConversationDecision.Unknown);
            }
        }

        private void ProcessTurnTask()
        {
            // The conversation window is the deadline; a turn that outlives it resolves through the same
            // deterministic fallback as a broken link rather than leaving the window stuck on "thinking".
            var state = turnOperation.Poll(
                (float)context.Time.Frame.ElapsedTime,
                Math.Max(0.1f, remainingSeconds),
                out var result);
            if (state == PendingOperationState.Waiting || state == PendingOperationState.Idle)
            {
                return;
            }

            if (state != PendingOperationState.Completed || !result.TryGetValue(out var turn) || turn == null)
            {
                context.Ui.ShowToast("Live brain link lost; deterministic uplink took over.", UiTone.Warning);
                Finish(ConversationDecision.Unknown);
                return;
            }

            var decision = ConversationDirector.Parse(turn.Decision);
            disposition = ConversationDirector.Nudge(disposition, decision, Tuning());
            transcript += "\nROBOT > " + (string.IsNullOrWhiteSpace(turn.Reply) ? "[static]" : turn.Reply.Trim());
            remainingSeconds = Math.Min(
                config.ConversationWindowSeconds,
                remainingSeconds + config.ConversationTurnRefillSeconds);

            if (decision == ConversationDecision.Convert
                && disposition >= ConversationDirector.ConvertThreshold(
                    target?.Archetype.BaseResistance ?? 1f,
                    Tuning()))
            {
                Finish(ConversationDecision.Convert);
                return;
            }

            if (decision == ConversationDecision.StandDown || decision == ConversationDecision.Flee)
            {
                Finish(decision);
                return;
            }

            if (conversation?.IsEnded == true)
            {
                Finish(ConversationDecision.Refuse);
                return;
            }

            Rebuild();
        }

        private void ProcessVoiceInput()
        {
            if (!voiceMode || dialogueInput?.IsVoiceAvailable != true || turnOperation.IsInFlight || voiceOperation.IsInFlight)
            {
                return;
            }

            if (voiceAction?.WasPressed == true && voiceCapture == null)
            {
                var result = dialogueInput.BeginVoiceCapture();
                if (result.TryGetValue(out var capture))
                {
                    voiceCapture = capture;
                    Rebuild();
                }
                else
                {
                    context.Ui.ShowToast("Voice unavailable; type your message instead.", UiTone.Warning);
                    voiceMode = false;
                    Rebuild();
                }
            }

            if (voiceAction?.WasReleased == true && voiceCapture != null)
            {
                var capture = voiceCapture;
                try
                {
                    voiceOperation.Begin(
                        token => capture.StopAsync(token),
                        context.Lifetime.StoppingToken,
                        (float)context.Time.Frame.ElapsedTime);
                }
                catch (Exception exception)
                {
                    capture.Dispose();
                    voiceCapture = null;
                    context.Logger.Warn("Zombies voice capture failed: " + exception.Message);
                }

                Rebuild();
            }
        }

        private void ProcessVoiceTask()
        {
            var state = voiceOperation.Poll(
                (float)context.Time.Frame.ElapsedTime,
                Math.Max(0.1f, remainingSeconds),
                out var result);
            if (state == PendingOperationState.Waiting || state == PendingOperationState.Idle)
            {
                return;
            }

            voiceCapture?.Dispose();
            voiceCapture = null;
            if (result.TryGetValue(out var transcriptResult)
                && transcriptResult != null
                && !string.IsNullOrWhiteSpace(transcriptResult.Text))
            {
                draft = transcriptResult.Text;
                Submit();
            }
            else
            {
                context.Ui.ShowToast("No voice transcript was received.", UiTone.Warning);
                Rebuild();
            }
        }

        private void Finish(ConversationDecision decision)
        {
            var completedTarget = target;
            var completedDisposition = disposition;
            ReleaseSession();
            if (!suppressResolution && !disposed && completedTarget != null && completedTarget.IsActive)
            {
                resolved(completedTarget, decision, completedDisposition);
            }
        }

        private void ReleaseSession()
        {
            try { cancellation?.Cancel(); }
            catch (ObjectDisposedException) { }
            cancellation?.Dispose();
            cancellation = null;
            voiceCapture?.Dispose();
            voiceCapture = null;
            voiceOperation.Cancel();
            turnOperation.Cancel();
            conversation?.Dispose();
            conversation = null;
            var window = surface;
            surface = null;
            window?.Dispose();
            pause.Release();
            target = null;
            lastDisplayedSecond = -1;
        }

        private ConversationTuning Tuning() => new ConversationTuning(
            config.ConvSeedBias,
            config.ConvertThreshold,
            config.ConvertResistanceWeight,
            config.ConvertNudge,
            config.StandDownNudge,
            config.FleeNudge,
            config.RefuseNudge,
            config.EnrageDispositionFloor);
    }
}
