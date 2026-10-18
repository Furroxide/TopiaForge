using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Runs a <see cref="TopiaForgeMod"/> through the same context-first, cleanup-last lifecycle as the loader.</summary>
    public sealed class ModLifecycleRunner : IDisposable
    {
        private readonly TopiaForgeMod mod;
        private readonly List<Exception> cleanupFailures = new List<Exception>();
        private bool loaded;
        private bool finished;

        /// <summary>Creates a lifecycle runner for one mod instance and context.</summary>
        public ModLifecycleRunner(TopiaForgeMod mod, FakeModContext? context = null)
        {
            this.mod = mod ?? throw new ArgumentNullException(nameof(mod));
            Context = context ?? new FakeModContext();
        }

        /// <summary>Gets the fake SDK context attached to the mod.</summary>
        public FakeModContext Context { get; }

        /// <summary>Gets whether load completed and unload has not begun.</summary>
        public bool IsLoaded => loaded && !finished;

        /// <summary>Gets whether unload or failed-load cleanup completed.</summary>
        public bool IsFinished => finished;

        /// <summary>
        /// Gets the failures swallowed by <see cref="Dispose"/>, in the order they occurred.
        /// </summary>
        /// <remarks>
        /// <see cref="Dispose"/> never throws, because a <c>using</c> block disposes while an assertion failure is
        /// already unwinding the stack and a throwing <c>Dispose</c> would replace that failure with this one —
        /// hiding the defect the test just found. Assert on this list when a test needs to prove cleanup succeeded,
        /// or call <see cref="Unload"/> explicitly, which still throws.
        /// </remarks>
        public IReadOnlyList<Exception> CleanupFailures => cleanupFailures;

        /// <summary>Creates a runner using the public parameterless constructor of a mod type.</summary>
        public static ModLifecycleRunner Create<TMod>(FakeModContext? context = null)
            where TMod : TopiaForgeMod, new()
        {
            return new ModLifecycleRunner(new TMod(), context);
        }

        /// <summary>
        /// Attaches the context and invokes the mod's load callback. A failing callback triggers best-effort unload
        /// and lifetime cleanup before the exception is rethrown.
        /// </summary>
        /// <remarks>
        /// As TopiaForge does in the game, a mod's generated multiplayer contract is bound to the context's multiplayer
        /// session after the context is attached and before <c>OnLoad</c>, and released with the context's lifetime.
        /// Register the session with <see cref="FakeExtensionService.Register{T}"/> first; without one, the contract
        /// stays unbound. A contract that cannot bind fails the load with the reason.
        /// </remarks>
        public void Load()
        {
            if (loaded || finished)
            {
                throw new InvalidOperationException("This lifecycle runner cannot load again.");
            }

            try
            {
                mod.Load(Context, attached => BindGeneratedContract(mod, attached));
                loaded = true;
            }
            catch (Exception loadException)
            {
                var failures = new List<Exception> { loadException };
                TryCleanup(failures);
                finished = true;
                if (failures.Count == 1)
                {
                    throw;
                }

                throw new AggregateException("Mod load and cleanup both failed.", failures);
            }
        }

        /// <summary>Invokes unload and always releases the context lifetime in reverse registration order.</summary>
        public void Unload()
        {
            if (!loaded || finished)
            {
                throw new InvalidOperationException("The mod is not currently loaded.");
            }

            var failures = new List<Exception>();
            TryCleanup(failures);
            loaded = false;
            finished = true;
            if (failures.Count == 1)
            {
                throw failures[0];
            }

            if (failures.Count > 1)
            {
                throw new AggregateException("Mod unload and lifetime cleanup failed.", failures);
            }
        }

        /// <summary>Loads and unloads the mod, then asserts leak-free cleanup.</summary>
        public void RunToCompletion()
        {
            Load();
            Unload();
            Context.AssertNoLeaks();
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Never throws. Anything that fails during cleanup is recorded in <see cref="CleanupFailures"/> so a
        /// failing assertion inside the <c>using</c> block survives to the test report.
        /// </remarks>
        public void Dispose()
        {
            if (finished)
            {
                return;
            }

            if (loaded)
            {
                TryCleanup(cleanupFailures);
                loaded = false;
                finished = true;
                return;
            }

            try
            {
                Context.Dispose();
            }
            catch (Exception exception)
            {
                cleanupFailures.Add(exception);
            }

            finished = true;
        }

        private void TryCleanup(List<Exception> failures)
        {
            try
            {
                mod.Unload();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }

            try
            {
                Context.Dispose();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }

        /// <summary>
        /// Binds the mod's generated multiplayer contract to the context's multiplayer session, as TopiaForge binds it
        /// in the game: once the context is attached, so the contract's session hooks may read it, and before
        /// <c>OnLoad</c>, so <c>OnLoad</c> may submit. The binding ends with the context's lifetime.
        /// </summary>
        private static void BindGeneratedContract(TopiaForgeMod mod, IModContext context)
        {
            if (!(mod is IGeneratedMultiplayerBinding generated) ||
                !context.TryGetMultiplayer(out var session) ||
                session == null)
            {
                return;
            }

            var bound = generated.BindMultiplayer(session);
            if (!bound.TryGetValue(out var binding) || binding == null)
            {
                throw new InvalidOperationException(
                    "The mod's generated multiplayer contract did not bind (" + bound.ErrorCode + "): " +
                    bound.ErrorMessage);
            }

            context.Lifetime.Track(binding);
        }
    }
}
