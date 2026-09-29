using Coglatas.Application.Common;

namespace Coglatas.Application.Projects;

/// <summary>
/// Fail-closed activation persistence used by Application-only/minimal hosts.
/// Full hosts replace these registrations from AddInfrastructure().
/// </summary>
public sealed class UnavailableProjectActivationWorkflowStore : IProjectActivationWorkflowStore
{
    public Task<Domain.Entities.TaskWorkflowDefinition?> GetDefinitionAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<Domain.Entities.TaskWorkflowDefinition?>(null);

    public Task<IReadOnlyList<Domain.Entities.TaskWorkflowStage>> ListStagesAsync(
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Domain.Entities.TaskWorkflowStage>>([]);

    public Task AddDefinitionAsync(
        Domain.Entities.TaskWorkflowDefinition definition,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task AddStageAsync(
        Domain.Entities.TaskWorkflowStage stage,
        CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

public sealed class UnavailableProjectActivationUnitOfWork : IProjectActivationUnitOfWork
{
    public Task<Result> ExecuteActivationAsync(
        Func<CancellationToken, Task<Result>> operation,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(new ApplicationErrorDetail(
            "DependencyUnavailable",
            "Project activation persistence is unavailable.")));
}
