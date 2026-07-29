using System;
using TopiaForge.Mods;

namespace TopiaForge.RobotKit
{
    // Pure request composition kept separate from the extension lookup and transport so prompt limits,
    // field preservation, and idempotence can be tested without Unity or a live RoboAPI backend.
    internal static class BrainQueryDirectiveComposer
    {
        internal const int MaxPromptChars = 10_000;
        private const string Separator = "\n\n";

        public static BrainQueryRequest ApplyFromRegistry(
            BrainQueryRequest request,
            Func<IPromptOverrideRegistry?>? registryResolver,
            out bool exceededPromptLimit,
            out Exception? resolutionFailure)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            exceededPromptLimit = false;
            resolutionFailure = null;
            try
            {
                var registry = registryResolver?.Invoke();
                if (registry == null
                    || !registry.TryGetEffectiveOverride(
                        WellKnownPromptIds.GlobalRobotDirective,
                        out var promptOverride)
                    || promptOverride == null)
                {
                    return request;
                }

                return Apply(
                    request,
                    promptOverride.ReplacementText,
                    out exceededPromptLimit);
            }
            catch (Exception exception)
            {
                resolutionFailure = exception;
                return request;
            }
        }

        public static BrainQueryRequest Apply(
            BrainQueryRequest request,
            string? directive,
            out bool exceededPromptLimit)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            exceededPromptLimit = false;
            var normalized = (directive ?? string.Empty).Trim();
            if (normalized.Length == 0
                || request.Prompt.EndsWith(Separator + normalized, StringComparison.Ordinal))
            {
                return request;
            }

            if (normalized.Length > MaxPromptChars - Separator.Length
                || request.Prompt.Length > MaxPromptChars - Separator.Length - normalized.Length)
            {
                exceededPromptLimit = true;
                return request;
            }

            return new BrainQueryRequest(
                request.Prompt + Separator + normalized,
                request.Outputs,
                request.Usage,
                request.SuccessDescription,
                request.Temperature,
                request.UseReasoning);
        }
    }
}
