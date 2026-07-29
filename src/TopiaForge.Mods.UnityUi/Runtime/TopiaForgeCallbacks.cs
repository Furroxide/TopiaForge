using System;

namespace TopiaForge.Mods.UnityUi
{
    /// <summary>Dispatches callback lists without allowing one owner to starve later owners.</summary>
    internal static class TopiaForgeCallbacks
    {
        public static void Invoke(Action? handlers, string description)
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
                    Report(description, exception);
                }
            }
        }

        public static void Invoke<T>(Action<T>? handlers, T value, string description)
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
                    Report(description, exception);
                }
            }
        }

        public static void Invoke<T1, T2>(Action<T1, T2>? handlers, T1 first, T2 second, string description)
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
                    Report(description, exception);
                }
            }
        }

        public static void Invoke<T1, T2, T3>(
            Action<T1, T2, T3>? handlers,
            T1 first,
            T2 second,
            T3 third,
            string description)
        {
            if (handlers == null)
            {
                return;
            }

            foreach (var subscriber in handlers.GetInvocationList())
            {
                try
                {
                    ((Action<T1, T2, T3>)subscriber)(first, second, third);
                }
                catch (Exception exception)
                {
                    Report(description, exception);
                }
            }
        }

        private static void Report(string description, Exception exception)
        {
            try
            {
                TopiaForgeLog.Warn(description + " callback failed: " + exception.Message);
            }
            catch
            {
                // Logging is advisory; a broken sink must not break callback isolation.
            }
        }
    }
}
