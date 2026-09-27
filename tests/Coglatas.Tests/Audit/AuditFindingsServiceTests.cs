using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Tenancy;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Audit;

public sealed class AuditFindingsServiceTests
{
    [Fact]
    public async Task ListPrioritizesUnresolvedThenSeverityThenConfidence()
    {
        await using var fixture = await Fixture.CreateAsync();
        var resolvedCritical = fixture.AddFinding(AuditFindingTriageStatus.Resolved, AuditFindingSeverity.Critical, 100, 1);
        var openMedium = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.Medium, 95, 2);
        var openCritical = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.Critical, 40, 3);
        var reviewingHigh = fixture.AddFinding(AuditFindingTriageStatus.Reviewing, AuditFindingSeverity.High, 99, 4);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.ListAsync(new AuditFindingsQuery(fixture.ArtifactVersionId));

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Equal(
            new[] { openCritical.Id, reviewingHigh.Id, openMedium.Id, resolvedCritical.Id },
            result.Value!.Findings.Select(item => item.FindingId));
        Assert.Equal("Critical", result.Value.Findings[0].Severity);
        Assert.Equal(40, result.Value.Findings[0].ConfidencePercent);
        Assert.True(result.Value.CanReview);
        var owner = Assert.Single(result.Value.EligibleOwners);
        Assert.Equal(fixture.UserId, owner.UserId);
        Assert.Equal("Audit Reviewer", owner.DisplayName);
    }

    [Fact]
    public async Task ListProjectsOnlyAuthorizedEvidenceTraceAndOwnerDisplay()
    {
        await using var fixture = await Fixture.CreateAsync();
        var eventId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.High, 88, 1);
        finding.OwnerUserId = fixture.UserId;
        fixture.Claims.SetEvidence(finding.ArtifactClaimId, evidenceId, eventId);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.ListAsync(new AuditFindingsQuery(fixture.ArtifactVersionId));

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        var item = Assert.Single(result.Value!.Findings);
        Assert.Equal(evidenceId, item.RelatedEvidenceId);
        Assert.Equal(eventId, item.RelatedEventId);
        Assert.Equal("Audit Reviewer", item.OwnerDisplayName);
        Assert.Equal("detector.test", item.DetectorKey);
        Assert.Equal("policy-2026.09", item.PolicyVersion);
        Assert.Equal("Open", item.WorkflowStatus);
        Assert.Null(item.DueDate);
        Assert.False(item.IsOverdue);
    }

    [Fact]
    public async Task ListSupportsMyReviewsOverdueUnassignedAndWorkflowFilters()
    {
        await using var fixture = await Fixture.CreateAsync();
        var overdue = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.Medium, 70, 1);
        overdue.WorkflowStatus = AuditFindingWorkflowStatus.InReview;
        overdue.OwnerUserId = fixture.UserId;
        overdue.DueDate = new DateOnly(2026, 9, 1);

        var unassigned = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.High, 80, 2);
        unassigned.WorkflowStatus = AuditFindingWorkflowStatus.WaitingFix;

        var done = fixture.AddFinding(AuditFindingTriageStatus.Resolved, AuditFindingSeverity.Critical, 99, 3);
        done.WorkflowStatus = AuditFindingWorkflowStatus.Done;
        done.OwnerUserId = fixture.UserId;
        done.DueDate = new DateOnly(2026, 8, 30);
        await fixture.Context.SaveChangesAsync();

        var myReviews = await fixture.Service.ListAsync(new AuditFindingsQuery(
            fixture.ArtifactVersionId,
            MyReviews: true));
        Assert.True(myReviews.IsSuccess, myReviews.Error ?? myReviews.ErrorDetail?.Message);
        Assert.Equal(new[] { overdue.Id, done.Id }, myReviews.Value!.Findings.Select(item => item.FindingId));

        var overdueResult = await fixture.Service.ListAsync(new AuditFindingsQuery(
            fixture.ArtifactVersionId,
            Overdue: true));
        Assert.True(overdueResult.IsSuccess, overdueResult.Error ?? overdueResult.ErrorDetail?.Message);
        var overdueItem = Assert.Single(overdueResult.Value!.Findings);
        Assert.Equal(overdue.Id, overdueItem.FindingId);
        Assert.True(overdueItem.IsOverdue);

        var unassignedResult = await fixture.Service.ListAsync(new AuditFindingsQuery(
            fixture.ArtifactVersionId,
            Unassigned: true,
            WorkflowStatus: "WaitingFix"));
        Assert.True(unassignedResult.IsSuccess, unassignedResult.Error ?? unassignedResult.ErrorDetail?.Message);
        Assert.Equal(unassigned.Id, Assert.Single(unassignedResult.Value!.Findings).FindingId);
    }

    [Theory]
    [InlineData("AcceptedRisk")]
    [InlineData("FalsePositive")]
    public async Task RiskAcceptanceAndFalsePositiveRequireReason(string nextStatus)
    {
        await using var fixture = await Fixture.CreateAsync();
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.High, 80, 1);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.UpdateTriageAsync(
            finding.Id,
            new UpdateAuditFindingTriageRequest(nextStatus, null));

        Assert.False(result.IsSuccess);
        Assert.Equal("ReasonRequired", result.ErrorDetail?.Code);
        Assert.Equal(AuditFindingTriageStatus.Open, finding.Status);
        Assert.Empty(fixture.Context.Set<AuditFindingHistory>());
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task FalsePositiveAssignsSelectedOwnerAndAppendsHistoryWithoutCopyingReasonToAuditLog()
    {
        await using var fixture = await Fixture.CreateAsync();
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.Medium, 62, 1);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.UpdateTriageAsync(
            finding.Id,
            new UpdateAuditFindingTriageRequest(
                "FalsePositive",
                "Detector matched a quoted example.",
                fixture.UserId,
                AssignOwner: true));

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Equal(AuditFindingTriageStatus.FalsePositive, finding.Status);
        Assert.Equal(fixture.UserId, finding.OwnerUserId);
        Assert.Equal("Detector matched a quoted example.", finding.ResolutionReason);
        var history = Assert.Single(fixture.Context.Set<AuditFindingHistory>());
        Assert.Equal(AuditFindingTriageStatus.Open, history.FromStatus);
        Assert.Equal(AuditFindingTriageStatus.FalsePositive, history.ToStatus);
        Assert.Equal(fixture.UserId, history.ChangedByUserId);
        Assert.Equal(fixture.UserId, history.OwnerUserId);
        Assert.Equal("Detector matched a quoted example.", history.Reason);

        var workflowHistory = Assert.Single(fixture.Context.Set<AuditFindingWorkflowHistory>());
        Assert.Equal(AuditFindingWorkflowStatus.Open, workflowHistory.FromWorkflowStatus);
        Assert.Equal(AuditFindingWorkflowStatus.Open, workflowHistory.ToWorkflowStatus);
        Assert.Null(workflowHistory.FromOwnerUserId);
        Assert.Equal(fixture.UserId, workflowHistory.ToOwnerUserId);

        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal("AuditFindingTriageChanged", audit.Action);
        Assert.NotNull(audit.Metadata);
        Assert.False(audit.Metadata!.ContainsKey("reason"));
        Assert.DoesNotContain(
            "Detector matched a quoted example.",
            System.Text.Json.JsonSerializer.Serialize(audit),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditReviewMemberCanBeAssignedWithoutChangingTriageStatus()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ownerId = await fixture.AddTenantMemberAsync("Finding Owner", TenantUserStatus.Active);
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Reviewing, AuditFindingSeverity.High, 80, 1);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.UpdateTriageAsync(
            finding.Id,
            new UpdateAuditFindingTriageRequest(
                "Reviewing",
                OwnerUserId: ownerId,
                AssignOwner: true));

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Equal(ownerId, finding.OwnerUserId);
        var history = Assert.Single(fixture.Context.Set<AuditFindingHistory>());
        Assert.Equal(AuditFindingTriageStatus.Reviewing, history.FromStatus);
        Assert.Equal(AuditFindingTriageStatus.Reviewing, history.ToStatus);
        Assert.Equal(ownerId, history.OwnerUserId);
        Assert.Null(history.Reason);
    }

    [Fact]
    public async Task MemberWithoutAuditReviewGrantIsNotExposedOrAssignable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var unauthorizedOwnerId = await fixture.AddTenantMemberAsync(
            "Unauthorized member",
            TenantUserStatus.Active,
            grantReview: false);
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.Low, 20, 1);
        await fixture.Context.SaveChangesAsync();

        var list = await fixture.Service.ListAsync(new AuditFindingsQuery(fixture.ArtifactVersionId));
        Assert.True(list.IsSuccess, list.Error ?? list.ErrorDetail?.Message);
        Assert.DoesNotContain(list.Value!.EligibleOwners, owner => owner.UserId == unauthorizedOwnerId);

        var result = await fixture.Service.UpdateWorkflowAsync(
            finding.Id,
            new UpdateAuditFindingWorkflowRequest(
                "InReview",
                OwnerUserId: unauthorizedOwnerId,
                AssignOwner: true));

        Assert.False(result.IsSuccess);
        Assert.Equal("OwnerNotEligible", result.ErrorDetail?.Code);
        Assert.Null(finding.OwnerUserId);
        Assert.Equal(AuditFindingWorkflowStatus.Open, finding.WorkflowStatus);
        Assert.Empty(fixture.Context.Set<AuditFindingWorkflowHistory>());
    }

    [Fact]
    public async Task SuspendedTenantMemberCannotBeAssigned()
    {
        await using var fixture = await Fixture.CreateAsync();
        var suspendedOwnerId = await fixture.AddTenantMemberAsync("Suspended Owner", TenantUserStatus.Suspended);
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.Low, 20, 1);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.UpdateTriageAsync(
            finding.Id,
            new UpdateAuditFindingTriageRequest(
                "Open",
                OwnerUserId: suspendedOwnerId,
                AssignOwner: true));

        Assert.False(result.IsSuccess);
        Assert.Equal("OwnerNotEligible", result.ErrorDetail?.Code);
        Assert.Null(finding.OwnerUserId);
        Assert.Empty(fixture.Context.Set<AuditFindingHistory>());
        Assert.Empty(fixture.Context.Set<AuditFindingWorkflowHistory>());
    }

    [Fact]
    public async Task WorkflowMutationTracksOwnerDueAndStatusAndCreatesSafeAssignmentNotification()
    {
        await using var fixture = await Fixture.CreateAsync();
        var ownerId = await fixture.AddTenantMemberAsync("Assigned Reviewer", TenantUserStatus.Active);
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.High, 80, 1);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.UpdateWorkflowAsync(
            finding.Id,
            new UpdateAuditFindingWorkflowRequest(
                "InReview",
                OwnerUserId: ownerId,
                AssignOwner: true,
                DueDate: new DateOnly(2026, 9, 5),
                SetDueDate: true));

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Equal(AuditFindingTriageStatus.Open, finding.Status);
        Assert.Equal(AuditFindingWorkflowStatus.InReview, finding.WorkflowStatus);
        Assert.Equal(ownerId, finding.OwnerUserId);
        Assert.Equal(new DateOnly(2026, 9, 5), finding.DueDate);

        var history = Assert.Single(fixture.Context.Set<AuditFindingWorkflowHistory>());
        Assert.Equal(AuditFindingWorkflowStatus.Open, history.FromWorkflowStatus);
        Assert.Equal(AuditFindingWorkflowStatus.InReview, history.ToWorkflowStatus);
        Assert.Null(history.FromOwnerUserId);
        Assert.Equal(ownerId, history.ToOwnerUserId);
        Assert.Null(history.FromDueDate);
        Assert.Equal(new DateOnly(2026, 9, 5), history.ToDueDate);
        Assert.Equal(fixture.UserId, history.ChangedByUserId);

        var audit = Assert.Single(fixture.Audit.Entries);
        Assert.Equal("AuditFindingWorkflowChanged", audit.Action);
        Assert.DoesNotContain("Claim 1", System.Text.Json.JsonSerializer.Serialize(audit), StringComparison.Ordinal);

        var notification = Assert.Single(fixture.Notifications.Entries);
        Assert.Equal(ownerId, notification.UserId);
        Assert.Equal(NotificationType.System, notification.Type);
        Assert.Equal("Audit review assigned", notification.Title);
        Assert.Null(notification.Body);
        Assert.Equal("Artifact", notification.RelatedEntityType);
        Assert.Equal(StubClaimsEvidenceService.ArtifactId, notification.RelatedEntityId);
        Assert.DoesNotContain("Claim 1", notification.LogicalKey, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkflowMutationCanClearOwnerAndDueDateAndAppendsAnotherHistoryEntry()
    {
        await using var fixture = await Fixture.CreateAsync();
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.High, 80, 1);
        finding.WorkflowStatus = AuditFindingWorkflowStatus.WaitingFix;
        finding.OwnerUserId = fixture.UserId;
        finding.DueDate = new DateOnly(2026, 9, 4);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.UpdateWorkflowAsync(
            finding.Id,
            new UpdateAuditFindingWorkflowRequest(
                "ReadyForReReview",
                OwnerUserId: null,
                AssignOwner: true,
                DueDate: null,
                SetDueDate: true));

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.Null(finding.OwnerUserId);
        Assert.Null(finding.DueDate);
        Assert.Equal(AuditFindingWorkflowStatus.ReadyForReReview, finding.WorkflowStatus);
        var history = Assert.Single(fixture.Context.Set<AuditFindingWorkflowHistory>());
        Assert.Equal(fixture.UserId, history.FromOwnerUserId);
        Assert.Null(history.ToOwnerUserId);
        Assert.Equal(new DateOnly(2026, 9, 4), history.FromDueDate);
        Assert.Null(history.ToDueDate);
        Assert.Empty(fixture.Notifications.Entries);
    }

    [Fact]
    public async Task AuditReviewPermissionIsRequiredBeforeFindingMutation()
    {
        await using var fixture = await Fixture.CreateAsync(canReview: false);
        var finding = fixture.AddFinding(AuditFindingTriageStatus.Open, AuditFindingSeverity.Low, 25, 1);
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.UpdateTriageAsync(
            finding.Id,
            new UpdateAuditFindingTriageRequest("Reviewing"));

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        Assert.Equal(AuditFindingTriageStatus.Open, finding.Status);
        Assert.Empty(fixture.Context.Set<AuditFindingHistory>());
        Assert.Equal(1, fixture.Authorization.AuthorizeCalls);

        var workflow = await fixture.Service.UpdateWorkflowAsync(
            finding.Id,
            new UpdateAuditFindingWorkflowRequest("InReview"));
        Assert.False(workflow.IsSuccess);
        Assert.Equal("CapabilityDenied", workflow.ErrorDetail?.Code);
        Assert.Equal(AuditFindingWorkflowStatus.Open, finding.WorkflowStatus);
        Assert.Empty(fixture.Context.Set<AuditFindingWorkflowHistory>());
        Assert.Equal(2, fixture.Authorization.AuthorizeCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Tenant tenant;

        private Fixture(
            Guid tenantId,
            Guid userId,
            Guid artifactVersionId,
            Tenant tenant,
            AppDbContext context,
            StubClaimsEvidenceService claims,
            StubAuditAuthorization authorization,
            StubCapabilityGrantEvaluator capabilities,
            StubAuditLogger audit,
            StubNotificationService notifications,
            DbAuditFindingsService service)
        {
            TenantId = tenantId;
            UserId = userId;
            ArtifactVersionId = artifactVersionId;
            this.tenant = tenant;
            Context = context;
            Claims = claims;
            Authorization = authorization;
            Capabilities = capabilities;
            Audit = audit;
            Notifications = notifications;
            Service = service;
        }

        public Guid TenantId { get; }
        public Guid UserId { get; }
        public Guid ArtifactVersionId { get; }
        public AppDbContext Context { get; }
        public StubClaimsEvidenceService Claims { get; }
        public StubAuditAuthorization Authorization { get; }
        public StubCapabilityGrantEvaluator Capabilities { get; }
        public StubAuditLogger Audit { get; }
        public StubNotificationService Notifications { get; }
        public DbAuditFindingsService Service { get; }

        public static async Task<Fixture> CreateAsync(bool canReview = true)
        {
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var artifactVersionId = Guid.NewGuid();
            var currentTenant = new StubCurrentTenant(tenantId);
            var context = new AppDbContext(
                new DbContextOptionsBuilder<AppDbContext>()
                    .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                    .Options,
                currentTenant);

            var tenant = new Tenant(tenantId)
            {
                Name = "Audit tenant",
                Slug = "audit-tenant",
                DisplayName = "Audit tenant",
                Status = TenantStatus.Active,
            };
            context.Tenants.Add(tenant);
            await context.SaveChangesAsync();

            var user = new User
            {
                Id = userId,
                DisplayName = "Audit Reviewer",
                Email = "reviewer@example.invalid",
                NormalizedEmail = "REVIEWER@EXAMPLE.INVALID",
                PasswordHash = "test",
                Status = UserStatus.Active,
            };
            context.Users.Add(user);
            context.TenantUsers.Add(new TenantUser
            {
                TenantId = tenantId,
                UserId = userId,
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                JoinedAt = DateTimeOffset.UtcNow,
                Tenant = tenant,
                User = user,
            });
            await context.SaveChangesAsync();

            var claims = new StubClaimsEvidenceService(artifactVersionId);
            var authorization = new StubAuditAuthorization(canReview);
            var capabilities = new StubCapabilityGrantEvaluator();
            if (canReview)
            {
                capabilities.Grant(userId);
            }
            var audit = new StubAuditLogger();
            var notifications = new StubNotificationService();
            var service = new DbAuditFindingsService(
                context,
                claims,
                authorization,
                capabilities,
                new StubCurrentUser(userId),
                new StubClock(new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)),
                notifications,
                audit,
                new ContextUnitOfWork(context));
            return new Fixture(
                tenantId,
                userId,
                artifactVersionId,
                tenant,
                context,
                claims,
                authorization,
                capabilities,
                audit,
                notifications,
                service);
        }

        public async Task<Guid> AddTenantMemberAsync(
            string displayName,
            TenantUserStatus status,
            bool grantReview = true,
            TenantUserRole role = TenantUserRole.Member)
        {
            var userId = Guid.NewGuid();
            var user = new User
            {
                Id = userId,
                DisplayName = displayName,
                Email = $"{userId:N}@example.invalid",
                NormalizedEmail = $"{userId:N}@EXAMPLE.INVALID".ToUpperInvariant(),
                PasswordHash = "test",
                Status = UserStatus.Active,
            };
            Context.Users.Add(user);
            Context.TenantUsers.Add(new TenantUser
            {
                TenantId = TenantId,
                UserId = userId,
                Role = role,
                Status = status,
                JoinedAt = DateTimeOffset.UtcNow,
                Tenant = tenant,
                User = user,
            });
            await Context.SaveChangesAsync();
            if (grantReview && status == TenantUserStatus.Active)
            {
                Capabilities.Grant(userId);
            }
            return userId;
        }

        public ArtifactFinding AddFinding(
            AuditFindingTriageStatus status,
            AuditFindingSeverity severity,
            int confidence,
            int ordinal)
        {
            var claim = new ArtifactClaim
            {
                TenantId = TenantId,
                ArtifactVersionId = ArtifactVersionId,
                Ordinal = ordinal,
                Text = $"Claim {ordinal}",
                CitationPresent = true,
                SupportStatus = ArtifactClaimSupportStatus.Unverified,
                ReviewStatus = ArtifactClaimReviewStatus.Unreviewed,
            };
            var finding = new ArtifactFinding
            {
                TenantId = TenantId,
                ArtifactClaimId = claim.Id,
                ArtifactClaim = claim,
                Severity = severity,
                ConfidencePercent = confidence,
                DetectorKey = "detector.test",
                PolicyVersion = "policy-2026.09",
                Status = status,
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

        public void SetEvidence(Guid claimId, Guid evidenceId, Guid eventId)
        {
            var claim = claims[claimId];
            claims[claimId] = claim with
            {
                Evidence = new[]
                {
                    new AuditEvidenceResponse(
                        evidenceId,
                        1,
                        "WebSnapshot",
                        "https://example.invalid/source",
                        "Authorized source",
                        "Authorized passage",
                        "Section 1",
                        eventId)
                }
            };
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
        public string? Email => "reviewer@example.invalid";
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

    private sealed class StubClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
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

    private sealed class ContextUnitOfWork(AppDbContext context) : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            context.SaveChangesAsync(cancellationToken);
    }
}
