using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>What a chat line is, which decides its tone.</summary>
    public enum TopiaForgeChatLineKind
    {
        /// <summary>A message from a player.</summary>
        Player,

        /// <summary>A notice from the game or session (joins, leaves, command output).</summary>
        System,

        /// <summary>A private message.</summary>
        Whisper,

        /// <summary>A failure, such as a rejected command.</summary>
        Error,
    }

    /// <summary>One retained chat line.</summary>
    public readonly struct TopiaForgeChatLine
    {
        internal TopiaForgeChatLine(string text, TopiaForgeChatLineKind kind, float postedAt, long sequence)
        {
            Text = text;
            Kind = kind;
            PostedAt = postedAt;
            Sequence = sequence;
        }

        /// <summary>Gets the sanitized, bounded text.</summary>
        public string Text { get; }

        /// <summary>Gets the line kind.</summary>
        public TopiaForgeChatLineKind Kind { get; }

        /// <summary>Gets when the line was posted, on the caller's clock (seconds).</summary>
        public float PostedAt { get; }

        /// <summary>Gets the line's position in the log's history, starting at 1.</summary>
        public long Sequence { get; }
    }

    /// <summary>
    /// A bounded chat history: a ring buffer that evicts the oldest line once full. Text is passed
    /// through <see cref="TopiaForgePlainText.Sanitize"/>, so a line is always one plain line of at
    /// most <see cref="MaxLineChars"/> characters. Unity-free; the clock is supplied by the caller.
    /// </summary>
    public sealed class TopiaForgeChatLogModel
    {
        /// <summary>The smallest capacity.</summary>
        public const int MinCapacity = 1;

        /// <summary>The largest capacity.</summary>
        public const int MaxCapacity = 512;

        /// <summary>The default capacity.</summary>
        public const int DefaultCapacity = 100;

        /// <summary>The default line bound: a 256-character message plus a short author prefix.</summary>
        public const int DefaultMaxLineChars = 300;

        private readonly TopiaForgeChatLine[] lines;
        private int head;
        private long sequence;

        /// <summary>Creates an empty log.</summary>
        /// <exception cref="ArgumentOutOfRangeException">A bound is outside its supported range.</exception>
        public TopiaForgeChatLogModel(int capacity = DefaultCapacity, int maxLineChars = DefaultMaxLineChars)
        {
            if (capacity < MinCapacity || capacity > MaxCapacity)
            {
                throw new ArgumentOutOfRangeException(nameof(capacity), "Chat capacity must be between " + MinCapacity + " and " + MaxCapacity + ".");
            }

            if (maxLineChars < 1 || maxLineChars > 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(maxLineChars), "A chat line must allow between 1 and 4096 characters.");
            }

            lines = new TopiaForgeChatLine[capacity];
            MaxLineChars = maxLineChars;
        }

        /// <summary>Gets how many lines the log retains.</summary>
        public int Capacity => lines.Length;

        /// <summary>Gets the longest line, in UTF-16 code units, after sanitizing.</summary>
        public int MaxLineChars { get; }

        /// <summary>Gets the number of retained lines.</summary>
        public int Count { get; private set; }

        /// <summary>Gets how many lines have been evicted to make room.</summary>
        public long EvictedCount { get; private set; }

        /// <summary>Gets the sequence of the newest line, or 0 when nothing was ever posted.</summary>
        public long LastSequence => sequence;

        /// <summary>Gets a retained line; 0 is the oldest and <c>Count - 1</c> the newest.</summary>
        public TopiaForgeChatLine this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                return lines[(head + index) % lines.Length];
            }
        }

        /// <summary>
        /// Appends a line, evicting the oldest when the log is full. Text that sanitizes to nothing is
        /// refused.
        /// </summary>
        public bool TryAppend(string? text, TopiaForgeChatLineKind kind, float now, out TopiaForgeChatLine line)
        {
            var clean = TopiaForgePlainText.Sanitize(text, MaxLineChars);
            if (clean.Length == 0)
            {
                line = default;
                return false;
            }

            line = new TopiaForgeChatLine(clean, kind, float.IsNaN(now) ? 0f : now, ++sequence);
            if (Count < lines.Length)
            {
                lines[(head + Count) % lines.Length] = line;
                Count++;
            }
            else
            {
                lines[head] = line;
                head = (head + 1) % lines.Length;
                EvictedCount++;
            }

            return true;
        }

        /// <summary>Removes every line; sequences keep counting.</summary>
        public void Clear()
        {
            Array.Clear(lines, 0, lines.Length);
            head = 0;
            Count = 0;
        }

        /// <summary>
        /// Counts the newest lines that are still visible while the log is closed, scanning back from
        /// the newest until a line has fully faded. Posting times are assumed non-decreasing.
        /// </summary>
        public int CountVisibleWhenClosed(float now, float visibleSeconds, float fadeSeconds, int maxLines)
        {
            var visible = 0;
            for (var index = Count - 1; index >= 0 && visible < maxLines; index--)
            {
                if (TopiaForgeChatFade.IsExpired(now - this[index].PostedAt, visibleSeconds, fadeSeconds))
                {
                    break;
                }

                visible++;
            }

            return visible;
        }
    }

    /// <summary>Fade timing for chat lines while the log is closed.</summary>
    public static class TopiaForgeChatFade
    {
        /// <summary>The default time a new line stays fully visible.</summary>
        public const float DefaultVisibleSeconds = 8f;

        /// <summary>The default fade duration after that.</summary>
        public const float DefaultFadeSeconds = 1.5f;

        /// <summary>
        /// Opacity of a line <paramref name="ageSeconds"/> old: fully visible, then a linear fade.
        /// Reduced motion replaces the fade with an immediate hide at the end of the visible time.
        /// </summary>
        public static float Alpha(float ageSeconds, float visibleSeconds, float fadeSeconds, bool reducedMotion)
        {
            if (float.IsNaN(ageSeconds) || ageSeconds < 0f)
            {
                ageSeconds = 0f;
            }

            if (ageSeconds < visibleSeconds)
            {
                return 1f;
            }

            if (reducedMotion || !(fadeSeconds > 0f))
            {
                return 0f;
            }

            var t = (ageSeconds - visibleSeconds) / fadeSeconds;
            return t >= 1f ? 0f : 1f - t;
        }

        /// <summary>Gets whether a line of this age has fully faded.</summary>
        public static bool IsExpired(float ageSeconds, float visibleSeconds, float fadeSeconds)
        {
            if (float.IsNaN(ageSeconds) || ageSeconds < 0f)
            {
                return false;
            }

            return ageSeconds >= visibleSeconds + (fadeSeconds > 0f ? fadeSeconds : 0f);
        }
    }
}
