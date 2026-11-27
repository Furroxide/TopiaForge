namespace TopiaForge.Multiplayer.Engine
{
    internal readonly struct PrivateSentValue
    {
        internal PrivateSentValue(PerPlayerRecord record, ulong version, long atMicros)
        { Record = record; Version = version; AtMicros = atMicros; }
        internal PerPlayerRecord Record { get; }
        internal ulong Version { get; }
        internal long AtMicros { get; }
    }
}
