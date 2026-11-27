using System;
using System.Reflection;
using System.Threading;

namespace TopiaForge.ModManager.Core.Multiplayer
{
    /// <summary>
    /// The logical call context, as an intent scope keeps its record there on the game's Mono (WS3.4). A value set in it
    /// changes the current execution context in place, and capturing that context, as an await, <c>Task.Run</c>, a timer
    /// or the thread pool does, copies what the slot holds then. A continuation therefore keeps the record it was captured
    /// with, nothing it captured changes afterwards, and an empty slot is never copied at all. Opening and closing a scope
    /// so allocates nothing, where an async-local copies the runtime's map of async-locals on every change.
    /// </summary>
    /// <remarks>
    /// Mono's and the .NET Framework's corlib have it, behind the public members of
    /// <c>System.Runtime.Remoting.Messaging.CallContext</c>. The netstandard2.1 surface does not name that type, so its
    /// public members are bound by name, once; .NET has none, and reuses captured execution contexts instead.
    /// <see cref="TryBind"/> checks the behaviour above on the running runtime before a scope relies on it.
    /// </remarks>
    internal sealed class IntentLogicalStore
    {
        private const string CallContextTypeName = "System.Runtime.Remoting.Messaging.CallContext";
        private const string ProbeSlot = "TopiaForge.Intent.Probe";
        private readonly Func<string, object?> get;
        private readonly Action<string, object> set;
        private readonly Action<string> free;

        /// <param name="get">Reads a slot of the current execution context; null when the slot is empty.</param>
        /// <param name="set">Sets a slot of the current execution context in place.</param>
        /// <param name="free">Removes a slot from the current execution context in place.</param>
        internal IntentLogicalStore(Func<string, object?> get, Action<string, object> set, Action<string> free)
        {
            this.get = get ?? throw new ArgumentNullException(nameof(get));
            this.set = set ?? throw new ArgumentNullException(nameof(set));
            this.free = free ?? throw new ArgumentNullException(nameof(free));
        }

        /// <summary>Reads <paramref name="slot"/> in the current execution context; null when it is empty.</summary>
        internal object? Get(string slot) => get(slot);

        /// <summary>Sets <paramref name="slot"/> in the current execution context.</summary>
        internal void Set(string slot, object value) => set(slot, value);

        /// <summary>
        /// Empties <paramref name="slot"/> by removing it, never by setting it to null: a context whose slots are all gone
        /// is copied by no capture, so at rest an await costs what it did before intent tracking.
        /// </summary>
        internal void Free(string slot) => free(slot);

        /// <summary>
        /// The running runtime's logical call context when it has one that keeps values as <see cref="KeepsCapturedValues"/>
        /// requires; null otherwise, as on .NET. Binding never throws: any failure means there is none.
        /// </summary>
        internal static IntentLogicalStore? TryBind() => TryBind(FindCallContext());

        /// <summary>
        /// The logical call context that <paramref name="callContext"/> offers through public static members named and
        /// shaped as corlib's <c>CallContext</c> names them, when it keeps values as <see cref="KeepsCapturedValues"/>
        /// requires; null otherwise, and for a null type. Never throws.
        /// </summary>
        internal static IntentLogicalStore? TryBind(Type? callContext)
        {
            var store = BindMembers(callContext);
            return store != null && KeepsCapturedValues(store) ? store : null;
        }

        /// <summary>
        /// Whether <paramref name="store"/> keeps values as an intent scope needs, checked on the calling thread: a value
        /// reads back in place; a context captured while it is set keeps it after the slot is emptied; a context captured
        /// after that has none; and a value set while running a captured context stays in that context. Leaves the slot it
        /// checks with empty; never throws.
        /// </summary>
        internal static bool KeepsCapturedValues(IntentLogicalStore store)
        {
            if (store == null) throw new ArgumentNullException(nameof(store));
            var marker = new object();
            try
            {
                if (store.Get(ProbeSlot) != null) return false;
                store.Set(ProbeSlot, marker);
                if (!ReferenceEquals(store.Get(ProbeSlot), marker)) return false;
                var whileSet = ExecutionContext.Capture();
                store.Free(ProbeSlot);
                if (store.Get(ProbeSlot) != null) return false;
                var afterFree = ExecutionContext.Capture();
                if (whileSet == null || afterFree == null) return false;
                var seen = new object?[] { null, marker };
                ExecutionContext.Run(whileSet, _ => seen[0] = store.Get(ProbeSlot), null);
                ExecutionContext.Run(afterFree, _ =>
                {
                    seen[1] = store.Get(ProbeSlot);
                    store.Set(ProbeSlot, marker);
                }, null);
                return ReferenceEquals(seen[0], marker) && seen[1] == null && store.Get(ProbeSlot) == null;
            }
            catch (Exception)
            {
                // A runtime that refuses any of this has no logical call context a scope can rely on.
                return false;
            }
            finally
            {
                TryFree(store);
            }
        }

        private static void TryFree(IntentLogicalStore store)
        {
            try
            {
                store.Free(ProbeSlot);
            }
            catch (Exception)
            {
                // The check already failed; the slot is the probe's own and nothing reads it.
            }
        }

        private static Type? FindCallContext()
        {
            try
            {
                return Type.GetType(CallContextTypeName, throwOnError: false);
            }
            catch (Exception)
            {
                // A runtime that refuses to look the type up has none.
                return null;
            }
        }

        private static IntentLogicalStore? BindMembers(Type? type)
        {
            if (type == null) return null;
            try
            {
                var get = Bind<Func<string, object?>>(type, "LogicalGetData", typeof(string));
                var set = Bind<Action<string, object>>(type, "LogicalSetData", typeof(string), typeof(object));
                var free = Bind<Action<string>>(type, "FreeNamedDataSlot", typeof(string));
                return get == null || set == null || free == null ? null : new IntentLogicalStore(get, set, free);
            }
            catch (Exception)
            {
                // A runtime that hides or refuses those members has none.
                return null;
            }
        }

        // Public static members only: this binds the runtime's documented API, never its internals.
        private static T? Bind<T>(Type type, string name, params Type[] parameters)
            where T : Delegate
        {
            var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            return method == null ? null : Delegate.CreateDelegate(typeof(T), method, throwOnBindFailure: false) as T;
        }
    }
}
