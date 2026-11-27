using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.GameBridge;

namespace TopiaForge.Worlds.Maps
{
    // Native map play prepares catalog lore before import. Keep that choice in one adapter;
    // cancellation must drain its awaiter before the owning scene can be discarded.
    internal static class MapLorePreparation
    {
        internal const string FromLaunchRequest = "Worlds: map lore uses the catalog address from the game's launch request.";
        internal const string FromAssetConfig = "Worlds: map lore uses the catalog address from the game's asset configuration.";
        private const BindingFlags PublicInstance = BindingFlags.Public | BindingFlags.Instance;
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;

        internal static Task PrepareAsync(object project, object config, IModLogger? logger, CancellationToken token)
            => PrepareAsync(project, config, logger, Type.GetType("UgcCatalogLoreLoader, GameCode", false),
                Type.GetType("UgcImportReport, GameCode", false), Type.GetType("UgcPlayLaunchRequest, GameCode", false), token);

        // Build 2640 gave the loader a second constructor argument, a catalog address with no default, and made
        // AddMissingLoreAsync take a caller-owned problem report and return a plain UniTask. The loader keeps a
        // single catalogBaseUrl field, which build 2597 filled from the asset configuration alone. The address is
        // taken from the game's own launch request when it carries one, otherwise from the asset configuration,
        // and the game's own validator must accept it. In a TopiaForge session the current launch request is usually
        // the empty bootstrap request TopiaForge queued itself, so the asset configuration supplies the address.
        // Unlike 2597's exception result, a plain UniTask may complete although some lore could not be fetched; such
        // problems reach only the report, whose count is logged.
        internal static async Task PrepareAsync(object project, object config, IModLogger? logger, Type? loaderType,
            Type? reportType, Type? launchRequestType, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var constructor = loaderType?.GetConstructor(new[] { config.GetType(), typeof(string) });
            var method = reportType == null ? null : loaderType?.GetMethod("AddMissingLoreAsync", PublicInstance, null,
                new[] { project.GetType(), reportType, typeof(CancellationToken) }, null);
            var validate = loaderType?.GetMethod("ValidateLinkCatalogUrl", PublicStatic, null, new[] { typeof(string) }, null);
            if (constructor == null || method == null || method.ReturnType.FullName != "Cysharp.Threading.Tasks.UniTask" ||
                validate?.ReturnType != typeof(string) || reportType!.GetConstructor(Type.EmptyTypes) == null) throw Unavailable();
            var (address, source) = CatalogAddress(config, launchRequestType, validate);
            Info(logger, source);
            object? report = null;
            try
            {
                report = Activator.CreateInstance(reportType);
                var loader = constructor.Invoke(new[] { config, address });
                var pending = method.Invoke(loader, new[] { project, report, token }) ?? throw Unavailable();
                await DrainAsync(pending, token);
            }
            catch (TargetInvocationException) { throw Unavailable(); }
            finally { UgcImportProblems.Warn(logger, "Worlds", "preparing map lore", report); }
            token.ThrowIfCancellationRequested();
        }

        private static (string Address, string Source) CatalogAddress(object config, Type? launchRequestType, MethodInfo validate)
        {
            foreach (var (candidate, source) in Candidates(config, launchRequestType))
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    if (validate.Invoke(null, new object[] { candidate! }) is string accepted && !string.IsNullOrWhiteSpace(accepted))
                        return (accepted, source);
                }
                catch (Exception) { /* The game refused this address; try the next source. */ }
            }
            throw new MapProblem(ModErrorCode.Unavailable,
                "The game did not supply a catalog address for this map. Update TopiaForge or try again.");
        }

        private static IEnumerable<(string? Candidate, string Source)> Candidates(object config, Type? launchRequestType)
        {
            yield return (Read(() =>
            {
                var current = launchRequestType?.GetProperty("Current", PublicStatic);
                if (current == null || current.PropertyType != launchRequestType || current.GetIndexParameters().Length != 0) return null;
                var request = current.GetValue(null);
                var url = launchRequestType!.GetProperty("CatalogUrl", PublicInstance);
                return request == null || url?.PropertyType != typeof(string) ? null : url.GetValue(request) as string;
            }), FromLaunchRequest);
            yield return (Read(() =>
            {
                var url = config.GetType().GetProperty("CatalogBaseUrl", PublicInstance);
                return url?.PropertyType != typeof(string) ? null : url.GetValue(config) as string;
            }), FromAssetConfig);
        }

        private static string? Read(Func<string?> read)
        {
            try { return read(); }
            catch (Exception) { return null; }
        }

        private static void Info(IModLogger? logger, string message)
        {
            try { logger?.Info(message); }
            catch (Exception) { /* A diagnostic cannot fail map loading. */ }
        }

        internal static Task DrainAsync(object pending, CancellationToken token = default)
        {
            var getAwaiter = pending.GetType().GetMethod("GetAwaiter", PublicInstance, null, Type.EmptyTypes, null);
            var awaiter = getAwaiter?.Invoke(pending, null) ?? throw Unavailable();
            var type = awaiter.GetType();
            var completed = type.GetProperty("IsCompleted", PublicInstance);
            var result = type.GetMethod("GetResult", PublicInstance, null, Type.EmptyTypes, null);
            var continuation = type.GetMethod("OnCompleted", PublicInstance, null, new[] { typeof(Action) }, null);
            if (completed?.PropertyType != typeof(bool) || result?.ReturnType != typeof(void) ||
                continuation?.ReturnType != typeof(void)) throw Unavailable();
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Finish()
            {
                try
                {
                    result.Invoke(awaiter, null);
                    completion.TrySetResult(true);
                }
                catch (TargetInvocationException error) when (error.InnerException is OperationCanceledException &&
                    token.IsCancellationRequested)
                {
                    completion.TrySetCanceled(token);
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
