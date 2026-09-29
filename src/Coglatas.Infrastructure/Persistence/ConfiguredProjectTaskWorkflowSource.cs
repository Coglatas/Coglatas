using System.Data;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Coglatas.Infrastructure.Persistence;

/// <summary>
/// Production WPC-DEC-033 configured-source adapter. The physical persistence is
/// normalized into Tenant-safe default/template tables so the existing EF model
/// snapshot does not need a second Project-workflow aggregate.
/// </summary>
public sealed class ConfiguredProjectTaskWorkflowSource(AppDbContext dbContext)
    : IConfiguredProjectTaskWorkflowSource
{
    public async Task<ProjectActivationTaskWorkflow?> FindWorkspaceTemplateAsync(
        Project project,
        CancellationToken cancellationToken = default)
    {
        if (project.TenantId == Guid.Empty || project.WorkspaceId == Guid.Empty)
        {
            return Invalid("WorkspaceConfigured", Guid.Empty);
        }

        var templateId = await FindDefaultTemplateIdAsync(
            """
            SELECT "TemplateId"
            FROM "workspace_task_workflow_defaults"
            WHERE "TenantId" = @tenantId AND "WorkspaceId" = @workspaceId
            """,
            project.TenantId,
            project.WorkspaceId,
            cancellationToken);

        return templateId.HasValue
            ? await LoadTemplateAsync(project.TenantId, templateId.Value, "WorkspaceConfigured", cancellationToken)
            : null;
    }

    public async Task<ProjectActivationTaskWorkflow?> FindTenantDefaultAsync(
        Project project,
        CancellationToken cancellationToken = default)
    {
        if (project.TenantId == Guid.Empty)
        {
            return Invalid("TenantDefault", Guid.Empty);
        }

        var templateId = await FindDefaultTemplateIdAsync(
            """
            SELECT "TemplateId"
            FROM "tenant_task_workflow_defaults"
            WHERE "TenantId" = @tenantId
            """,
            project.TenantId,
            workspaceId: null,
            cancellationToken);

        return templateId.HasValue
            ? await LoadTemplateAsync(project.TenantId, templateId.Value, "TenantDefault", cancellationToken)
            : null;
    }

    private async Task<Guid?> FindDefaultTemplateIdAsync(
        string sql,
        Guid tenantId,
        Guid? workspaceId,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            AttachCurrentTransaction(command);
            AddParameter(command, "tenantId", tenantId);
            if (workspaceId.HasValue)
            {
                AddParameter(command, "workspaceId", workspaceId.Value);
            }

            var value = await command.ExecuteScalarAsync(cancellationToken);
            return value switch
            {
                null or DBNull => null,
                Guid id => id,
                _ => Guid.TryParse(Convert.ToString(value), out var parsed) ? parsed : null
            };
        }
        finally
        {
            if (closeAfter)
            {
                await connection.CloseAsync();
            }
        }
    }

    private async Task<ProjectActivationTaskWorkflow> LoadTemplateAsync(
        Guid tenantId,
        Guid templateId,
        string sourceKind,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeAfter = connection.State != ConnectionState.Open;
        if (closeAfter)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            string? name;
            bool reviewEnforcementEnabled;
            long version;

            await using (var templateCommand = connection.CreateCommand())
            {
                templateCommand.CommandText = """
                    SELECT "Name", "ReviewEnforcementEnabled", "VersionNo"
                    FROM "task_workflow_templates"
                    WHERE "TenantId" = @tenantId AND "Id" = @templateId
                    """;
                AttachCurrentTransaction(templateCommand);
                AddParameter(templateCommand, "tenantId", tenantId);
                AddParameter(templateCommand, "templateId", templateId);

                await using var reader = await templateCommand.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken))
                {
                    return Invalid(sourceKind, templateId);
                }

                name = reader.GetString(0);
                reviewEnforcementEnabled = reader.GetBoolean(1);
                version = reader.GetInt64(2);
            }

            if (version <= 0 || string.IsNullOrWhiteSpace(name))
            {
                return Invalid(sourceKind, templateId);
            }

            var stages = new List<ProjectActivationTaskWorkflowStage>();
            await using (var stageCommand = connection.CreateCommand())
            {
                stageCommand.CommandText = """
                    SELECT "Name", "InternalCategory", "SortKey", "WipWarningLimit", "IsInitialStage", "IsTerminalStage"
                    FROM "task_workflow_template_stages"
                    WHERE "TenantId" = @tenantId AND "TemplateId" = @templateId
                    ORDER BY "SortKey", "Id"
                    """;
                AttachCurrentTransaction(stageCommand);
                AddParameter(stageCommand, "tenantId", tenantId);
                AddParameter(stageCommand, "templateId", templateId);

                await using var reader = await stageCommand.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    if (!Enum.TryParse<TaskStageCategory>(reader.GetString(1), ignoreCase: false, out var category) ||
                        !Enum.IsDefined(typeof(TaskStageCategory), category))
                    {
                        return Invalid(sourceKind, templateId);
                    }

                    stages.Add(new ProjectActivationTaskWorkflowStage(
                        reader.GetString(0),
                        category,
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetInt32(3),
                        reader.GetBoolean(4),
                        reader.GetBoolean(5)));
                }
            }

            return new ProjectActivationTaskWorkflow(
                $"TaskWorkflowTemplate/{templateId:D}/v{version}",
                name,
                reviewEnforcementEnabled,
                stages);
        }
        finally
        {
            if (closeAfter)
            {
                await connection.CloseAsync();
            }
        }
    }

    private void AttachCurrentTransaction(System.Data.Common.DbCommand command)
    {
        if (dbContext.Database.CurrentTransaction is { } transaction)
        {
            command.Transaction = transaction.GetDbTransaction();
        }
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static ProjectActivationTaskWorkflow Invalid(string sourceKind, Guid templateId) =>
        new(
            $"TaskWorkflowTemplate/{templateId:D}/invalid",
            sourceKind,
            ReviewEnforcementEnabled: true,
            Array.Empty<ProjectActivationTaskWorkflowStage>());
}
