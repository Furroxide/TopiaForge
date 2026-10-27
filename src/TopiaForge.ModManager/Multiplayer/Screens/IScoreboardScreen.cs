using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Display data only. Actions retain the row object, never its sorted position.</summary>
    internal sealed class ScoreboardRow
    {
        internal ScoreboardRow(string name, bool host, bool local)
        { Name = name; IsHost = host; IsLocal = local; }
        internal string Name { get; }
        internal bool IsHost { get; }
        internal bool IsLocal { get; }
        internal bool CanModerate { get; set; }
        internal bool CanHear { get; set; }
        internal bool Muted { get; set; }
        internal bool ServerMuted { get; set; }
        internal float Volume { get; set; } = 1f;
        internal int Signal { get; set; } = -1;
        internal VoiceIndicator Talk { get; set; }
        internal string[] Values { get; } = new string[64];
    }

    /// <summary>The bounded scoreboard shared by the live HUD and gallery; no session or transport identities escape.</summary>
    internal interface IScoreboardScreen
    {
        IReadOnlyList<ScoreboardRow> Rows { get; }
        IReadOnlyList<string> Columns { get; }
        string Note { get; }
        int SortColumn { get; }
        bool Descending { get; }
        void Sort(int column);
        void Mute(ScoreboardRow row);
        void Volume(ScoreboardRow row, float value);
        void ServerMute(ScoreboardRow row);
        void Kick(ScoreboardRow row);
        void Ban(ScoreboardRow row);
    }
}
