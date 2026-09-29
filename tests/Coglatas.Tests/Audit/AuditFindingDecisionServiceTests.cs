using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Audit;

public sealed class AuditFindingDecisionServiceTests
{
    [Fact]
    public async Task NoDecisionDoesNotCompleteReviewAndServerDefinesRationalePolicy()
    {
        await using var fixture = await Fixture.CreateAsync();
        var finding = fixture.AddFinding();
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.GetAsync(finding.Id);

        Assert.True(result.IsSuccess, result.Error ?? result.ErrorDetail?.Message);
        Assert.False(result.Value!.ReviewCompleted);
        Assert.Null(result.Value.CurrentDecision);
        Assert.Empty(result.Value.History);
        Assert.True(result.Value.CanReview);
        var acceptedRisk = Assert.Single(result.Value.Options, option => option.Decision == "AcceptedRisk");
        Assert.True(acceptedRisk.RationaleRequired);
        Assert.Contains(result.Value.Options, option => option.Decision == "NoIssue" && !option.RationaleRequired);
        Assert.Contains(result.Value.Options, option => option.Decision == "NeedsFix" && !option.RationaleRequired);
    }

    [Fact]
    public async Task AcceptedRiskRequiresRationaleBeforeAnythingIsPersisted()
    {
        await using var fixture = await Fixture.CreateAsync();
        var finding = fixture.AddFinding();
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.SaveAsync(
            finding.Id,
            new SaveAuditFindingDecisionRequest("AcceptedRisk", "   "));

        Assert.False(result.IsSuccess);
        Assert.Equal("ReasonRequired", result.ErrorDetail?.Code);
        Assert.Empty(fixture.Context.Set<AuditFindingDecision>());
        Assert.Empty(fixture.Audit.Entries);
    }

    [Fact]
    public async Task DecisionChangesAppendReviewerRationaleTimestampAndPreviousState()
    {
        await using var fixture = await Fixture.CreateAsync();
        var finding = fixture.AddFinding();
        await fixture.Context.SaveChangesAsync();

        var first = await fixture.Service.SaveAsync(
            finding.Id,
            new SaveAuditFindingDecisionRequest("NoIssue"));
        var second = await fixture.Service.SaveAsync(
            finding.Id,
            new SaveAuditFindingDecisionRequest("NeedsFix", "Claim needs a corrected source."));

        Assert.True(first.IsSuccess, first.Error ?? first.ErrorDetail?.Message);
        Assert.True(second.IsSuccess, second.Error ?? second.ErrorDetail?.Message);
        Assert.True(second.Value!.ReviewCompleted);
        Assert.Equal("NeedsFix", second.Value.CurrentDecision!.Decision);
        Assert.Equal("NoIssue", second.Value.CurrentDecision.PreviousDecision);
        Assert.Equal("Claim needs a corrected source.", second.Value.CurrentDecision.Rationale);
        Assert.Equal(fixture.UserId, second.Value.CurrentDecision.ReviewerUserId);
        Assert.Equal("Audit Reviewer", second.Value.CurrentDecision.ReviewerDisplayName);
        Assert.NotEqual(default, second.Value.CurrentDecision.Timestamp);
        Assert.Equal(2, second.Value.History.Count);
        Assert.Equal("NeedsFix", second.Value.History[0].Decision);
        Assert.Equal("NoIssue", second.Value.History[1].Decision);
        Assert.Equal(2, fixture.Context.Set<AuditFindingDecision>().Count());

        Assert.Equal(2, fixture.Audit.Entries.Count);
        var serializedAudit = System.Text.Json.JsonSerializer.Serialize(fixture.Audit.Entries);
        Assert.DoesNotContain("Claim needs a corrected source.", serializedAudit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdenticalDecisionSaveIsIdempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var finding = fixture.AddFinding();
        await fixture.Context.SaveChangesAsync();

        var first = await fixture.Service.SaveAsync(
            finding.Id,
            new SaveAuditFindingDecisionRequest("NoIssue", "Reviewed against the cited source."));
        var second = await fixture.Service.SaveAsync(
            finding.Id,
            new SaveAuditFindingDecisionRequest("NoIssue", "Reviewed against the cited source."));

        Assert.True(first.IsSuccess, first.Error ?? first.ErrorDetail?.Message);
        Assert.True(second.IsSuccess, second.Error ?? second.ErrorDetail?.Message);
        Assert.Single(fixture.Context.Set<AuditFindingDecision>());
        Assert.Single(fixture.Audit.Entries);
    }

    [Fact]
    public async Task AuditReviewPermissionIsRequiredBeforeDecisionMutation()
    {
        await using var fixture = await Fixture.CreateAsync(canReview: false);
        var finding = fixture.AddFinding();
        await fixture.Context.SaveChangesAsync();

        var result = await fixture.Service.SaveAsync(
            finding.Id,
            new SaveAuditFindingDecisionRequest("NoIssue"));

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        Assert.Empty(fixture.Context.Set<AuditFindingDecision>());
        Assert.Empty(fixture.Audit.Entries);
        Assert.Equal(1, fixture.Authorization.AuthorizeCalls);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            Guid tenantId,
            Guid userId,
            Guid artifactVersionId,
            AppDbContext context,
            StubClaimsEvidenceService claims,
            StubAuditAuthorization authorization,
            StubAuditLogger audit,
            DbAuditFindingDecisionService service)
        {
            TenantId = tenantId;
            UserId = userId;
            ArtifactVersionId = artifactVersionId;
            Context = context;
            Claims = claims;
            Authorization = authorization;
            Audit = audit;
            Service = service;
        }

        public Guid TenantId { get; }
        public Guid UserId { get; }
        public Guid ArtifactVersionId { get; }
        public AppDbContext Context { get; }
        public StubClaimsEvidenceService Claims { get; }
        public StubAuditAuthorization Authorization { get; }
        public StubAuditLogger Audit { get; }
        public DbAuditFindingDecisionService Service { get; }

        public static async Task<Fixture> CreateAsync(bool canReview = true)
        {
            var tenantId = Guid.NewGuid();
            var userId = Guid.NewGuid();
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
            var audit = new StubAuditLogger();
            var service = new DbAuditFindingDecisionService(
                context,
                claims,
                authorization,
                new StubCurrentUser(userId),
                audit,
                new ContextUnitOfWork(context));
            return new Fixture(
                tenantId,
                userId,
                artifactVersionId,
                context,
                claims,
                authorization,
                audit,
                service);
        }

        public ArtifactFinding AddFinding()
        {
            var claim = new ArtifactClaim
            {
                TenantId = TenantId,
                ArtifactVersionId = ArtifactVersionId,
                Ordinal = 1,
                Text = "Claim under structured review",
                CitationPresent = true,
                SupportStatus = ArtifactClaimSupportStatus.Supported,
                ReviewStatus = ArtifactClaimReviewStatus.Unreviewed,
            };
            var finding = new ArtifactFinding
            {
                TenantId = TenantId,
                ArtifactClaimId = claim.Id,
                ArtifactClaim = claim,
                Severity = AuditFindingSeverity.High,
                ConfidencePercent = 90,
                DetectorKey = "detector.test",
                PolicyVersion = "policy-2026.09",
                Status = AuditFindingTriageStatus.Reviewing,
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
                Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"),
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
                capabilityKey == Application.Tenancy.CapabilityKeys.AuditReview && canReview
                    ? Result.Success()
                    : Result.Failure(new ApplicationErrorDetail("CapabilityDenied", "Audit operation denied.")));
        }
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
