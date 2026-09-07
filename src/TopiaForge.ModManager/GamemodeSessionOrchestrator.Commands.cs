using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    internal sealed partial class GamemodeSessionOrchestrator
    {
        internal Task<OperationResult<bool>> ExecuteCommandAsync(ProfileLaunchConfigurationV4 request, CancellationToken cancellationToken = default)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return dispatcher.InvokeCallbackAsync(async () =>
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var snapshot = environment.Capture();
                    var actual = snapshot.Profile.Packages.Select(package => package.Identity).OrderBy(package => package.Id, StringComparer.Ordinal)
                        .ThenBy(package => package.Version, StringComparer.Ordinal);
                    var expected = request.Packages.OrderBy(package => package.Id, StringComparer.Ordinal).ThenBy(package => package.Version, StringComparer.Ordinal);
                    if (snapshot.Profile.ProfileId != request.ProfileId || snapshot.Profile.Revision != request.ProfileRevision
                        || PackageSetDigest.Of(snapshot.Profile.Packages) != request.Digest || !actual.SequenceEqual(expected))
                        return await RejectCommandAsync(request.RequestId, request.Command, ModErrorCode.InvalidState,
                            "The runtime profile or exact package selection changed after launcher preflight.",
                            new[] { new LaunchBlock(LaunchBlockCode.PlanPackageSetMismatch, request.ProfileId) });
                    if (snapshot.PackageCleanupPending)
                        return await RejectCommandAsync(request.RequestId, request.Command, ModErrorCode.Conflict, "Package native cleanup is Busy.");
                    return request.Command == "main-menu"
                        ? await ReturnToMainMenuAsync(cancellationToken: cancellationToken, requestedId: request.RequestId)
                        : await StartAsync(request.Plan!, request.RequestId, cancellationToken);
                }
                catch (Exception error)
                {
                    return await RejectCommandAsync(request.RequestId, request.Command,
                        error is OperationCanceledException ? ModErrorCode.Cancelled : ModErrorCode.InvalidState, error.Message);
                }
            });
        }
        internal Task<OperationResult<bool>> RejectCommandAsync(string requestId, string command, ModErrorCode code, string message,
            IEnumerable<LaunchBlock>? blocks = null)
        {
            // Validate before scheduling so malformed correlation can never fault the host callback queue.
            _ = new LaunchOutcome("launch", requestId, 0, "idle", "failed", blocks ?? Array.Empty<LaunchBlock>(), command: command,
                error: new LaunchExecutionError(ErrorName(code), Message(message)));
            var completion = Completion();
            _ = dispatcher.InvokeAsync(() => CompleteCommand(completion, requestId, command, null,
                OperationResult<bool>.Failure(code, Message(message)), blocks));
            return completion.Task;
        }
    }
}
