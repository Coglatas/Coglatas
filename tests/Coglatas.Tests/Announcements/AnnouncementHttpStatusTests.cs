using System.Text.Json;
using Coglatas.Application.Announcements;
using Coglatas.Application.Common;
using Coglatas.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Coglatas.Tests.Announcements;

public sealed class AnnouncementHttpStatusTests
{
    [Fact]
    public async Task Create_denied_selected_audience_returns_exact_forbidden_payload_without_calling_service()
    {
        var announcements = new CreateTrackingAnnouncementService();
        var controller = new AnnouncementsController(
            announcements,
            null!,
            new DeniedAudienceService(),
            null!);
        var request = new CreateAnnouncementRequest(
            Guid.NewGuid(),
            null,
            null,
            "Title",
            "Body");

        var result = Assert.IsType<ObjectResult>(await controller.Create(request, CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(
            "{\"error\":\"Announcement audience is not authorized.\"}",
            JsonSerializer.Serialize(result.Value));
        Assert.Equal(0, announcements.CreateCalls);
    }

    [Theory]
    [InlineData("Announcement not found.", StatusCodes.Status404NotFound)]
    [InlineData("Authentication is required.", StatusCodes.Status401Unauthorized)]
    [InlineData("This announcement does not require acknowledgement.", StatusCodes.Status400BadRequest)]
    public async Task Acknowledge_preserves_error_body_and_reports_failure_category(string error, int status)
    {
        var controller = new AnnouncementsController(null!, new AnalyticsStub(error), null!, null!);

        var result = Assert.IsType<ObjectResult>(await controller.Acknowledge(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(status, result.StatusCode);
        Assert.Equal(error, JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Analytics_denied_returns_forbidden()
    {
        const string error = "You are not allowed to view announcement analytics.";
        var controller = new AnnouncementsController(null!, new AnalyticsStub(error), null!, null!);

        var result = Assert.IsType<ObjectResult>(await controller.Analytics(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal(error, JsonSerializer.SerializeToElement(result.Value).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Acknowledge_success_preserves_existing_response()
    {
        var controller = new AnnouncementsController(null!, new AnalyticsStub(null), null!, null!);

        var result = Assert.IsType<OkObjectResult>(await controller.Acknowledge(Guid.NewGuid(), CancellationToken.None));

        Assert.Equal("OK", JsonSerializer.SerializeToElement(result.Value).GetProperty("status").GetString());
    }

    private sealed class DeniedAudienceService : IAnnouncementAudienceService
    {
        public Task<Result<IReadOnlyList<AnnouncementAudienceOptionResponse>>> ListAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(ListAsync));

        public Task<Result<bool>> IsAuthorizedAsync(
            Guid? workspaceId,
            Guid? groupId,
            Guid? channelId,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Result<bool>.Success(false));

        public Task<Result<bool>> IsAuthorizedForActorAsync(
            Guid actorUserId,
            Guid? workspaceId,
            Guid? groupId,
            Guid? channelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(IsAuthorizedForActorAsync));
    }

    private sealed class CreateTrackingAnnouncementService : IAnnouncementService
    {
        public int CreateCalls { get; private set; }

        public Task<Result<AnnouncementDetailResponse>> CreateAsync(
            CreateAnnouncementRequest request,
            CancellationToken cancellationToken = default)
        {
            CreateCalls++;
            throw new InvalidOperationException("CreateAsync must not be called when audience authorization is denied.");
        }

        public Task<Result<PagedResponse<AnnouncementListItemResponse>>> ListAsync(
            AnnouncementListQuery query,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(ListAsync));

        public Task<Result<AnnouncementDetailResponse>> GetAsync(Guid announcementId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(GetAsync));

        public Task<Result<AnnouncementDetailResponse>> UpdateAsync(
            Guid announcementId,
            UpdateAnnouncementRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(UpdateAsync));

        public Task<Result> DeleteAsync(Guid announcementId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(DeleteAsync));

        public Task<Result> MarkReadAsync(Guid announcementId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(MarkReadAsync));

        public Task<Result<AnnouncementReadStatusResponse>> GetReadStatusAsync(
            Guid announcementId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(GetReadStatusAsync));

        public Task<Result> ResendUnreadAsync(Guid announcementId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException(nameof(ResendUnreadAsync));
    }

    private sealed class AnalyticsStub(string? error) : IAnnouncementAnalyticsService
    {
        public Task<Result<AnnouncementAnalyticsResponse>> GetAsync(Guid announcementId, CancellationToken cancellationToken = default)
            => Task.FromResult(Result<AnnouncementAnalyticsResponse>.Failure(
                error ?? throw new InvalidOperationException("AnalyticsStub.GetAsync requires a configured failure.")));

        public Task<Result> AcknowledgeAsync(Guid announcementId, CancellationToken cancellationToken = default)
            => Task.FromResult(error is null ? Result.Success() : Result.Failure(error));

        public Task<Result> TrackCtaClickAsync(Guid announcementId, CancellationToken cancellationToken = default)
            => AcknowledgeAsync(announcementId, cancellationToken);
    }
}
