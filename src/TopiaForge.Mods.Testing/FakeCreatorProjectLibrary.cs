using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic in-memory creator event-project library.</summary>
    public sealed class FakeCreatorProjectLibrary : ICreatorProjectLibrary
    {
        private readonly Dictionary<string, CreatorEventProject> projects =
            new Dictionary<string, CreatorEventProject>(StringComparer.OrdinalIgnoreCase);
        private long revision;

        /// <summary>Gets or sets an expected persistence failure.</summary>
        public ModErrorCode PersistenceErrorCode { get; set; }
        /// <summary>Gets or sets an optional validation callback.</summary>
        public Func<CreatorEventProject, CreatorProjectValidationResult>? Validator { get; set; }

        /// <inheritdoc />
        public CreatorProjectValidationResult Validate(CreatorEventProject project)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            return Validator?.Invoke(project) ?? new CreatorProjectValidationResult(Array.Empty<CreatorProjectValidationIssue>());
        }

        /// <inheritdoc />
        public Task<OperationResult<CreatorProjectLibrarySnapshot>> ListAsync(CancellationToken cancellationToken = default)
        {
            if (Cancelled(cancellationToken, out var failure))
            {
                return Task.FromResult(OperationResult<CreatorProjectLibrarySnapshot>.Failure(failure, "The fake project operation was rejected."));
            }
            var summaries = projects.Values
                .Select(ProjectSummary)
                .OrderBy(project => project.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(project => project.Id, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(OperationResult<CreatorProjectLibrarySnapshot>.Success(
                new CreatorProjectLibrarySnapshot(revision, summaries)));
        }

        /// <inheritdoc />
        public Task<OperationResult<CreatorEventProject>> LoadAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            if (Cancelled(cancellationToken, out var failure))
            {
                return Task.FromResult(OperationResult<CreatorEventProject>.Failure(failure, "The fake project operation was rejected."));
            }
            return Task.FromResult(projects.TryGetValue(projectId, out var project)
                ? OperationResult<CreatorEventProject>.Success(project)
                : OperationResult<CreatorEventProject>.Failure(ModErrorCode.NotFound, "The fake project does not exist."));
        }

        /// <inheritdoc />
        public Task<OperationResult<CreatorProjectSummary>> SaveAsync(
            CreatorEventProject project,
            CancellationToken cancellationToken = default)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            if (Cancelled(cancellationToken, out var failure))
            {
                return Task.FromResult(OperationResult<CreatorProjectSummary>.Failure(failure, "The fake project operation was rejected."));
            }
            var validation = Validate(project);
            var error = validation.Issues.FirstOrDefault(issue => issue.Severity == CreatorProjectValidationSeverity.Error);
            if (error != null)
            {
                return Task.FromResult(OperationResult<CreatorProjectSummary>.Failure(ModErrorCode.InvalidArgument, error.Message));
            }
            projects[project.Id] = project;
            revision++;
            return Task.FromResult(OperationResult<CreatorProjectSummary>.Success(ProjectSummary(project)));
        }

        /// <inheritdoc />
        public Task<OperationResult<bool>> DeleteAsync(
            string projectId,
            CancellationToken cancellationToken = default)
        {
            if (Cancelled(cancellationToken, out var failure))
            {
                return Task.FromResult(OperationResult<bool>.Failure(failure, "The fake project operation was rejected."));
            }
            var removed = projects.Remove(projectId);
            if (removed) revision++;
            return Task.FromResult(OperationResult<bool>.Success(removed));
        }

        private bool Cancelled(CancellationToken cancellationToken, out ModErrorCode failure)
        {
            failure = cancellationToken.IsCancellationRequested ? ModErrorCode.Cancelled : PersistenceErrorCode;
            return failure != ModErrorCode.None;
        }

        private static CreatorProjectSummary ProjectSummary(CreatorEventProject project) =>
            new CreatorProjectSummary(project.Id, project.DisplayName, project.Scope, project.ModifiedAtUtc);
    }
}
