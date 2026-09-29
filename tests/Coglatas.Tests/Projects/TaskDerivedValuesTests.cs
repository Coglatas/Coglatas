using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.Projects;

public sealed class TaskDerivedValuesTests
{
    [Fact]
    public void ParentWithoutChildrenUsesItsOwnValues()
    {
        var parent = Task("parent", progress: 17, start: new DateOnly(2026, 7, 1), end: new DateOnly(2026, 7, 2));

        var actual = ParentTaskDerivedValuesCalculator.Calculate(parent, [], Category);

        Assert.False(actual.IsDerived);
        Assert.Equal(17, actual.ProgressPercent);
        Assert.Equal(new DateOnly(2026, 7, 1), actual.PlannedStartDate);
        Assert.Equal(new DateOnly(2026, 7, 2), actual.PlannedEndDate);
    }

    [Fact]
    public void ParentUsesDatesAndWeightedProgressWhenEveryActiveChildHasAnEstimate()
    {
        var parent = Task("parent");
        var early = Task("early", parent.Id, progress: 20, start: new DateOnly(2026, 7, 2), end: new DateOnly(2026, 7, 4), effort: 30);
        var late = Task("late", parent.Id, progress: 80, start: new DateOnly(2026, 7, 3), end: new DateOnly(2026, 7, 8), effort: 90);

        var actual = ParentTaskDerivedValuesCalculator.Calculate(parent, [early, late], Category);

        Assert.True(actual.IsDerived);
        Assert.Equal(new DateOnly(2026, 7, 2), actual.PlannedStartDate);
        Assert.Equal(new DateOnly(2026, 7, 8), actual.PlannedEndDate);
        Assert.Equal(65, actual.ProgressPercent);
    }

    [Fact]
    public void MissingEstimateFallsBackToSimpleAverageAndCancelledDeletedChildrenAreExcluded()
    {
        var parent = Task("parent");
        var first = Task("first", parent.Id, progress: 20, effort: 10);
        var missing = Task("missing", parent.Id, progress: 80);
        var cancelled = Task("cancelled", parent.Id, progress: 100, effort: 100);
        cancelled.Status = TaskItemStatus.Cancelled;
        var deleted = Task("deleted", parent.Id, progress: 100, effort: 100);
        deleted.MarkDeleted(DateTimeOffset.UtcNow);

        var actual = ParentTaskDerivedValuesCalculator.Calculate(parent, [first, missing, cancelled, deleted], Category);

        Assert.True(actual.IsDerived);
        Assert.Equal(50, actual.ProgressPercent);
    }

    [Fact]
    public void AllCancelledChildrenRemainDerivedWithoutUsingSavedParentProgress()
    {
        var parent = Task("parent", progress: 37, start: new DateOnly(2026, 7, 1), end: new DateOnly(2026, 7, 2));
        var cancelled = Task("cancelled", parent.Id, progress: 100, start: new DateOnly(2026, 7, 4), end: new DateOnly(2026, 7, 8));
        cancelled.Status = TaskItemStatus.Cancelled;

        var actual = ParentTaskDerivedValuesCalculator.Calculate(parent, [cancelled], Category);

        Assert.True(actual.IsDerived);
        Assert.Equal(0, actual.ProgressPercent);
        Assert.Equal(new DateOnly(2026, 7, 4), actual.PlannedStartDate);
        Assert.Equal(new DateOnly(2026, 7, 8), actual.PlannedEndDate);
    }

    [Fact]
    public void NullDatesAndZeroEstimateUseSimpleAverageWithoutFabricatingDates()
    {
        var parent = Task("parent", progress: 4, start: new DateOnly(2026, 7, 1), end: new DateOnly(2026, 7, 2));
        var first = Task("first", parent.Id, progress: 10, effort: 0);
        var second = Task("second", parent.Id, progress: 21);

        var actual = ParentTaskDerivedValuesCalculator.Calculate(parent, [first, second], Category);

        Assert.True(actual.IsDerived);
        Assert.Null(actual.PlannedStartDate);
        Assert.Null(actual.PlannedEndDate);
        Assert.Equal(16, actual.ProgressPercent); // 15.5, away from zero
    }

    [Fact]
    public void DeletedChildrenDoNotDeriveAndCancelledChildrenDoNotContributeProgress()
    {
        var parent = Task("parent", progress: 37);
        var active = Task("active", parent.Id, progress: 20, effort: 10);
        var cancelled = Task("cancelled", parent.Id, progress: 100, effort: 10);
        cancelled.Status = TaskItemStatus.Cancelled;
        var deleted = Task("deleted", parent.Id, progress: 100, effort: 10);
        deleted.MarkDeleted(DateTimeOffset.UtcNow);

        var mixed = ParentTaskDerivedValuesCalculator.Calculate(parent, [active, cancelled, deleted], Category);
        var onlyDeleted = ParentTaskDerivedValuesCalculator.Calculate(parent, [deleted], Category);

        Assert.True(mixed.IsDerived);
        Assert.Equal(20, mixed.ProgressPercent);
        Assert.False(onlyDeleted.IsDerived);
        Assert.Equal(37, onlyDeleted.ProgressPercent);
    }

