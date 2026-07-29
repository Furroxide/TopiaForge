using System;

namespace TopiaForge.Mods.Internal
{
    /// <summary>
    /// Dispatches first-party mod events one subscriber at a time. A consumer callback is an
    /// isolation boundary: one failing mod must not prevent later subscribers from observing a
    /// lifecycle transition or leave the publishing service half-finished.
    /// </summary>
    internal static class SafeEvent
    {
        public static void Invoke<T>(Action<T>? handlers, T value, Action<Exception>? onError = null)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (var subscriber in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T>)subscriber)(value);
                }
                catch (Exception exception)
                {
                    ReportError(onError, exception);
                }
            }
        }

        public static void Invoke(Action? handlers, Action<Exception>? onError = null)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (var subscriber in handlers.GetInvocationList())
            {
                try
                {
                    ((Action)subscriber)();
                }
                catch (Exception exception)
                {
                    ReportError(onError, exception);
                }
            }
        }

        public static void Invoke<T1, T2>(
            Action<T1, T2>? handlers,
            T1 first,
            T2 second,
            Action<Exception>? onError = null)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (var subscriber in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T1, T2>)subscriber)(first, second);
                }
                catch (Exception exception)
                {
                    ReportError(onError, exception);
                }
            }
        }

        private static void ReportError(Action<Exception>? onError, Exception subscriberFailure)
        {
            if (onError != null)
            {
                try
                {
                    onError(subscriberFailure);
                    return;
                }
                catch (Exception reportingFailure)
                {
                    FallbackToConsole(
                        "TopiaForge event subscriber failed ('" + subscriberFailure.Message
                        + "') and its error reporter also failed ('" + reportingFailure.Message + "').");
                    return;
                }
            }

            FallbackToConsole("TopiaForge event subscriber failed: " + subscriberFailure.Message);
        }

        private static void FallbackToConsole(string message)
        {
            try
            {
                Console.Error.WriteLine(message);
            }
            catch
            {
                // No independent diagnostic sink remains; event isolation must still preserve later subscribers.
            }
        }
    }
}
