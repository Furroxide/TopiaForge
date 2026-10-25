using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed class RemotePlayerUi : IPlayerUi
    {
        private readonly PlayerContext context;
        private readonly ParticipantId player;
        internal RemotePlayerUi(PlayerContext context, ParticipantId player) { this.context = context; this.player = player; }
        public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => context.Runtime.Toast(context, player, message, tone);
        public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => context.Runtime.Modal(context, player, request, completed);
        public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => context.Runtime.Surface(context, player, request);
    }

    /// <summary>A host-owned surface. Dirty updates wait for the target's window; closing revokes callbacks immediately.</summary>
    internal sealed class RemoteUiSurface : IUiSurface, IUiSurfaceDismissalSource
    {
        internal readonly MultiplayerPlayers Runtime;
        internal readonly PlayerContext Context;
        internal readonly IPlayer Target;
        internal readonly ulong Token;
        internal readonly UiSurfaceRequest Request;
        internal UiNode? Content;
        internal string Body;
        internal ulong Revision = 1;
        internal ulong Stream;
        internal bool Visible = true, Closed, Dirty;
        internal RemoteUiSurface(MultiplayerPlayers runtime, PlayerContext context, IPlayer target, ulong token, UiSurfaceRequest request)
        { Runtime = runtime; Context = context; Target = target; Token = token; Request = request; Content = request.Content; Body = request.Body; }
        public string Id => Request.Id;
        public bool IsVisible => !Closed && Target.IsPresent && !Context.Lifetime.IsStopping && Visible;
        public event Action? Dismissed;
        public void Show() { Check(); if (Closed || Visible) return; Visible = true; Dirty = true; }
        public void Hide() { Check(); if (Closed || !Visible) return; Visible = false; Dirty = true; Runtime.Dismiss(Context, Dismissed); }
        public void SetBody(string body)
        {
            Check();
            if (body == null || body.Length > 4096) throw new ArgumentException("Remote UI text is limited to 4096 characters.", nameof(body));
            if (Closed || Body == body) return;
            Body = body; Dirty = true;
        }
        public OperationResult<bool> SetContent(UiNode content)
        {
            Check();
            if (Closed || !Target.IsPresent) return MultiplayerPlayers.Gone<bool>();
            if (content == null) throw new ArgumentNullException(nameof(content));
            UiComposition.Validate(content);
            if (Request.Kind == UiSurfaceKind.Hud && UiComposition.ContainsInteractive(content))
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Put interactive controls in a window.");
            var old = Content;
            Content = content; Revision++;
            var result = Runtime.SendSurface(this);
            if (!result.Succeeded) { Content = old; Revision--; }
            return result;
        }
        public void Dispose() { Check(); Closed = true; Dirty = true; }
        private static void Check() => UnityMainThreadGuard.AssertCurrent();
    }

    /// <summary>
    /// A host-owned modal. Like a local one it reports exactly one answer: the player's, or a cancellation when it is closed
    /// or disposed, its player leaves, or its scope or the session ends.
    /// </summary>
    internal sealed class RemoteUiModal : IUiModal
    {
        internal readonly PlayerContext Context;
        internal readonly IPlayer Target;
        internal readonly ulong Token;
        internal readonly UiModalRequest Request;
        internal Action<bool>? Completed;
        internal ulong Stream;
        internal RemoteUiModal(PlayerContext context, IPlayer target, ulong token, UiModalRequest request, Action<bool> completed)
        { Context = context; Target = target; Token = token; Request = request; Completed = completed; }
        public bool IsOpen => Completed != null && Target.IsPresent && !Context.Lifetime.IsStopping;
        public void Close() { UnityMainThreadGuard.AssertCurrent(); Cancel(); }
        public void Dispose() { UnityMainThreadGuard.AssertCurrent(); Cancel(); }
        internal void Cancel() => Context.Runtime.Complete(Context, Take(), false);
        internal Action<bool>? Take()
        {
            var callback = Completed;
            Completed = null;
            Context.Modals.Remove(this);
            return callback;
        }
    }
}
