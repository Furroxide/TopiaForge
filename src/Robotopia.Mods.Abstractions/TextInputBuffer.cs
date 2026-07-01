using System.Text;

namespace Robotopia.Mods
{
    /// <summary>
    /// A tiny, Unity-free typed-text accumulator for IMGUI/console chat boxes. Feed it Unity's
    /// <c>Input.inputString</c> each frame (which already contains printable characters plus backspace <c>'\b'</c> and
    /// return <c>'\n'</c>/<c>'\r'</c>); it maintains the current line, honours backspace, flags when the player pressed
    /// return, and clamps the length. Pure string handling so it unit-tests with no engine, and shared so the SDK and
    /// gameplay mods buffer text the same way (mirrors the base game's typed-text path).
    /// </summary>
    public sealed class TextInputBuffer
    {
        private readonly StringBuilder builder = new StringBuilder();
        private readonly int maxChars;
        private bool submitRequested;

        /// <summary>Creates a buffer.</summary>
        /// <param name="maxChars">Hard cap on the line length (extra characters are dropped). Default 200.</param>
        public TextInputBuffer(int maxChars = 200)
        {
            this.maxChars = maxChars > 0 ? maxChars : 200;
        }

        /// <summary>The current typed line.</summary>
        public string Text => builder.ToString();

        /// <summary>The current line length.</summary>
        public int Length => builder.Length;

        /// <summary>
        /// Process a frame's worth of input characters (Unity's <c>Input.inputString</c>): printable characters are
        /// appended (up to the cap), <c>'\b'</c> deletes the last character, and <c>'\n'</c>/<c>'\r'</c> raises the
        /// submit flag (read it with <see cref="ConsumeSubmit"/>).
        /// </summary>
        public void Append(string? inputString)
        {
            if (string.IsNullOrEmpty(inputString))
            {
                return;
            }

            foreach (var ch in inputString!)
            {
                switch (ch)
                {
                    case '\b':
                        if (builder.Length > 0)
                        {
                            builder.Length -= 1;
                        }

                        break;
                    case '\n':
                    case '\r':
                        submitRequested = true;
                        break;
                    default:
                        if (ch >= ' ' && builder.Length < maxChars)
                        {
                            builder.Append(ch);
                        }

                        break;
                }
            }
        }

        /// <summary>Delete the last character, if any.</summary>
        public void Backspace()
        {
            if (builder.Length > 0)
            {
                builder.Length -= 1;
            }
        }

        /// <summary>Clear the line and the submit flag.</summary>
        public void Clear()
        {
            builder.Length = 0;
            submitRequested = false;
        }

        /// <summary>
        /// Returns <c>true</c> once if a return was pressed since the last call, clearing the flag. Lets the caller
        /// detect "submit" without polling key state directly.
        /// </summary>
        public bool ConsumeSubmit()
        {
            if (!submitRequested)
            {
                return false;
            }

            submitRequested = false;
            return true;
        }
    }
}
