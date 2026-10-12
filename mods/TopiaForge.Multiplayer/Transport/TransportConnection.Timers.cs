using System;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>
    /// Timers: handshake retransmission and timeout, Completion resends, acknowledgement deadlines, loss and probe
    /// timeouts, keepalive, stall, idle timeout and the close drain. <see cref="NextTimeout"/> says when the next one
    /// falls due, so the I/O layer can sleep until then.
    /// </summary>
    internal sealed partial class TransportConnection
    {
        /// <summary>Runs every timer that is due and sends what the pacer allows.</summary>
        internal void Poll(long time)
        {
            now = time;
            switch (State)
            {
                case TransportConnectionState.Handshaking:
                    PollHandshake();
                    return;
                case TransportConnectionState.Draining:
                    if (closeResendPending)
                    {
                        closeResendPending = false;
                        closeResends++;
                        SendClose(lastCloseCode);
                    }

                    if (now >= drainDeadline) Finish(CloseReason);
                    return;
                case TransportConnectionState.Closed:
                    return;
            }

            if (now - lastReceivedAt >= options.IdleTimeoutMicros)
            {
                Finish(TransportCloseReason.IdleTimeout);
                return;
            }

            IsStalled = now - lastReceivedAt >= options.StallMicros;
            if (loss.OnTimer(now, rtt, options.MaximumAckDelayMicros, onLost)) probesPending = ProbesPerTimeout;
            PollCompletion();
            if (now - lastSentAt >= options.KeepaliveMicros) pingPending = true;
            SendPackets();
        }

        /// <summary>Gets the time the next timer falls due, or <see cref="long.MaxValue"/> when none is pending.</summary>
        internal long NextTimeout()
        {
            switch (State)
            {
                case TransportConnectionState.Handshaking:
                    return IsInitiator ? Math.Min(retransmitAt, handshakeDeadline) : handshakeDeadline;
                case TransportConnectionState.Draining:
                    return closeResendPending ? now : drainDeadline;
                case TransportConnectionState.Closed:
                    return long.MaxValue;
            }

            if (pendingClose.HasValue || probesPending > 0 || IsReadyToClose) return now;
            var deadline = Math.Min(lastReceivedAt + options.IdleTimeoutMicros, lastSentAt + options.KeepaliveMicros);
            if (!IsStalled) deadline = Math.Min(deadline, lastReceivedAt + options.StallMicros);
            deadline = Math.Min(deadline, acks.AckDeadline(options.MaximumAckDelayMicros));
            deadline = Math.Min(deadline, loss.TimerDeadline(rtt, options.MaximumAckDelayMicros));
            if (IsInitiator && !peerConfirmed && completionDatagram != null) deadline = Math.Min(deadline, completionResendAt);
            if (HasElicitingWork) deadline = Math.Min(deadline, pacer.NextAvailable(TransportOptions.MaximumDatagramBytes, now));
            return deadline;
        }
    }
}
