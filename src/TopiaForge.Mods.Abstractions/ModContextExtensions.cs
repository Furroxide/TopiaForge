using System;
using System.Diagnostics.CodeAnalysis;

namespace TopiaForge.Mods
{
    /// <summary>Convenience helpers over dependency-scoped extension services.</summary>
    public static class ModContextExtensions
    {
        /// <summary>Resolves a required extension provider or throws a descriptive contract exception.</summary>
        /// <typeparam name="T">The dependency-owned extension contract.</typeparam>
        /// <param name="context">The current mod context.</param>
        /// <returns>The deterministic provider declared by a manifest dependency.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        /// <exception cref="InvalidOperationException">No declared dependency provides the contract.</exception>
        public static T RequireExtension<T>(this IModContext context) where T : class
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!context.Extensions.TryGet<T>(out var provider))
            {
                throw new InvalidOperationException(
                    "Required extension '" + typeof(T).FullName + "' is unavailable. " +
                    "Declare the provider as a manifest dependency and add its contract package.");
            }

            return provider!;
        }

        /// <summary>Tries to resolve an optional dependency-scoped extension provider.</summary>
        /// <typeparam name="T">The dependency-owned extension contract.</typeparam>
        /// <param name="context">The current mod context.</param>
        /// <param name="provider">Receives the deterministic provider when one is available.</param>
        /// <returns>True when a declared dependency provides the contract.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
        public static bool TryGetExtension<T>(
            this IModContext context,
            [NotNullWhen(true)] out T? provider) where T : class
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            return context.Extensions.TryGet(out provider);
        }
    }
}
