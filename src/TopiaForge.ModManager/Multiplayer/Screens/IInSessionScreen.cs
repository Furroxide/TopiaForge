namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Address-free presentation seam shared by the live HUD and the gallery.</summary>
    internal interface IInSessionScreen
    {
        bool IsAvailable { get; }
        bool IsOpen { get; }
        bool IsTyping { get; }
        int Revision { get; }
        string ConnectionText { get; }
        bool IsReconnecting { get; }
        void Open();
        void Close();
        void SetTyping(bool focused);
        bool Submit(string text);
        bool TryTakeLine(out ChatLine line);
    }

    internal readonly struct ChatLine
    {
        internal ChatLine(string author, string text, bool error = false)
        {
            Author = author;
            Text = text;
            Error = error;
        }
        internal string Author { get; }
        internal string Text { get; }
        internal bool Error { get; }
    }
}
