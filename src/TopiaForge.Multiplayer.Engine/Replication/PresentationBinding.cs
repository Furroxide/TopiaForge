using System;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>One registered presentation event: its codec and, on an interactive process, its local handler.</summary>
    internal abstract class PresentationBinding : IPresentationEventRegistration
    {
        protected PresentationBinding(SessionEngine engine, WireKey key, bool hasHandler)
        {
            Engine = engine;
            Key = key;
            HasHandler = hasHandler;
        }

        internal SessionEngine Engine { get; }
        internal WireKey Key { get; }
        internal bool HasHandler { get; }

        public string Id => Key.Id;

        public bool IsActive { get; internal set; } = true;

        public void Dispose() => Engine.UnregisterPresentation(this);

        /// <summary>Decodes the event with the local codec and invokes the handler, if any.</summary>
        internal abstract void Deliver(byte[] bytes);
    }

    internal sealed class PresentationBinding<TEvent> : PresentationBinding where TEvent : class
    {
        internal PresentationBinding(SessionEngine engine, WireKey key, PresentationEventDefinition<TEvent> definition)
            : base(engine, key, definition.Handler != null)
        {
            Definition = definition;
        }

        internal PresentationEventDefinition<TEvent> Definition { get; }

        internal override void Deliver(byte[] bytes)
        {
            var handler = Definition.Handler;
            if (!IsActive || handler == null) return;
            var decoded = EngineCodec.Decode(Definition.Type.Codec, bytes);
            if (!decoded.TryGetValue(out var value))
            {
                Engine.ReportCodecFailure("presentation event '" + Key + "'", decoded.ErrorMessage);
                return;
            }

            Engine.InvokeCallback(handler, value, "presentation handler for '" + Key + "'");
        }
    }

    /// <summary>A generated contract descriptor registered for admission.</summary>
    internal sealed class ContractRegistration
    {
        internal ContractRegistration(WireKey key, MultiplayerContractDescriptor descriptor)
        {
            Key = key;
            Descriptor = descriptor;
        }

        internal WireKey Key { get; }
        internal MultiplayerContractDescriptor Descriptor { get; }

        internal ContractEntry ToEntry() =>
            new ContractEntry(
                Key.Owner,
                Key.Id,
                Descriptor.WireFormatRevision,
                JoinMessages.NormalizeDigest(Descriptor.SchemaSha256));
    }
}
