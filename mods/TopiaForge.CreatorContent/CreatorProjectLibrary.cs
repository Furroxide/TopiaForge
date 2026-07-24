using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed class CreatorProjectLibrary : ICreatorProjectLibrary, IDisposable
    {
        private const string IndexPath = "event-projects/index.v1.json";
        private const int MaximumProjectBytes = 2 * 1024 * 1024;
        private const int MaximumProjects = 256;
        private readonly IModFiles files;
        private readonly CreatorProjectValidator validator;
        private readonly IModLogger logger;
        private readonly SemaphoreSlim sync = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, CreatorProjectSummary> projects =
            new Dictionary<string, CreatorProjectSummary>(StringComparer.OrdinalIgnoreCase);
        private bool indexLoaded;
        private bool disposed;
        private long revision;

        public CreatorProjectLibrary(IModFiles files, CreatorProjectValidator validator, IModLogger logger)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public CreatorProjectValidationResult Validate(CreatorEventProject project) => validator.Validate(project);

        public async Task<OperationResult<CreatorProjectLibrarySnapshot>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            var entered = await EnterAsync(cancellationToken).ConfigureAwait(false);
            if (!entered.Succeeded)
            {
                return OperationResult<CreatorProjectLibrarySnapshot>.Failure(entered.ErrorCode, entered.ErrorMessage);
            }
            try
            {
                var loaded = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
                if (!loaded.Succeeded)
                {
                    return OperationResult<CreatorProjectLibrarySnapshot>.Failure(loaded.ErrorCode, loaded.ErrorMessage);
                }
                return OperationResult<CreatorProjectLibrarySnapshot>.Success(CreateSnapshot());
            }
            finally
            {
                sync.Release();
            }
        }

        public async Task<OperationResult<CreatorEventProject>> LoadAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            if (!CreatorIds.IsLocalId(projectId, 64))
            {
                return OperationResult<CreatorEventProject>.Failure(ModErrorCode.InvalidArgument, "Project id is not portable.");
            }
            var entered = await EnterAsync(cancellationToken).ConfigureAwait(false);
            if (!entered.Succeeded)
            {
                return OperationResult<CreatorEventProject>.Failure(entered.ErrorCode, entered.ErrorMessage);
            }
            try
            {
                var loaded = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
                if (!loaded.Succeeded)
                {
                    return OperationResult<CreatorEventProject>.Failure(loaded.ErrorCode, loaded.ErrorMessage);
                }
                if (!projects.ContainsKey(projectId))
                {
                    return OperationResult<CreatorEventProject>.Failure(ModErrorCode.NotFound, "The project is not in the local library index.");
                }

                var read = await files.ReadDataTextAsync(ProjectPath(projectId), cancellationToken).ConfigureAwait(false);
                if (!read.TryGetValue(out var json))
                {
                    return OperationResult<CreatorEventProject>.Failure(read.ErrorCode, read.ErrorMessage);
                }
                if (Encoding.UTF8.GetByteCount(json) > MaximumProjectBytes)
                {
                    return OperationResult<CreatorEventProject>.Failure(ModErrorCode.InvalidArgument, "The stored project exceeds 2 MiB.");
                }

                try
                {
                    var project = CreatorProjectCodec.DecodeProject(json);
                    if (!string.Equals(project.Id, projectId, StringComparison.OrdinalIgnoreCase))
                    {
                        return OperationResult<CreatorEventProject>.Failure(ModErrorCode.InvalidState, "The project file id does not match its index id.");
                    }
                    var validation = validator.Validate(project);
                    var error = validation.Issues.FirstOrDefault(issue => issue.Severity == CreatorProjectValidationSeverity.Error);
                    return error == null
                        ? OperationResult<CreatorEventProject>.Success(project)
                        : OperationResult<CreatorEventProject>.Failure(ModErrorCode.InvalidState, "Stored project is invalid: " + error.Message);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "Failed to decode creator project '" + projectId + "'.");
                    return OperationResult<CreatorEventProject>.Failure(ModErrorCode.External, "The stored project document is malformed.");
                }
            }
            finally
            {
                sync.Release();
            }
        }

        public async Task<OperationResult<CreatorProjectSummary>> SaveAsync(
            CreatorEventProject project,
            CancellationToken cancellationToken = default)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            var validation = validator.Validate(project);
            var error = validation.Issues.FirstOrDefault(issue => issue.Severity == CreatorProjectValidationSeverity.Error);
            if (error != null)
            {
                return OperationResult<CreatorProjectSummary>.Failure(ModErrorCode.InvalidArgument, error.Message);
            }

            string json;
            try
            {
                json = CreatorProjectCodec.EncodeProject(project);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Failed to encode creator project '" + project.Id + "'.");
                return OperationResult<CreatorProjectSummary>.Failure(ModErrorCode.External, "The project could not be encoded.");
            }
            if (Encoding.UTF8.GetByteCount(json) > MaximumProjectBytes)
            {
                return OperationResult<CreatorProjectSummary>.Failure(ModErrorCode.InvalidArgument, "The project exceeds 2 MiB.");
            }

            var entered = await EnterAsync(cancellationToken).ConfigureAwait(false);
            if (!entered.Succeeded)
            {
                return OperationResult<CreatorProjectSummary>.Failure(entered.ErrorCode, entered.ErrorMessage);
            }
            try
            {
                var loaded = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
                if (!loaded.Succeeded)
                {
                    return OperationResult<CreatorProjectSummary>.Failure(loaded.ErrorCode, loaded.ErrorMessage);
                }
                var hadPrior = projects.TryGetValue(project.Id, out var prior);
                if (!hadPrior && projects.Count >= MaximumProjects)
                {
                    return OperationResult<CreatorProjectSummary>.Failure(
                        ModErrorCode.RateLimited,
                        "The creator project library reached its 256-project limit.");
                }
                var path = ProjectPath(project.Id);
                string? priorDocument = null;
                if (hadPrior)
                {
                    var priorRead = await files.ReadDataTextAsync(path, cancellationToken).ConfigureAwait(false);
                    if (!priorRead.TryGetValue(out priorDocument))
                    {
                        return OperationResult<CreatorProjectSummary>.Failure(priorRead.ErrorCode, priorRead.ErrorMessage);
                    }
                }

                var write = await files.WriteDataTextAsync(path, json, cancellationToken).ConfigureAwait(false);
                if (!write.Succeeded)
                {
                    return OperationResult<CreatorProjectSummary>.Failure(write.ErrorCode, write.ErrorMessage);
                }

                var summary = new CreatorProjectSummary(project.Id, project.DisplayName, project.Scope, project.ModifiedAtUtc);
                projects[project.Id] = summary;
                var indexWrite = await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
                if (!indexWrite.Succeeded)
                {
                    if (hadPrior && prior != null)
                    {
                        projects[project.Id] = prior;
                    }
                    else
                    {
                        projects.Remove(project.Id);
                    }
                    var restored = hadPrior
                        ? await files.WriteDataTextAsync(path, priorDocument!, CancellationToken.None).ConfigureAwait(false)
                        : await files.DeleteDataFileAsync(path, CancellationToken.None).ConfigureAwait(false);
                    if (!restored.Succeeded)
                    {
                        logger.Error(
                            new InvalidOperationException(restored.ErrorMessage),
                            "Creator project document rollback failed after its index update was rejected.");
                        return OperationResult<CreatorProjectSummary>.Failure(
                            ModErrorCode.External,
                            "The project index update failed and the previous project document could not be restored.");
                    }
                    return OperationResult<CreatorProjectSummary>.Failure(indexWrite.ErrorCode, indexWrite.ErrorMessage);
                }
                revision++;
                return OperationResult<CreatorProjectSummary>.Success(summary);
            }
            finally
            {
                sync.Release();
            }
        }

        public async Task<OperationResult<bool>> DeleteAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            if (!CreatorIds.IsLocalId(projectId, 64))
            {
                return OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "Project id is not portable.");
            }
            var entered = await EnterAsync(cancellationToken).ConfigureAwait(false);
            if (!entered.Succeeded) return entered;
            try
            {
                var loaded = await EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
                if (!loaded.Succeeded) return loaded;
                if (!projects.TryGetValue(projectId, out var prior)) return OperationResult<bool>.Success(false);

                projects.Remove(projectId);
                var indexWrite = await WriteIndexAsync(cancellationToken).ConfigureAwait(false);
                if (!indexWrite.Succeeded)
                {
                    projects[projectId] = prior;
                    return indexWrite;
                }
                revision++;
                var deleted = await files.DeleteDataFileAsync(ProjectPath(projectId), cancellationToken).ConfigureAwait(false);
                return deleted.Succeeded
                    ? OperationResult<bool>.Success(true)
                    : OperationResult<bool>.Failure(deleted.ErrorCode, deleted.ErrorMessage);
            }
            finally
            {
                sync.Release();
            }
        }

        public void Dispose()
        {
            disposed = true;
        }

        private async Task<OperationResult<bool>> EnsureIndexAsync(CancellationToken cancellationToken)
        {
            if (indexLoaded) return OperationResult<bool>.Success(true);
            if (!files.DataFileExists(IndexPath))
            {
                indexLoaded = true;
                return OperationResult<bool>.Success(true);
            }
            var read = await files.ReadDataTextAsync(IndexPath, cancellationToken).ConfigureAwait(false);
            if (!read.TryGetValue(out var json)) return OperationResult<bool>.Failure(read.ErrorCode, read.ErrorMessage);
            try
            {
                projects.Clear();
                var decoded = CreatorProjectCodec.DecodeIndex(json);
                if (decoded.Count > MaximumProjects)
                {
                    throw new InvalidOperationException("The creator project index exceeds the 256-project limit.");
                }
                foreach (var summary in decoded)
                {
                    if (!CreatorIds.IsLocalId(summary.Id, 64) || projects.ContainsKey(summary.Id))
                    {
                        throw new InvalidOperationException("The creator project index contains invalid or duplicate ids.");
                    }
                    projects.Add(summary.Id, summary);
                }
                indexLoaded = true;
                return OperationResult<bool>.Success(true);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Failed to decode the creator project index.");
                return OperationResult<bool>.Failure(ModErrorCode.External, "The local creator project index is malformed.");
            }
        }

        private async Task<OperationResult<bool>> WriteIndexAsync(CancellationToken cancellationToken)
        {
            try
            {
                var json = CreatorProjectCodec.EncodeIndex(OrderedProjects());
                return await files.WriteDataTextAsync(IndexPath, json, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Failed to encode the creator project index.");
                return OperationResult<bool>.Failure(ModErrorCode.External, "The creator project index could not be encoded.");
            }
        }

        private CreatorProjectLibrarySnapshot CreateSnapshot() => new CreatorProjectLibrarySnapshot(revision, OrderedProjects());

        private CreatorProjectSummary[] OrderedProjects() => projects.Values
            .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        private async Task<OperationResult<bool>> EnterAsync(CancellationToken cancellationToken)
        {
            if (disposed) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The creator project library is disposed.");
            try
            {
                await sync.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (disposed)
                {
                    sync.Release();
                    return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "The creator project library is disposed.");
                }
                return OperationResult<bool>.Success(true);
            }
            catch (OperationCanceledException)
            {
                return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The creator project operation was cancelled.");
            }
        }

        private static string ProjectPath(string projectId) => "event-projects/" + projectId.ToLowerInvariant() + ".v1.json";
    }
}
