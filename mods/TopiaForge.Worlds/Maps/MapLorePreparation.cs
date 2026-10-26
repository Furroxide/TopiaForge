using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Worlds.Maps
{
    // Native map play prepares catalog lore before import. Keep that choice in one adapter;
    // cancellation must drain its awaiter before the owning scene can be discarded.
    internal static class MapLorePreparation
    {
        internal static async Task PrepareAsync(object project, object config, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var type = Type.GetType("UgcCatalogLoreLoader, GameCode", false);
            var constructor = type?.GetConstructor(new[] { config.GetType() });
            var method = type?.GetMethod("AddMissingLoreAsync", BindingFlags.Public | BindingFlags.Instance, null,
                new[] { project.GetType(), typeof(CancellationToken) }, null);
            if (constructor == null || method == null || !method.ReturnType.IsGenericType ||
                method.ReturnType.GetGenericTypeDefinition().FullName != "Cysharp.Threading.Tasks.UniTask`1" ||
                method.ReturnType.GetGenericArguments()[0] != typeof(Exception)) throw Unavailable();
            var loader = constructor.Invoke(new[] { config });
            var pending = method.Invoke(loader, new object[] { project, token }) ?? throw Unavailable();
            await DrainAsync(pending);
            token.ThrowIfCancellationRequested();
        }

        internal static Task DrainAsync(object pending)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            var getAwaiter = pending.GetType().GetMethod("GetAwaiter", flags, null, Type.EmptyTypes, null);
            var awaiter = getAwaiter?.Invoke(pending, null) ?? throw Unavailable();
            var type = awaiter.GetType();
            var completed = type.GetProperty("IsCompleted", flags);
            var result = type.GetMethod("GetResult", flags, null, Type.EmptyTypes, null);
            var continuation = type.GetMethod("OnCompleted", flags, null, new[] { typeof(Action) }, null);
            if (completed?.PropertyType != typeof(bool) || result?.ReturnType != typeof(Exception) ||
                continuation?.ReturnType != typeof(void)) throw Unavailable();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Finish()
            {
                try
                {
                    if (result.Invoke(awaiter, null) is Exception) throw Unavailable();
                    completion.TrySetResult(true);
                }
                catch (Exception) { completion.TrySetException(Unavailable()); }
            }
            if (completed.GetValue(awaiter) is true) Finish();
            else continuation.Invoke(awaiter, new object[] { (Action)Finish });
            return completion.Task;
        }
        private static MapProblem Unavailable() => new MapProblem(ModErrorCode.Unavailable,
            "The map's catalog assets could not be prepared. Update TopiaForge or try again.");
    }
}
