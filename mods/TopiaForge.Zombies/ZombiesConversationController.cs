using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Optional, explicitly enabled RobotKit brain conversation with deterministic game-state gates.</summary>
    internal sealed partial class ZombiesConversationController : IDisposable
    {
        private const float PauseRetrySeconds = 0.5f;

        private readonly IModContext context;
        private readonly ZombiesConfig config;
        private readonly ITimeControlService? time;
        private readonly IRobotConversationService? service;
        private readonly IPlayerDialogueInputService? dialogueInput;
        private readonly Action<ZombieEnemy, ConversationDecision, float> resolved;
        private readonly IInputAction? voiceAction;
        private IUiSurface? surface;
        private IRobotConversation? conversation;
        private ZombieEnemy? target;
        private ITimeLease? freeze;
        private IPlayerControlLease? control;
        private IVoiceCapture? voiceCapture;
        private CancellationTokenSource? cancellation;
        private Task<OperationResult<RobotConversationTurnResult>>? turnTask;
        private Task<OperationResult<VoiceTranscriptResult>>? voiceTask;
        private string draft = string.Empty;
        private string transcript = string.Empty;
        private float disposition;
        private float remainingSeconds;
        private float pauseRetryTimer;
        private int lastDisplayedSecond = -1;
        private bool voiceMode;
        private bool pauseFailureReported;
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
        public bool IsWorldFrozen => freeze?.IsActive == true;

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
            disposition = ConversationDirector.SeedDisposition(enemy.Mind, Tuning());
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

            AcquirePause();
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

            EnsurePause(controlDelta);
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
            voiceAction?.Dispose();
        }

        private void Submit()
        {
            if (conversation == null || turnTask != null || voiceTask != null
                || string.IsNullOrWhiteSpace(draft))
            {
                return;
            }

            var line = draft.Trim();
            draft = string.Empty;
            transcript += "\n\nYOU > " + line;
            try
            {
                turnTask = conversation.SubmitAsync(line, cancellation?.Token ?? default);
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
            if (turnTask == null || !turnTask.IsCompleted)
            {
                return;
            }

            var task = turnTask;
            turnTask = null;
            var result = Complete(task);
            if (!result.TryGetValue(out var turn) || turn == null)
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
            if (!voiceMode || dialogueInput?.IsVoiceAvailable != true || turnTask != null || voiceTask != null)
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
                    voiceTask = capture.StopAsync(cancellation?.Token ?? default);
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
            if (voiceTask == null || !voiceTask.IsCompleted)
            {
                return;
            }

            var task = voiceTask;
            voiceTask = null;
            var result = Complete(task);
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
            voiceTask = null;
            turnTask = null;
            conversation?.Dispose();
            conversation = null;
            var window = surface;
            surface = null;
            window?.Dispose();
            freeze?.Dispose();
            freeze = null;
            control?.Dispose();
            control = null;
            pauseRetryTimer = 0f;
            pauseFailureReported = false;
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

        private static OperationResult<T> Complete<T>(Task<OperationResult<T>> task) where T : notnull
        {
            try
            {
                return task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                return OperationResult<T>.Failure(ModErrorCode.External, exception.Message);
            }
        }
    }
}
