using System;
using System.Diagnostics.CodeAnalysis;

namespace TopiaForge.Mods
{
    /// <summary>Identifies an expected SDK operation failure without requiring string parsing.</summary>
    public enum ModErrorCode
    {
        /// <summary>No error occurred.</summary>
        None = 0,

        /// <summary>An argument did not satisfy the operation contract.</summary>
        InvalidArgument = 1,

        /// <summary>The requested resource does not exist.</summary>
        NotFound = 2,

        /// <summary>The requested capability is not available in the current game or runtime.</summary>
        Unavailable = 3,

        /// <summary>The request conflicts with current state or another owner.</summary>
        Conflict = 4,

        /// <summary>The operation is not valid in the current lifecycle state.</summary>
        InvalidState = 5,

        /// <summary>The operation was cancelled before it completed.</summary>
        Cancelled = 6,

        /// <summary>The operation did not complete within its allowed time.</summary>
        TimedOut = 7,

        /// <summary>A filesystem or stream operation failed.</summary>
        Io = 8,

        /// <summary>An external game or platform operation failed.</summary>
        External = 9,

        /// <summary>The operation failed for an unclassified reason.</summary>
        Unknown = 10,

        /// <summary>The current process does not have authority to perform the requested shared-world mutation.</summary>
        NotAuthoritative = 11,

        /// <summary>The authenticated caller exceeded a bounded operation rate.</summary>
        RateLimited = 12
    }

    /// <summary>
    /// Represents either a successful SDK operation value or a stable error code with a human-readable message.
    /// </summary>
    /// <typeparam name="T">The value produced by a successful operation.</typeparam>
    public sealed class OperationResult<T> where T : notnull
    {
        private OperationResult(bool succeeded, T? value, ModErrorCode errorCode, string errorMessage)
        {
            Succeeded = succeeded;
            Value = value;
            ErrorCode = errorCode;
            ErrorMessage = errorMessage;
        }

        /// <summary>Gets whether the operation succeeded.</summary>
        public bool Succeeded { get; }

        /// <summary>Gets the successful value, or the default value of <typeparamref name="T"/> after failure.</summary>
        public T? Value { get; }

        /// <summary>Gets the stable error code, or <see cref="ModErrorCode.None"/> after success.</summary>
        public ModErrorCode ErrorCode { get; }

        /// <summary>Gets a user-readable failure description, or an empty string after success.</summary>
        public string ErrorMessage { get; }

        /// <summary>Creates a successful result.</summary>
        /// <param name="value">The value produced by the operation.</param>
        /// <returns>A successful result containing <paramref name="value"/>.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
        public static OperationResult<T> Success(T value)
        {
            if (value is null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            return new OperationResult<T>(true, value, ModErrorCode.None, string.Empty);
        }

        /// <summary>Creates a failed result.</summary>
        /// <param name="errorCode">A stable code other than <see cref="ModErrorCode.None"/>.</param>
        /// <param name="errorMessage">A concise description suitable for logs and diagnostics.</param>
        /// <returns>A failed result.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="errorCode"/> is <see cref="ModErrorCode.None"/>.</exception>
        public static OperationResult<T> Failure(ModErrorCode errorCode, string errorMessage)
        {
            if (errorCode == ModErrorCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(errorCode), "A failed result must have an error code.");
            }

            return new OperationResult<T>(false, default, errorCode, errorMessage ?? string.Empty);
        }

        /// <summary>Tries to obtain the successful value.</summary>
        /// <param name="value">Receives the successful value, or the default value after failure.</param>
        /// <returns><see langword="true"/> when this result succeeded.</returns>
        public bool TryGetValue([MaybeNullWhen(false)] out T value)
        {
            value = Value;
            return Succeeded;
        }
    }
}
