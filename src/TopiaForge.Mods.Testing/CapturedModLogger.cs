using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Identifies the severity of a captured SDK log entry.</summary>
    public enum CapturedLogLevel
    {
        /// <summary>Diagnostic detail.</summary>
        Debug = 0,

        /// <summary>Ordinary informational output.</summary>
        Information = 1,

        /// <summary>A recoverable warning.</summary>
        Warning = 2,

        /// <summary>An error.</summary>
        Error = 3
    }

    /// <summary>Represents one message captured from a mod under test.</summary>
    public sealed class CapturedLogEntry
    {
        /// <summary>Creates a captured log entry.</summary>
        /// <param name="level">The message severity.</param>
        /// <param name="message">The message text.</param>
        /// <param name="exception">The associated exception, when present.</param>
        public CapturedLogEntry(CapturedLogLevel level, string message, Exception? exception = null)
        {
            Level = level;
            Message = message ?? string.Empty;
            Exception = exception;
        }

        /// <summary>Gets the message severity.</summary>
        public CapturedLogLevel Level { get; }

        /// <summary>Gets the message text.</summary>
        public string Message { get; }

        /// <summary>Gets the associated exception, when present.</summary>
        public Exception? Exception { get; }
    }

    /// <summary>Captures logger calls in memory for runner-independent assertions.</summary>
    public sealed class CapturedModLogger : IModLogger
    {
        private readonly object gate = new object();
        private readonly List<CapturedLogEntry> entries = new List<CapturedLogEntry>();

        /// <summary>Gets a stable snapshot of all captured entries.</summary>
        public IReadOnlyList<CapturedLogEntry> Entries
        {
            get
            {
                lock (gate)
                {
                    return new List<CapturedLogEntry>(entries).AsReadOnly();
                }
            }
        }

        /// <summary>Removes every captured entry.</summary>
        public void Clear()
        {
            lock (gate)
            {
                entries.Clear();
            }
        }

        /// <summary>Gets the number of entries at a particular severity.</summary>
        /// <param name="level">The severity to count.</param>
        /// <returns>The number of matching messages.</returns>
        public int Count(CapturedLogLevel level)
        {
            lock (gate)
            {
                var count = 0;
                foreach (var entry in entries)
                {
                    if (entry.Level == level)
                    {
                        count++;
                    }
                }

                return count;
            }
        }

        /// <inheritdoc/>
        public void Debug(string message) => Add(CapturedLogLevel.Debug, message, null);

        /// <inheritdoc/>
        public void Info(string message) => Add(CapturedLogLevel.Information, message, null);

        /// <inheritdoc/>
        public void Warn(string message) => Add(CapturedLogLevel.Warning, message, null);

        /// <inheritdoc/>
        public void Error(string message) => Add(CapturedLogLevel.Error, message, null);

        /// <inheritdoc/>
        public void Error(Exception exception, string message) =>
            Add(CapturedLogLevel.Error, message, exception ?? throw new ArgumentNullException(nameof(exception)));

        private void Add(CapturedLogLevel level, string message, Exception? exception)
        {
            lock (gate)
            {
                entries.Add(new CapturedLogEntry(level, message, exception));
            }
        }
    }
}