    [Fact]
    public void ReopenedCancelledChildIsIncludedInProgressAgain()
    {
        var parent = Task("parent");
        var active = Task("active", parent.Id, progress: 20, effort: 10);
        var cancelled = Task("cancelled", parent.Id, progress: 80, effort: 10);
        cancelled.Status = TaskItemStatus.Cancelled;

        var whileCancelled = ParentTaskDerivedValuesCalculator.Calculate(parent, [active, cancelled], Category);
        cancelled.Status = TaskItemStatus.InProgress;
        var reopened = ParentTaskDerivedValuesCalculator.Calculate(parent, [active, cancelled], Category);

        Assert.Equal(20, whileCancelled.ProgressPercent);
        Assert.Equal(50, reopened.ProgressPercent);
    }

    [Fact]
    public void EstimateWeightedProgressUsesLosslessEffortAndSpecifiedRounding()
    {
        var parent = Task("parent");
        var first = Task("first", parent.Id, progress: 0, effort: 1);
        var second = Task("second", parent.Id, progress: 100, effort: 2);

        var actual = ParentTaskDerivedValuesCalculator.Calculate(parent, [first, second], Category);

        Assert.Equal(67, actual.ProgressPercent); // 66.666..., midpoint policy is fixed by calculator
    }

    [Fact]
    public void PlannedEndIsOverdueOnlyAfterTheWorkspaceLocalDayEnds()
    {
        var task = Task("task", end: new DateOnly(2026, 7, 25));
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");

        Assert.False(TaskDeadlineCalculator.IsOverdue(task, TaskStageCategory.Todo, tokyo, new DateTimeOffset(2026, 7, 25, 14, 59, 0, TimeSpan.Zero)));
        Assert.True(TaskDeadlineCalculator.IsOverdue(task, TaskStageCategory.Todo, tokyo, new DateTimeOffset(2026, 7, 25, 15, 0, 0, TimeSpan.Zero)));
        Assert.False(TaskDeadlineCalculator.IsOverdue(task, TaskStageCategory.Done, tokyo, new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void OptionalStringPreservesOmissionAndExplicitNull()
    {
        var omitted = System.Text.Json.JsonSerializer.Deserialize<UpdateProjectTaskLabelRequest>("{\"name\":null,\"sortKey\":null,\"expectedVersion\":1}");
        var explicitlyCleared = System.Text.Json.JsonSerializer.Deserialize<UpdateProjectTaskLabelRequest>("{\"name\":null,\"description\":null,\"sortKey\":null,\"expectedVersion\":1}");

        Assert.NotNull(omitted);
        Assert.NotNull(explicitlyCleared);
        Assert.False(omitted.Description.IsSpecified);
        Assert.True(explicitlyCleared.Description.IsSpecified);
        Assert.Null(explicitlyCleared.Description.Value);
    }

    [Fact]
    public void LabelPatchWriterOmitsUnspecifiedMembersAndKeepsExplicitNull()
    {
        var patch = new UpdateProjectTaskLabelRequest(default, new OptionalString(true, null), default, 2);

        using var document = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(patch));

        Assert.False(document.RootElement.TryGetProperty("name", out _));
        Assert.True(document.RootElement.TryGetProperty("description", out var description));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, description.ValueKind);
        Assert.False(document.RootElement.TryGetProperty("sortKey", out _));
        Assert.Equal(2, document.RootElement.GetProperty("expectedVersion").GetInt64());
    }

    [Fact]
    public void LabelPatchRejectsMalformedScalarTypesDuringDeserialization()
    {
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<UpdateProjectTaskLabelRequest>("{\"expectedVersion\":\"one\"}"));
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Deserialize<UpdateProjectTaskLabelRequest>("{\"description\":true,\"expectedVersion\":1}"));
    }

    private static TaskItem Task(string title, Guid? parentId = null, int progress = 0, DateOnly? start = null, DateOnly? end = null, int? effort = null) => new()
    {
        Title = title, ParentTaskItemId = parentId, ProgressPercent = progress,
        PlannedStartDate = start, PlannedEndDate = end, EstimatedEffortMinutes = effort, Status = TaskItemStatus.NotStarted
    };

    private static TaskStageCategory Category(TaskItem task) => task.Status == TaskItemStatus.Cancelled ? TaskStageCategory.Cancelled : TaskStageCategory.Todo;
}
