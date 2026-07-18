using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Inspectable modal whose result is completed explicitly by a test.</summary>
    public sealed class FakeUiModal : IUiModal
    {
        private Action<bool>? completed;
        private Action<FakeUiModal>? release;
        private IDisposable? lifetimeLease;
        private readonly List<string> callbackErrors = new List<string>();

        internal FakeUiModal(
            UiModalRequest request,
            Action<bool> completed,
            Action<FakeUiModal> release)
        {
            Request = request;
            this.completed = completed;
            this.release = release;
        }

        internal void AttachLifetimeLease(IDisposable lease)
        {
            lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
        }

        /// <summary>Gets the captured modal request.</summary>
        public UiModalRequest Request { get; }

        /// <inheritdoc/>
        public bool IsOpen => release != null;

        /// <summary>Gets isolated completion-callback failures in invocation order.</summary>
        public IReadOnlyList<string> CallbackErrors => callbackErrors.AsReadOnly();

        /// <summary>Closes the modal and reports confirmation.</summary>
        public void Confirm() => Complete(true);

        /// <inheritdoc/>
        public void Close() => Complete(false);

        /// <inheritdoc/>
        public void Dispose() => Complete(false);

        private void Complete(bool confirmed)
        {
            var callback = completed;
            completed = null;
            var releaseCallback = release;
            release = null;
            releaseCallback?.Invoke(this);
            System.Threading.Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
            if (callback == null) return;
            foreach (var subscriber in callback.GetInvocationList())
            {
                try { ((Action<bool>)subscriber)(confirmed); }
                catch (Exception exception)
                {
                    callbackErrors.Add("modal completion callback failed: " + exception.Message);
                }
            }
        }
    }

    /// <summary>Represents a captured toast.</summary>
    public sealed class FakeToast
    {
        /// <summary>Creates captured toast data.</summary>
        public FakeToast(string message, UiTone tone)
        {
            Message = message;
            Tone = tone;
        }

        /// <summary>Gets the display message.</summary>
        public string Message { get; }

        /// <summary>Gets the semantic tone.</summary>
        public UiTone Tone { get; }
    }
}
