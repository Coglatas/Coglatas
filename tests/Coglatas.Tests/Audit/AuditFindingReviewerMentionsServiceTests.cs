using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Audit;

public sealed class AuditFindingReviewerMentionsServiceTests
{
    [Fact]
    public async Task EligibleReviewerGetsReferenceOnlyNotification()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reviewerId = await fixture.AddMemberAsync("Mentioned reviewer", grantReview: true);
        var finding = fixture.AddFinding("Sensitive claim text must not enter the notification.");
        await fixture.Context.SaveChangesAsync();
        var requestId = Guid.NewGuid();

        var result = await fixture.Service.MentionAsync(
            finding.Id,
            new MentionAuditFindingReviewerRequest(reviewerId, requestId));

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);

        var notification = Assert.Single(fixture.Notifications.Entries);
        Assert.Equal(reviewerId, notification.UserId);
        Assert.Equal(NotificationType.Mention, notification.Type);
        Assert.Equal("Mentioned in Audit review", notification.Title);
        Assert.Null(notification.Body);
        Assert.Equal("Artifact", notification.RelatedEntityType);
        Assert.Equal(StubClaimsEvidenceService.ArtifactId, notification.RelatedEntityId);
        Assert.Contains(requestId.ToString("N"), notification.LogicalKey, StringComparison.Ordinal);
        Assert.DoesNotContain("Sensitive claim text", notification.LogicalKey, StringComparison.Ordinal);

        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal("AuditFindingReviewerMentioned", audit.Action);
        Assert.Equal(finding.Id, audit.EntityId);
        Assert.NotNull(audit.Metadata);
        Assert.Equal(reviewerId, audit.Metadata!["recipientUserId"]);
        Assert.Equal(requestId, audit.Metadata["requestId"]);
        Assert.DoesNotContain(
            "Sensitive claim text",
            System.Text.Json.JsonSerializer.Serialize(audit),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveMemberWithoutAuditReviewGrantCannotBeMentioned()
    {
        await using var fixture = await Fixture.CreateAsync();
        var reviewerId = await fixture.AddMemberAsync("Unauthorized member", grantReview: false);
        var finding = fixture.AddFinding("Authorized finding");
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.MentionAsync(
            finding.Id,
            new MentionAuditFindingReviewerRequest(reviewerId, Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("MentionTargetNotEligible", result.ErrorDetail?.Code);
        Assert.Empty(fixture.Notifications.Entries);
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task ActorWithoutAuditReviewPermissionCannotMention()
    {
        await using var fixture = await Fixture.CreateAsync(canReview: false);
        var reviewerId = await fixture.AddMemberAsync("Reviewer", grantReview: true);
        var finding = fixture.AddFinding("Authorized finding");
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.MentionAsync(
            finding.Id,
            new MentionAuditFindingReviewerRequest(reviewerId, Guid.NewGuid()));

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        Assert.Empty(fixture.Notifications.Entries);
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(1, fixture.Authorization.AuthorizeCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Tenant tenant;

        private Fixture(
            Guid tenantId,
            Guid actorUserId,
            Guid artifactVersionId,
            Tenant tenant,
            AppDbContext context,
            StubClaimsEvidenceService claims,
            StubAuditAuthorization authorization,
            StubCapabilityGrantEvaluator capabilities,
            StubNotificationService notifications,
            StubAuditLogger audit,
            DbAuditFindingReviewerMentionsService service)
        {
            TenantId = tenantId;
            ActorUserId = actorUserId;
            ArtifactVersionId = artifactVersionId;
            this.tenant = tenant;
            Context = context;
            Claims = claims;
            Authorization = authorization;
            Capabilities = capabilities;
            Notifications = notifications;
            Audit = audit;
            Service = service;
        }

        public Guid TenantId { get; }
        public Guid ActorUserId { get; }
        public Guid ArtifactVersionId { get; }
        public AppDbContext Context { get; }
        public StubClaimsEvidenceService Claims { get; }
        public StubAuditAuthorization Authorization { get; }
        public StubCapabilityGrantEvaluator Capabilities { get; }
        public StubNotificationService Notifications { get; }
        public StubAuditLogger Audit { get; }
        public DbAuditFindingReviewerMentionsService Service { get; }

        public static async Task<Fixture> CreateAsync(bool canReview = true)
        {
            var tenantId = Guid.NewGuid();
            var actorUserId = Guid.NewGuid();
            var artifactVersionId = Guid.NewGuid();
            var context = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options,
                new StubCurrentTenant(tenantId));

            var tenant = new Tenant(tenantId)
            {
                Name = "Audit tenant",
                Slug = "audit-tenant",
                DisplayName = "Audit tenant",
                Status = TenantStatus.Active,
            };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();

            var actor = new User
            {
                Id = actorUserId,
                DisplayName = "Actor reviewer",
                Email = "actor@example.invalid",
                NormalizedEmail = "ACTOR@EXAMPLE.INVALID",
                PasswordHash = "test",
                Status = UserStatus.Active,
            };
            context.Users.Add(actor);
            context.TenantUsers.Add(new TenantUser
            {
                TenantId = tenantId,
                UserId = actorUserId,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
                Tenant = tenant,
                User = actor,
            });
            await context.SaveChangesAsync();

            var claims = new StubClaimsEvidenceService(artifactVersionId);
            var authorization = new StubAuditAuthorization(canReview);
            var capabilities = new StubCapabilityGrantEvaluator();
            if (canReview)
            {
                capabilities.Grant(actorUserId);
            }
            var notifications = new StubNotificationService();
            var audit = new StubAuditLogger();
            var service = new DbAuditFindingReviewerMentionsService(
                context,
                claims,
                authorization,
                capabilities,
                new StubCurrentUser(actorUserId),
                notifications,
                audit,
                new ContextUnitOfWork(context));

            return new Fixture(
                tenantId,
                actorUserId,
                artifactVersionId,
                tenant,
                context,
                claims,
                authorization,
                capabilities,
                notifications,
                audit,
                service);
        }

        public async Task<Guid> AddMemberAsync(string displayName, bool grantReview)
        {
            var userId = Guid.NewGuid();
            var user = new User
            {
                Id = userId,
                DisplayName = displayName,
                Email = $"{userId:N}@example.invalid",
                NormalizedEmail = $"{userId:N}@example.invalid".ToUpperInvariant(),
                PasswordHash = "test",
                Status = UserStatus.Active,
            };
            Context.Users.Add(user);
            Context.TenantUsers.Add(new TenantUser
            {
                TenantId = TenantId,
                UserId = userId,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
                Tenant = tenant,
                User = user,
            });
            await Context.SaveChangesAsync();
            if (grantReview)
            {
                Capabilities.Grant(userId);
            }
            return userId;
        }

        public ArtifactFinding AddFinding(string claimText)
        {
            var claim = new ArtifactClaim
            {
                TenantId = TenantId,
                ArtifactVersionId = ArtifactVersionId,
                Ordinal = 1,
                Text = claimText,
                CitationPresent = true,
                SupportStatus = ArtifactClaimSupportStatus.Unverified,
                ReviewStatus = ArtifactClaimReviewStatus.Unreviewed,
            };
            var finding = new ArtifactFinding
            {
                TenantId = TenantId,
                ArtifactClaimId = claim.Id,
                ArtifactClaim = claim,
                Severity = AuditFindingSeverity.High,
                ConfidencePercent = 80,
                DetectorKey = "detector.test",
                PolicyVersion = "policy-2026.09",
                Status = AuditFindingTriageStatus.Open,
            };
            claim.Finding = finding;
            Context.Set<ArtifactClaim>().Add(claim);
            Context.Set<ArtifactFinding>().Add(finding);
            Claims.AddClaim(claim);
            return finding;
        }

        public ValueTask DisposeAsync() => Context.DisposeAsync();
    }

    private sealed class StubClaimsEvidenceService(Guid artifactVersionId) : IAuditClaimsEvidenceService
    {
        public static readonly Guid ArtifactId = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        private readonly Dictionary<Guid, AuditClaimEvidenceResponse> claims = new();

        public void AddClaim(ArtifactClaim claim)
        {
            claims[claim.Id] = new AuditClaimEvidenceResponse(
                claim.Id,
                claim.Ordinal,
                claim.Text,
                claim.CitationPresent,
                claim.SupportStatus.ToString(),
                claim.ReviewStatus.ToString(),
                Array.Empty<AuditEvidenceResponse>());
        }

        public Task<Result<AuditClaimsEvidenceResponse>> GetAsync(
            Guid requestedVersionId,
            CancellationToken cancellationToken = default)
        {
            if (requestedVersionId != artifactVersionId)
            {
                return Task.FromResult(Result<AuditClaimsEvidenceResponse>.Failure(
                    new ApplicationErrorDetail("ArtifactVersionNotFound", "The artifact version is not available.")));
            }

            return Task.FromResult(Result<AuditClaimsEvidenceResponse>.Success(new AuditClaimsEvidenceResponse(
                ArtifactId,
                artifactVersionId,
                1,
                "Audit report",
                claims.Values.OrderBy(claim => claim.Ordinal).ToArray())));
        }
    }

    private sealed class StubAuditAuthorization(bool canReview) : IAuditAuthorizationService
    {
        public int AuthorizeCalls { get; private set; }

        public Task<AuditCapabilityResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuditCapabilityResponse(true, canReview, false, false, false));

        public Task<Result> AuthorizeAsync(
            string capabilityKey,
            string operation,
            CancellationToken cancellationToken = default)
        {
            AuthorizeCalls++;
            return Task.FromResult(
                capabilityKey == CapabilityKeys.AuditReview && canReview
                    ? Result.Success()
                    : Result.Failure(new ApplicationErrorDetail("CapabilityDenied", "Audit operation denied.")));
        }
    }

    private sealed class StubCapabilityGrantEvaluator : ICapabilityGrantEvaluator
    {
        private readonly HashSet<Guid> grantedUsers = new();

        public void Grant(Guid userId) => grantedUsers.Add(userId);

        public Task<bool> HasActiveGrantAsync(
            Guid subjectUserId,
            Guid tenantId,
            string capabilityKey,
            CapabilityScopeType scopeType,
            Guid? scopeId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                grantedUsers.Contains(subjectUserId) &&
                (capabilityKey == CapabilityKeys.AuditView || capabilityKey == CapabilityKeys.AuditReview) &&
                scopeType == CapabilityScopeType.Tenant &&
                scopeId == tenantId);
    }

    private sealed class StubCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserId { get; } = userId;
        public Guid? SessionId => null;
        public string? Email => "actor@example.invalid";
        public SystemRole? SystemRole => Coglatas.Domain.Enums.SystemRole.User;
        public bool IsAuthenticated => true;
    }

    private sealed class StubCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId { get; } = tenantId;
        public bool IsAvailable => true;
        public string? TenantSlug => "audit-tenant";
        public bool IsPlatformScope => false;
    }

    private sealed class StubNotificationService : INotificationService
    {
        public List<NotificationRecord> Entries { get; } = new();

        public Task<Guid> CreateOrGetByLogicalKeyAsync(
            Guid userId,
            NotificationType type,
            string title,
            string? body,
            string? relatedEntityType,
            Guid? relatedEntityId,
            string logicalKey,
            CancellationToken cancellationToken = default)
        {
            var existing = Entries.FirstOrDefault(entry =>
                entry.UserId == userId && string.Equals(entry.LogicalKey, logicalKey, StringComparison.Ordinal));
            if (existing is not null)
            {
                return Task.FromResult(existing.Id);
            }

            var entry = new NotificationRecord(
                Guid.NewGuid(),
                userId,
                type,
                title,
                body,
                relatedEntityType,
                relatedEntityId,
                logicalKey);
            Entries.Add(entry);
            return Task.FromResult(entry.Id);
        }

        public Task NotifyAsync(
            Guid recipientUserId,
            string title,
            string? body,
            string sourceType,
            Guid sourceId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public sealed record NotificationRecord(
            Guid Id,
            Guid UserId,
            NotificationType Type,
            string Title,
            string? Body,
            string? RelatedEntityType,
            Guid? RelatedEntityId,
            string LogicalKey);
    }

    private sealed class StubAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = new();

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class ContextUnitOfWork(AppDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            context.SaveChangesAsync(cancellationToken);
    }
}
