using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>An announcement accepted by the fake UI scope.</summary>
    [RequiresTopiaForge(UiKitRelease.K2)]
    public sealed class FakeStinger
    {
        internal FakeStinger(UiStingerRequest request, bool broadcast, FakeUiThemeResolution theme,
            UiThemeDescriptor? declared, bool usesDeclaredFaces)
        {
            Request = request;
            Broadcast = broadcast;
            Theme = theme;
            Declared = declared;
            UsesDeclaredFaces = usesDeclaredFaces;
        }

        /// <summary>Gets the accepted announcement request.</summary>
        public UiStingerRequest Request { get; }

        /// <summary>Gets the fake clock time when the announcement began showing, or NaN while it has never been shown.</summary>
        public double ShownAt { get; private set; } = double.NaN;

        /// <summary>Gets whether the announcement originated from the server UI service of a fake gamemode.</summary>
        public bool Broadcast { get; }

        internal FakeUiThemeResolution Theme { get; }
        internal UiThemeDescriptor? Declared { get; }
        internal UiThemeDescriptor Typography => Declared ?? UiThemePackParser.Default;
        internal bool UsesDeclaredFaces { get; }
        internal void MarkShown(double time) => ShownAt = time;
    }

    public sealed partial class FakeUiService
    {
        private static readonly OperationResult<bool> StingerStopped = OperationResult<bool>.Failure(ModErrorCode.Cancelled,
            "The fake UI scope is stopping.");
        private static readonly OperationResult<bool> StingerFull = OperationResult<bool>.Failure(ModErrorCode.RateLimited,
            "Three stingers are already waiting in this scope.");
        private static readonly OperationResult<bool> StingerStyleInvalid = OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
            "A named stinger style must target stinger and preserve its requested role.");
        private static readonly OperationResult<bool> StingerTextInvalid = OperationResult<bool>.Failure(ModErrorCode.InvalidArgument,
            "A stinger title and subtitle must each fit within two lines and clear the crosshair.");
        private readonly List<FakeStinger> stingers = new List<FakeStinger>();
        private IReadOnlyList<FakeStinger>? stingerView;
        private UiStingerQueue<FakeStinger>? stingerQueue;
        private bool trackingStingers;
        private Func<FakePlayerDirectory?>? serverPlayers;

        /// <summary>Gets accepted announcements in admission order, including those still waiting to show.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public IReadOnlyList<FakeStinger> Stingers
        {
            get
            {
                AdvanceStingers();
                return stingerView ??= stingers.AsReadOnly();
            }
        }

        /// <summary>Gets the announcement currently displayed by this scope on its fake clock.</summary>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public FakeStinger? ActiveStinger
        {
            get
            {
                AdvanceStingers();
                return stingerQueue?.Active?.Presentation;
            }
        }

        /// <inheritdoc/>
        [RequiresTopiaForge(UiKitRelease.K2)]
        public OperationResult<bool> ShowStinger(UiStingerRequest request)
        {
            var result = AcceptStinger(request, serverPlayers != null, null, out var admitted);
            if (admitted != null)
                serverPlayers?.Invoke()?.FanOutStinger(admitted);
            return result;
        }

        internal void FollowGamemodeStingers(FakeModContext context) =>
            serverPlayers = () => context.Extensions.TryGet<IPlayerDirectory>(out var players) ? players as FakePlayerDirectory : null;

        internal OperationResult<bool> ShowTargetedStinger(UiStingerRequest request) => AcceptStinger(request, false, null, out _);

        internal void ReceiveBroadcastStinger(FakeStinger sent) => AcceptStinger(sent.Request, true, sent, out _);

        private OperationResult<bool> AcceptStinger(UiStingerRequest request, bool broadcast, FakeStinger? received,
            out FakeStinger? admitted)
        {
            gameThread.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            admitted = null;
            if (lifetime.IsStopping) return StingerStopped;
            var declared = received == null ? DeclaredTheme(null, null) ?? UiThemePackParser.Default : MatchStingerTheme(received.Declared);
            if (declared != null && !UiLayoutDecoration.AdmitsStingerStyle(request, declared)) return StingerStyleInvalid;
            var theme = received == null ? ResolveTheme(null) : RemoteStingerTheme(received, declared);

            AdvanceStingers();
            var queue = stingerQueue;
            var decision = queue?.Classify(request) ?? UiStingerQueueResult.Started;
            if (decision == UiStingerQueueResult.Coalesced) return FakeUiThemePackage.Success;
            if (decision == UiStingerQueueResult.RateLimited) return StingerFull;
            if (decision == UiStingerQueueResult.Cancelled) return StingerStopped;
            var typography = declared ?? UiThemePackParser.Default;
            if (!FakeUiStingerCapture.FitsSupportedViewport(request, typography) ||
                received == null && broadcast && !ReferenceEquals(typography, UiThemePackParser.Default) &&
                !FakeUiStingerCapture.FitsSupportedViewport(request, UiThemePackParser.Default)) return StingerTextInvalid;

            var usesDeclaredFaces = !theme.UsedFallback && (received?.UsesDeclaredFaces ?? true) &&
                (UiStingerLayout.NeedsDeclaredFace(typography, UiTextStyle.Banner) ||
                 request.Subtitle.Length != 0 && UiStingerLayout.NeedsDeclaredFace(typography, UiTextStyle.Title));

            if (queue == null)
            {
                queue = stingerQueue = new UiStingerQueue<FakeStinger>();
                if (!trackingStingers)
                {
                    trackingStingers = true;
                    lifetime.StoppingToken.Register(ForgetStingers);
                }
            }

            admitted = new FakeStinger(request, broadcast, theme, declared, usesDeclaredFaces);
            queue.EnqueueRetained(request, admitted, ClockTime);
            stingers.Add(admitted);
            MarkActiveStinger(queue);
            return FakeUiThemePackage.Success;
        }

        private UiThemeDescriptor? MatchStingerTheme(UiThemeDescriptor? sender)
        {
            if (sender == null || ReferenceEquals(sender, UiThemePackParser.Default)) return sender;
            var candidate = DeclaredTheme(new UiThemeRef(sender.Id), null);
            return candidate != null && candidate.Digest.Span.SequenceEqual(sender.Digest.Span) ? candidate : null;
        }

        private FakeUiThemeResolution RemoteStingerTheme(FakeStinger sender, UiThemeDescriptor? declared)
        {
            if (sender.Declared == null || ReferenceEquals(sender.Declared, UiThemePackParser.Default))
                return new FakeUiThemeResolution(UiThemePackParser.Default, null, false);
            return declared != null ? ResolveTheme(new UiThemeRef(declared.Id)) :
                new FakeUiThemeResolution(UiThemePackParser.Default, sender.Declared.Id, true);
        }

        private void AdvanceStingers()
        {
            if (stingerQueue is not { } queue || queue.Active is not { } current) return;
            var now = ClockTime;
            var exitSeconds = MotionSeconds(current.Presentation.Theme.Descriptor.Motion.SlowMs);
            if (now - queue.ShownAt >= current.Request.DurationSeconds + exitSeconds && queue.Advance(now, out _))
                MarkActiveStinger(queue);
        }

        private static void MarkActiveStinger(UiStingerQueue<FakeStinger> queue)
        {
            if (queue.Active is { } active && double.IsNaN(active.Presentation.ShownAt)) active.Presentation.MarkShown(queue.ShownAt);
        }

        internal double MotionSeconds(float milliseconds) =>
            Accessibility.ReducedMotion || Accessibility.MotionIntensity == 0f ? 0d : milliseconds / 1000d;

        internal void ForgetStingers()
        {
            if (stingerQueue is not { } queue) return;
            stingerQueue = null;
            while (queue.TryDrain(out _)) { }
        }
    }
}
