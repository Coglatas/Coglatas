using System.Text.Json;
using Coglatas.Application.Workspaces;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Workspaces;

public sealed class WorkspaceNeedsAttentionContractTests
{
    [Fact]
    public void ContractSerializesOnlyNormalizedActionableSummary()
    {
        var taskId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var occurredAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var attention = new WorkspaceNeedsAttentionItemResponse(
            Guid.NewGuid(),
            WorkspaceNeedsAttentionKind.ResearchFailed,
            $"/projects/{projectId:D}/tasks/{taskId:D}",
            occurredAt);
        var response = new WorkspaceDashboardListItemResponse(
            Guid.NewGuid(),
            "Workspace",
            null,
            null,
            WorkspaceStatus.Active,
            occurredAt,
            occurredAt,
            WorkspaceRole.Member,
            WorkspaceDashboardAccessSource.WorkspaceMembership,
            true,
            true,
            true,
            false,
            true,
            0,
            0,
            0,
            0,
            0)
        {
            NeedsAttentionCount = 1,
            NeedsAttentionItems = [attention]
        };

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            response,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var root = json.RootElement;
        Assert.Equal(1, root.GetProperty("needsAttentionCount").GetInt32());
        var item = Assert.Single(root.GetProperty("needsAttentionItems").EnumerateArray());
        Assert.Equal("ResearchFailed", item.GetProperty("kind").GetString());
        Assert.Equal(attention.TargetRoute, item.GetProperty("targetRoute").GetString());
        Assert.Equal(occurredAt, item.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.False(item.TryGetProperty("title", out _));
        Assert.False(item.TryGetProperty("body", out _));
        Assert.False(item.TryGetProperty("failureCode", out _));
        Assert.False(item.TryGetProperty("isRead", out _));
        Assert.False(item.TryGetProperty("processed", out _));
    }

    [Fact]
    public void ContractRepresentsResolvedStateAsZeroCurrentItems()
    {
        var now = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        var response = new WorkspaceDashboardListItemResponse(
            Guid.NewGuid(),
            "Workspace",
            null,
            null,
            WorkspaceStatus.Active,
            now,
            now,
            WorkspaceRole.Member,
            WorkspaceDashboardAccessSource.WorkspaceMembership,
            true,
            true,
            true,
            false,
            true,
            0,
            0,
            0,
            0,
            0)
        {
            NeedsAttentionCount = 0,
            NeedsAttentionItems = []
        };

        Assert.Equal(0, response.NeedsAttentionCount);
        Assert.Empty(response.NeedsAttentionItems!);
    }
}
