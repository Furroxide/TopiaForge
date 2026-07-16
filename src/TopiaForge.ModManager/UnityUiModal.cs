using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    internal sealed partial class OwnerUiService
    {
        private sealed class UnityUiModal : IUiModal
        {
            private TopiaForgeModalInstance? modal;
            private Action<bool>? completed;
            private IDisposable? lifetimeLease;
            private readonly IModLogger logger;
            private int completionArmed;
            private int finished;

            public UnityUiModal(
                TopiaForgeModalInstance modal,
                Action<bool> completed,
                IModLogger logger)
            {
                this.modal = modal;
                this.completed = completed;
                this.logger = logger;
            }

            public bool IsOpen => Volatile.Read(ref finished) == 0 && modal != null;

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public void ArmCompletion()
            {
                Volatile.Write(ref completionArmed, 1);
            }

            public void Abort()
            {
                Interlocked.Exchange(ref completed, null);
                Complete(false);
            }

            public void Confirm()
            {
                Complete(true);
            }

            public void Cancel()
            {
                Complete(false);
            }

            public void HandleNativeClosed()
            {
                Complete(false, closeNative: false);
            }

            public void Close()
            {
                UnityMainThreadGuard.AssertCurrent();
                Cancel();
            }

            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                Complete(false);
            }

            private void Complete(bool confirmed, bool closeNative = true)
            {
                if (Interlocked.Exchange(ref finished, 1) != 0)
                {
                    return;
                }

                var currentModal = Interlocked.Exchange(ref modal, null);
                if (currentModal != null)
                {
                    currentModal.Closed -= HandleNativeClosed;
                    if (closeNative)
                    {
                        try
                        {
                            currentModal.Close();
                        }
                        catch (Exception exception)
                        {
                            ReportCleanupFailure("modal close", exception);
                        }
                    }
                }

                var callback = Interlocked.Exchange(ref completed, null);
                try
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
                catch (Exception exception)
                {
                    ReportCleanupFailure("modal lifetime release", exception);
                }

                if (callback == null || Volatile.Read(ref completionArmed) == 0)
                {
                    return;
                }

                foreach (var subscriber in callback.GetInvocationList())
                {
                    try
                    {
                        ((Action<bool>)subscriber)(confirmed);
                    }
                    catch (Exception exception)
                    {
                        try { logger.Error(exception, "A mod UI modal completion callback failed."); }
                        catch { }
                    }
                }
            }

            private void ReportCleanupFailure(string operation, Exception exception)
            {
                try { logger.Error(exception, "TopiaForgeUi " + operation + " failed."); }
                catch { }
            }
        }
    }
}
