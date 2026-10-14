using System;
using System.Collections.Generic;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Stun
{
    /// <summary>
    /// Retransmission timing for one request over UDP (RFC 8489, section 6.2.1): the request goes out, then again after
    /// RTO, 2 RTO, 4 RTO and so on, up to the given number of transmissions; the transaction fails a final wait of
    /// <see cref="FinalWaitMultiplier"/> initial RTOs after the last one.
    /// </summary>
    internal readonly struct StunTiming
    {
        internal StunTiming(long initialRtoMicros, int transmissions, int finalWaitMultiplier)
        {
            if (initialRtoMicros <= 0 || transmissions < 1 || finalWaitMultiplier < 1) throw new ArgumentOutOfRangeException(nameof(initialRtoMicros));
            InitialRtoMicros = initialRtoMicros;
            Transmissions = transmissions;
            FinalWaitMultiplier = finalWaitMultiplier;
        }

        /// <summary>RFC 8489's defaults: 500 ms, seven transmissions and a 16-RTO wait, 39.5 seconds in all.</summary>
        internal static StunTiming Rfc8489 => new StunTiming(500_000, 7, 16);

        /// <summary>Gathering: 500 ms, four transmissions, a four-RTO wait: settled within 5.5 seconds.</summary>
        internal static StunTiming Gathering => new StunTiming(500_000, 4, 4);

        /// <summary>TURN requests on a live allocation: 500 ms, six transmissions, a 16-RTO wait: 23.5 seconds.</summary>
        internal static StunTiming Relay => new StunTiming(500_000, 6, 16);

        /// <summary>Connectivity checks: 250 ms, five transmissions, a four-RTO wait: settled within 4.75 seconds.</summary>
        internal static StunTiming Check => new StunTiming(250_000, 5, 4);

        internal long InitialRtoMicros { get; }

        internal int Transmissions { get; }

        internal int FinalWaitMultiplier { get; }

        /// <summary>Gets how long the whole transaction may take before it fails.</summary>
        internal long TotalMicros
        {
            get
            {
                var interval = InitialRtoMicros;
                var total = 0L;
                for (var send = 1; send < Transmissions; send++)
                {
                    total += interval;
                    interval *= 2;
                }

                return total + (InitialRtoMicros * FinalWaitMultiplier);
            }
        }
    }

    /// <summary>Whoever started a transaction hears how it ended, on the thread that runs the client.</summary>
    internal interface IStunTransactionOwner
    {
        /// <summary>
        /// Gets whether <paramref name="response"/> may complete the transaction, typically by checking its integrity.
        /// A response it refuses is dropped and the request keeps retransmitting, as if it had never arrived.
        /// </summary>
        bool Accepts(StunTransaction transaction, StunMessage response);

        /// <summary>A response with the transaction's id, accepted, arrived from the path the request went to.</summary>
        void OnResponse(StunTransaction transaction, StunMessage response, long now);

        void OnTimeout(StunTransaction transaction, long now);
    }

    /// <summary>One outstanding request: its bytes, where it went, and when it goes again.</summary>
    internal sealed class StunTransaction
    {
        internal StunTransaction(StunTransactionId id, TransportPath destination, byte[] request, int length, StunTiming timing, IStunTransactionOwner owner, object? tag, long now)
        {
            Id = id;
            Destination = destination;
            Request = request;
            RequestLength = length;
            Timing = timing;
            Owner = owner;
            Tag = tag;
            StartedAt = now;
            NextSendAt = now;
            Interval = timing.InitialRtoMicros;
        }

        internal StunTransactionId Id { get; }

        internal TransportPath Destination { get; }

        internal byte[] Request { get; }

        internal int RequestLength { get; }

        internal StunTiming Timing { get; }

        internal IStunTransactionOwner Owner { get; }

        /// <summary>Gets the owner's own context for the transaction, such as the pair a check is for.</summary>
        internal object? Tag { get; }

        internal long StartedAt { get; }

        internal int Sends { get; set; }

        internal long NextSendAt { get; set; }

        internal long Interval { get; set; }

        internal long FirstSentAt { get; set; }

        internal long LastSentAt { get; set; }

        internal bool IsDone { get; set; }
    }

    /// <summary>
    /// Runs STUN transactions sans-I/O: binding requests, TURN requests and connectivity checks alike. It sends through a
    /// callback, retransmits on RFC 8489's schedule, and matches a response by transaction id and by the path it came
    /// from, which must be the path the request went to. At most <see cref="MaximumOutstanding"/> transactions run at once.
    /// </summary>
    internal sealed class StunClient
    {
        internal const int MaximumOutstanding = 256;

        private readonly ITransportRandom random;
        private readonly Action<TransportPath, byte[], int> send;
        private readonly Dictionary<StunTransactionId, StunTransaction> outstanding = new Dictionary<StunTransactionId, StunTransaction>();
        private readonly List<StunTransaction> due = new List<StunTransaction>();
        private readonly List<StunTransaction> cancelling = new List<StunTransaction>();
        private readonly byte[] idScratch = new byte[StunTransactionId.Size];

        internal StunClient(ITransportRandom random, Action<TransportPath, byte[], int> send)
        {
            this.random = random ?? throw new ArgumentNullException(nameof(random));
            this.send = send ?? throw new ArgumentNullException(nameof(send));
        }

        internal int Outstanding => outstanding.Count;

        /// <summary>Gets a fresh transaction id that no outstanding transaction uses.</summary>
        internal StunTransactionId NewTransactionId()
        {
            while (true)
            {
                var id = StunTransactionId.New(random, idScratch);
                if (!outstanding.ContainsKey(id)) return id;
            }
        }

        /// <summary>
        /// Starts a transaction for a request already written with <paramref name="id"/>; the first copy goes out now.
        /// Returns null, sending nothing, when the client is full.
        /// </summary>
        internal StunTransaction? Begin(TransportPath destination, byte[] request, int length, StunTransactionId id, StunTiming timing, IStunTransactionOwner owner, object? tag, long now)
        {
            if (destination == null) throw new ArgumentNullException(nameof(destination));
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (owner == null) throw new ArgumentNullException(nameof(owner));
            if (length < StunMessage.HeaderSize || length > request.Length) throw new ArgumentOutOfRangeException(nameof(length));
            if (outstanding.Count >= MaximumOutstanding || outstanding.ContainsKey(id)) return null;
            var copy = new byte[length];
            Buffer.BlockCopy(request, 0, copy, 0, length);
            var transaction = new StunTransaction(id, destination, copy, length, timing, owner, tag, now);
            outstanding.Add(id, transaction);
            Transmit(transaction, now);
            return transaction;
        }

        /// <summary>Hands a response to its transaction's owner; false when no transaction matches it from that path or the owner refuses it.</summary>
        internal bool TryComplete(StunMessage response, TransportPath from, long now)
        {
            if (response == null || from == null) return false;
            if (response.Class != StunClass.Success && response.Class != StunClass.Error) return false;
            if (!outstanding.TryGetValue(response.TransactionId, out var transaction) || !transaction.Destination.Equals(from)) return false;
            if (!transaction.Owner.Accepts(transaction, response)) return false;
            outstanding.Remove(response.TransactionId);
            transaction.IsDone = true;
            transaction.Owner.OnResponse(transaction, response, now);
            return true;
        }

        /// <summary>Gets whether a response with this id would match a transaction, without completing it.</summary>
        internal bool IsOutstanding(StunTransactionId id) => outstanding.ContainsKey(id);

        internal void Cancel(StunTransaction? transaction)
        {
            if (transaction == null || transaction.IsDone) return;
            transaction.IsDone = true;
            outstanding.Remove(transaction.Id);
        }

        /// <summary>Cancels every transaction <paramref name="owner"/> started, without telling it.</summary>
        internal void CancelAll(IStunTransactionOwner owner)
        {
            cancelling.Clear();
            foreach (var transaction in outstanding.Values)
            {
                if (ReferenceEquals(transaction.Owner, owner)) cancelling.Add(transaction);
            }

            foreach (var transaction in cancelling) Cancel(transaction);
            cancelling.Clear();
        }

        /// <summary>Drops every transaction without telling anyone: the agent is gone.</summary>
        internal void Clear()
        {
            foreach (var transaction in outstanding.Values) transaction.IsDone = true;
            outstanding.Clear();
        }

        /// <summary>Retransmits what is due and fails what ran out of time. An owner may start or cancel transactions meanwhile.</summary>
        internal void Poll(long now)
        {
            due.Clear();
            foreach (var transaction in outstanding.Values)
            {
                if (transaction.NextSendAt <= now) due.Add(transaction);
            }

            foreach (var transaction in due)
            {
                if (transaction.IsDone) continue;
                if (transaction.Sends >= transaction.Timing.Transmissions)
                {
                    outstanding.Remove(transaction.Id);
                    transaction.IsDone = true;
                    transaction.Owner.OnTimeout(transaction, now);
                }
                else
                {
                    Transmit(transaction, now);
                }
            }

            due.Clear();
        }

        internal long NextTimeoutMicros()
        {
            var next = long.MaxValue;
            foreach (var transaction in outstanding.Values)
            {
                if (transaction.NextSendAt < next) next = transaction.NextSendAt;
            }

            return next;
        }

        private void Transmit(StunTransaction transaction, long now)
        {
            if (transaction.Sends == 0) transaction.FirstSentAt = now;
            transaction.Sends++;
            transaction.LastSentAt = now;
            if (transaction.Sends >= transaction.Timing.Transmissions)
            {
                transaction.NextSendAt = now + (transaction.Timing.InitialRtoMicros * transaction.Timing.FinalWaitMultiplier);
            }
            else
            {
                transaction.NextSendAt = now + transaction.Interval;
                transaction.Interval *= 2;
            }

            send(transaction.Destination, transaction.Request, transaction.RequestLength);
        }
    }
}
