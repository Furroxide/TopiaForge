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

        /// <summary>
        /// Dispatches what the player's own input on a control <paramref name="ownerId"/> owns runs, inside the scope of that
        /// player's action (<see cref="TopiaForgeIntent"/>): a click, a confirm, a chosen or typed value, a selection.
        /// </summary>
        public static void Player(string? ownerId, Action? handlers, string description)
        {
            if (handlers == null)
            {
                return;
            }

            using (TopiaForgeIntent.PlayerAction(ownerId))
            {
                Invoke(handlers, description);
            }
        }

        public static void Player<T>(string? ownerId, Action<T>? handlers, T value, string description)
        {
            if (handlers == null)
            {
                return;
            }

            using (TopiaForgeIntent.PlayerAction(ownerId))
            {
                Invoke(handlers, value, description);
            }
        }

        public static void Player<T1, T2>(string? ownerId, Action<T1, T2>? handlers, T1 first, T2 second, string description)
        {
            if (handlers == null)
            {
                return;
            }

            using (TopiaForgeIntent.PlayerAction(ownerId))
            {
                Invoke(handlers, first, second, description);
            }
        }

        /// <summary>Runs a completion the kit deferred, such as an animation's, with the intent captured where it began.</summary>
        public static void Resume(object? captured, Action? handlers, string description)
        {
            if (handlers == null)
            {
                return;
            }

            using (TopiaForgeIntent.Resume(captured))
            {
                Invoke(handlers, description);
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
