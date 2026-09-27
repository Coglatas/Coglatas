using System.Text.Json;
using Coglatas.Application.Audit;
using Coglatas.Application.Auth;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Tenancy;
using Coglatas.Application.TenantAdministration;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Files;
using Coglatas.Infrastructure.Persistence;
using Coglatas.Web.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Coglatas.Tests.Tenancy;

public sealed class TenantIsolationSecurityTests
{
    [Fact]
    public async Task TenantContextsReturnOnlyTheirOwnResourceGraph()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();

        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        await AssertOnlyTenantAsync(dbContext, data.TenantA.Id);
        Assert.Equal(data.WorkspaceA.Id, (await dbContext.Workspaces.SingleAsync()).Id);
        Assert.Equal(data.ProjectA.Id, (await dbContext.Projects.SingleAsync()).Id);
        Assert.Equal(data.TaskA.Id, (await dbContext.TaskItems.SingleAsync()).Id);
        Assert.Equal(data.FileA.Id, (await dbContext.FileObjects.SingleAsync()).Id);
        Assert.Equal(data.ConversationA.Id, (await dbContext.Conversations.SingleAsync()).Id);
        Assert.Equal(data.AnnouncementA.Id, (await dbContext.Announcements.SingleAsync()).Id);

        currentTenant.SetTenant(data.TenantB.Id, data.TenantB.Slug);
        await AssertOnlyTenantAsync(dbContext, data.TenantB.Id);
        Assert.Equal(data.WorkspaceB.Id, (await dbContext.Workspaces.SingleAsync()).Id);
        Assert.Equal(data.ProjectB.Id, (await dbContext.Projects.SingleAsync()).Id);
        Assert.Equal(data.TaskB.Id, (await dbContext.TaskItems.SingleAsync()).Id);
        Assert.Equal(data.FileB.Id, (await dbContext.FileObjects.SingleAsync()).Id);
        Assert.Equal(data.ConversationB.Id, (await dbContext.Conversations.SingleAsync()).Id);
        Assert.Equal(data.AnnouncementB.Id, (await dbContext.Announcements.SingleAsync()).Id);
    }

    [Fact]
    public async Task TenantAContextCannotQueryTenantBRecordsById()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();

        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);

        Assert.Null(await dbContext.Workspaces.FirstOrDefaultAsync(workspace => workspace.Id == data.WorkspaceB.Id));
        Assert.Null(await dbContext.Groups.FirstOrDefaultAsync(group => group.Id == data.GroupB.Id));
        Assert.Null(await dbContext.Projects.FirstOrDefaultAsync(project => project.Id == data.ProjectB.Id));
        Assert.Null(await dbContext.TaskItems.FirstOrDefaultAsync(task => task.Id == data.TaskB.Id));
        Assert.Null(await dbContext.FileObjects.FirstOrDefaultAsync(file => file.Id == data.FileB.Id));
        Assert.Null(await dbContext.Conversations.FirstOrDefaultAsync(conversation => conversation.Id == data.ConversationB.Id));
        Assert.Null(await dbContext.Announcements.FirstOrDefaultAsync(announcement => announcement.Id == data.AnnouncementB.Id));
    }

    [Fact]
    public async Task NormalTenantScopeExcludesOtherTenantNotificationsAuditAndSecurityEvents()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();

        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);

        Assert.All(await dbContext.Notifications.ToListAsync(), notification => Assert.Equal(data.TenantA.Id, notification.TenantId));
        Assert.All(await dbContext.AuditLogs.ToListAsync(), log => Assert.Equal(data.TenantA.Id, log.TenantId));
        Assert.All(await dbContext.SecurityEvents.ToListAsync(), item => Assert.Equal(data.TenantA.Id, item.TenantId));
    }

    [Fact]
    public async Task CreatingTenantEntityStampsCurrentTenantAndMismatchedUpdateIsRejected()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);

        var workspace = new Workspace
        {
            Name = "Stamped",
            Slug = "stamped",
            CreatedByUserId = data.TenantAOwner.Id
        };

        dbContext.Workspaces.Add(workspace);
        await dbContext.SaveChangesAsync();
        Assert.Equal(data.TenantA.Id, workspace.TenantId);

        workspace.TenantId = data.TenantB.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task PlatformScopeMustSetTenantIdExplicitlyForTenantOwnedData()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetPlatformScope();

        dbContext.Workspaces.Add(new Workspace
        {
            Name = "Missing tenant",
            Slug = "missing-tenant",
            CreatedByUserId = data.PlatformAdmin.Id
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
    }

    [Fact]
    public async Task SuspendedTenantContextCannotSaveTenantOwnedWritesAfterResolution()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();

        currentTenant.SetPlatformScope();
        data.TenantA.Status = TenantStatus.Suspended;
        await dbContext.SaveChangesAsync();

        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        dbContext.Workspaces.Add(new Workspace
        {
            Name = "Blocked",
            Slug = "blocked",
            CreatedByUserId = data.TenantAOwner.Id
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => dbContext.SaveChangesAsync());
        Assert.Contains("not active", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ActiveTenantContextCanStillSaveTenantOwnedWrites()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();

        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var workspace = new Workspace
        {
            Name = "Allowed",
            Slug = "allowed",
            CreatedByUserId = data.TenantAOwner.Id
        };
        dbContext.Workspaces.Add(workspace);

        await dbContext.SaveChangesAsync();

        Assert.Equal(data.TenantA.Id, workspace.TenantId);
    }

    [Fact]
    public async Task TenantSwitchingRequiresActiveMembershipAndEnabledMode()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();

        var crossTenantService = CreateTenantService(dbContext, currentTenant, data.CrossTenantUser, new TenancyOptions { AllowTenantSwitching = true });
        Assert.True((await crossTenantService.SwitchTenantAsync(data.TenantA.Id)).IsSuccess);
        Assert.True((await crossTenantService.SwitchTenantAsync(data.TenantB.Id)).IsSuccess);

        var tenantAMemberService = CreateTenantService(dbContext, currentTenant, data.TenantAMember, new TenancyOptions { AllowTenantSwitching = true });
        var tenantAToB = await tenantAMemberService.SwitchTenantAsync(data.TenantB.Id);
        Assert.False(tenantAToB.IsSuccess);
        Assert.Equal("Tenant membership is required.", tenantAToB.Error);

        var outsiderService = CreateTenantService(dbContext, currentTenant, data.Outsider, new TenancyOptions { AllowTenantSwitching = true });
        var outsiderToA = await outsiderService.SwitchTenantAsync(data.TenantA.Id);
        Assert.False(outsiderToA.IsSuccess);
        Assert.Equal("Tenant membership is required.", outsiderToA.Error);

        var onPremService = CreateTenantService(dbContext, currentTenant, data.CrossTenantUser, new TenancyOptions { AppMode = AppMode.OnPremSingleTenant, AllowTenantSwitching = true });
        Assert.Equal("Tenant switching is disabled.", (await onPremService.SwitchTenantAsync(data.TenantA.Id)).Error);
    }

    [Fact]
    public async Task SuspendedTenantCannotBeResolvedOrSwitchedInto()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        var httpContextAccessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        httpContextAccessor.HttpContext.Request.Headers["X-Tenant-Slug"] = data.SuspendedTenant.Slug;
        var resolver = new HttpTenantResolver(
            httpContextAccessor,
            new FakeWebHostEnvironment(Environments.Development),
            Options.Create(new TenancyOptions
            {
                TenantResolutionStrategy = TenantResolutionStrategy.HeaderForDevelopmentOnly,
                AllowDevelopmentHeaderTenantResolution = true
            }),
            dbContext);

        var resolved = await resolver.ResolveAsync();
        Assert.False(resolved.IsResolved);
        Assert.Contains("Suspended", resolved.FailureReason, StringComparison.OrdinalIgnoreCase);

        var service = CreateTenantService(dbContext, currentTenant, data.SuspendedTenantUser, new TenancyOptions { AllowTenantSwitching = true });
        var switched = await service.SwitchTenantAsync(data.SuspendedTenant.Id);
        Assert.False(switched.IsSuccess);
        Assert.Equal("Tenant is not available.", switched.Error);
    }

    [Fact]
    public async Task PlatformAdminCanSuspendAndActivateTenantsAndActionsAreAudited()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        var audit = new CapturingAuditLogger();
        var service = CreateTenantService(dbContext, currentTenant, data.PlatformAdmin, new TenancyOptions(), audit);

        Assert.True((await service.ListPlatformTenantsAsync()).IsSuccess);
        Assert.True((await service.SuspendTenantAsync(data.TenantB.Id)).IsSuccess);
        Assert.Equal(TenantStatus.Suspended, (await dbContext.Tenants.FindAsync(data.TenantB.Id))!.Status);
        Assert.True((await service.ActivateTenantAsync(data.TenantB.Id)).IsSuccess);
        Assert.Equal(TenantStatus.Active, (await dbContext.Tenants.FindAsync(data.TenantB.Id))!.Status);
        Assert.Contains(audit.Entries, entry => entry.Action == "TenantSuspended" && entry.TenantId == data.TenantB.Id);
        Assert.Contains(audit.Entries, entry => entry.Action == "TenantActivated" && entry.TenantId == data.TenantB.Id);
    }

    [Fact]
    public async Task PlatformAdminDoesNotBypassTenantFiltersOnNormalTenantScope()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();

        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var visible = await dbContext.Workspaces.AsNoTracking().ToListAsync();

        Assert.Single(visible);
        Assert.Equal(data.WorkspaceA.Id, visible[0].Id);
    }

    [Fact]
    public async Task TenantAdminAuditQuerySeesOnlyCurrentTenantLogs()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(dbContext, currentTenant, data.TenantAAdmin);

        var result = await service.ListAuditLogsAsync(new AuditLogQuery(Page: 1, PageSize: 20));

        Assert.True(result.IsSuccess);
        var items = result.Value!.Items;
        Assert.Single(items);
        Assert.Equal(data.WorkspaceA.Id, items[0].WorkspaceId);
    }

    [Fact]
    public async Task TenantOwnerAuditQueryWithoutWorkspaceFilterStaysTenantScoped()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(dbContext, currentTenant, data.TenantAOwner);

        var result = await service.ListAuditLogsAsync(new AuditLogQuery(Page: 1, PageSize: 20));

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(data.WorkspaceA.Id, item.WorkspaceId);
        Assert.Equal("TenantA audit", item.Action);
    }

    [Fact]
    public async Task TenantAdminAuditGridProjectsBackendOwnedResultAndSeverity()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var now = DateTimeOffset.UtcNow;
        dbContext.AuditLogs.AddRange(
            new AuditLog
            {
                TenantId = data.TenantA.Id,
                ActorUserId = data.TenantAAdmin.Id,
                Action = "workspace.member.view",
                EntityType = "WorkspaceMember",
                WorkspaceId = data.WorkspaceA.Id,
                Summary = "Member list opened.",
                CorrelationId = "req-success",
                CreatedAt = now.AddMinutes(1)
            },
            new AuditLog
            {
                TenantId = data.TenantA.Id,
                ActorUserId = data.TenantAAdmin.Id,
                Action = "file.download.denied",
                EntityType = "File",
                WorkspaceId = data.WorkspaceA.Id,
                Summary = "Download blocked.",
                CorrelationId = "req-denied",
                CreatedAt = now.AddMinutes(2)
            },
            new AuditLog
            {
                TenantId = data.TenantA.Id,
                ActorUserId = data.TenantAAdmin.Id,
                Action = "export.request.failed",
                EntityType = "ExportJob",
                WorkspaceId = data.WorkspaceA.Id,
                Summary = "Export failed.",
                CorrelationId = "req-failed",
                CreatedAt = now.AddMinutes(3)
            });
        await dbContext.SaveChangesAsync();
        var service = CreateAuditQueryService(dbContext, currentTenant, data.TenantAAdmin);

        var result = await service.ListAuditGridAsync(new AuditLogQuery(Page: 1, PageSize: 20));

        Assert.True(result.IsSuccess);
        var rows = result.Value!.Items.ToDictionary(item => item.Action);
        Assert.Equal("success", rows["workspace.member.view"].Result);
        Assert.Equal("info", rows["workspace.member.view"].Severity);
        Assert.Equal("denied", rows["file.download.denied"].Result);
        Assert.Equal("warning", rows["file.download.denied"].Severity);
        Assert.Equal("failed", rows["export.request.failed"].Result);
        Assert.Equal("critical", rows["export.request.failed"].Severity);
        Assert.Equal(data.WorkspaceA.Name, rows["workspace.member.view"].WorkspaceLabel);
    }

    [Fact]
    public async Task AuditGridFiltersAndCountsOnlyAuthorizedTenantRows()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetPlatformScope();
        var now = new DateTimeOffset(2026, 8, 30, 8, 0, 0, TimeSpan.Zero);
        dbContext.AuditLogs.AddRange(
            new AuditLog
            {
                TenantId = data.TenantA.Id,
                ActorUserId = data.TenantAAdmin.Id,
                Action = "file.export.failed",
                EntityType = "ExportJob",
                WorkspaceId = data.WorkspaceA.Id,
                Summary = "Neptune retention export failed.",
                CreatedAt = now,
            },
            new AuditLog
            {
                TenantId = data.TenantA.Id,
                ActorUserId = data.TenantAAdmin.Id,
                Action = "file.export.completed",
                EntityType = "ExportJob",
                WorkspaceId = data.WorkspaceA.Id,
                Summary = "Neptune retention export completed.",
                CreatedAt = now,
            },
            new AuditLog
            {
                TenantId = data.TenantB.Id,
                ActorUserId = data.TenantBOwner.Id,
                Action = "file.export.failed",
                EntityType = "ExportJob",
                WorkspaceId = data.WorkspaceB.Id,
                Summary = "Neptune retention export failed in another Tenant.",
                CreatedAt = now,
            });
        await dbContext.SaveChangesAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAAdmin,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(true, true, false, false, true)));

        var result = await service.ListAuditGridAsync(new AuditLogQuery(
            Action: " FILE.EXPORT.FAILED ",
            EntityType: " exportjob ",
            FromDate: now.AddMinutes(-1),
            ToDate: now.AddMinutes(1),
            Page: 1,
            PageSize: 100,
            Q: " Neptune ",
            Actor: "tenantaadmin",
            Severity: "CRITICAL",
            Result: "FAILED"));

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Value!.Items);
        Assert.Equal("file.export.failed", row.Action);
        Assert.Equal(data.WorkspaceA.Name, row.WorkspaceLabel);
        Assert.Equal(1, result.Value.TotalCount);
    }

    [Fact]
    public async Task AuditGridActorFacetIsDeniedBeforeRowsOrCountsWithoutSensitiveCapability()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAAdmin,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(true, true, false, false, false)));

        var result = await service.ListAuditGridAsync(new AuditLogQuery(Actor: data.TenantAAdmin.DisplayName));

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task AuditGridGlobalSearchDoesNotUseActorNamesWithoutSensitiveCapability()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var withoutSensitiveCapability = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAAdmin,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(true, true, false, false, false)));
        var withSensitiveCapability = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAAdmin,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(true, true, false, false, true)));

        var redacted = await withoutSensitiveCapability.ListAuditGridAsync(
            new AuditLogQuery(Q: data.TenantAAdmin.DisplayName, PageSize: 100));
        var disclosed = await withSensitiveCapability.ListAuditGridAsync(
            new AuditLogQuery(Q: data.TenantAAdmin.DisplayName, PageSize: 100));

        Assert.True(redacted.IsSuccess);
        Assert.Empty(redacted.Value!.Items);
        Assert.Equal(0, redacted.Value.TotalCount);
        Assert.True(disclosed.IsSuccess);
        Assert.NotEmpty(disclosed.Value!.Items);
        Assert.Equal(disclosed.Value.Items.Count, disclosed.Value.TotalCount);
    }

    [Theory]
    [InlineData("severity", "urgent")]
    [InlineData("result", "pending")]
    [InlineData("q", "overlong")]
    [InlineData("dates", "reversed")]
    public async Task AuditGridRejectsInvalidFilterContracts(string filter, string value)
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var query = filter switch
        {
            "severity" => new AuditLogQuery(Severity: value),
            "result" => new AuditLogQuery(Result: value),
            "q" => new AuditLogQuery(Q: new string('x', 201)),
            _ => new AuditLogQuery(
                FromDate: new DateTimeOffset(2026, 8, 31, 0, 0, 0, TimeSpan.Zero),
                ToDate: new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero)),
        };
        var service = CreateAuditQueryService(dbContext, currentTenant, data.TenantAAdmin);

        var result = await service.ListAuditGridAsync(query);

        Assert.False(result.IsSuccess);
        Assert.Equal("AuditFilterInvalid", result.ErrorDetail?.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task TenantAdminAuditGridRowLookupKeepsOtherTenantAndAbsentRowsIndistinguishable()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        var currentTenantAuditId = await dbContext.AuditLogs.IgnoreQueryFilters()
            .Where(log => log.TenantId == data.TenantA.Id)
            .Select(log => log.Id)
            .SingleAsync();
        var otherTenantAuditId = await dbContext.AuditLogs.IgnoreQueryFilters()
            .Where(log => log.TenantId == data.TenantB.Id)
            .Select(log => log.Id)
            .SingleAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(dbContext, currentTenant, data.TenantAAdmin);

        var visible = await service.GetAuditGridRowAsync(currentTenantAuditId);
        var crossTenant = await service.GetAuditGridRowAsync(otherTenantAuditId);
        var absent = await service.GetAuditGridRowAsync(Guid.NewGuid());
        var malformed = await service.GetAuditGridRowAsync(Guid.Empty);

        Assert.True(visible.IsSuccess);
        Assert.Equal(currentTenantAuditId, visible.Value!.Id);
        Assert.False(crossTenant.IsSuccess);
        Assert.False(absent.IsSuccess);
        Assert.False(malformed.IsSuccess);
        Assert.Equal("AuditEventNotFound", crossTenant.ErrorDetail?.Code);
        Assert.Equal(absent.ErrorDetail, crossTenant.ErrorDetail);
        Assert.Equal(absent.Error, crossTenant.Error);
        Assert.Equal(absent.ErrorDetail, malformed.ErrorDetail);
        Assert.Equal(absent.Error, malformed.Error);
    }

    [Fact]
    public async Task AuditGridRowLookupWithholdsSensitiveFieldsAndNeverReturnsMetadata()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        const string rawMetadata = "{\"secret\":\"must-not-leave-the-server\"}";
        var audit = new AuditLog
        {
            TenantId = data.TenantA.Id,
            ActorUserId = data.TenantAAdmin.Id,
            Action = "audit.detail.read",
            EntityType = "AuditLog",
            WorkspaceId = data.WorkspaceA.Id,
            Summary = "A metadata-safe summary.",
            CorrelationId = "request-sensitive",
            MetadataJson = rawMetadata,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.AuditLogs.Add(audit);
        await dbContext.SaveChangesAsync();

        var service = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAAdmin,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(
                CanView: true,
                CanReview: false,
                CanApprove: false,
                CanExport: false,
                CanViewSensitiveMetadata: false)));

        var result = await service.GetAuditGridRowAsync(audit.Id);

        var row = Assert.IsType<AuditGridRowResponse>(result.Value);
        Assert.Equal("Redacted actor", row.ActorDisplayName);
        Assert.Null(row.RequestId);
        var json = JsonSerializer.Serialize(row);
        Assert.DoesNotContain(rawMetadata, json, StringComparison.Ordinal);
        Assert.DoesNotContain("MetadataJson", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ActorUserId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EntityId", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AuditGridRowLookupDeniesBeforeAnyIdentifierCanProduceNotFound()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAMember,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(
                CanView: false,
                CanReview: false,
                CanApprove: false,
                CanExport: false,
                CanViewSensitiveMetadata: false)));

        var result = await service.GetAuditGridRowAsync(Guid.Empty);

        Assert.False(result.IsSuccess);
        Assert.Equal("CapabilityDenied", result.ErrorDetail?.Code);
        Assert.NotEqual("AuditEventNotFound", result.ErrorDetail?.Code);
    }

    [Fact]
    public async Task AuditSensitiveMetadataRequiresIndependentCapabilityBeforeIdentifierLookup()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var existingAuditId = await dbContext.AuditLogs
            .Where(log => log.TenantId == data.TenantA.Id)
            .Select(log => log.Id)
            .SingleAsync();
        var service = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAAdmin,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(
                CanView: true,
                CanReview: true,
                CanApprove: false,
                CanExport: false,
                CanViewSensitiveMetadata: false)));

        var existing = await service.GetAuditSensitiveMetadataAsync(existingAuditId);
        var absent = await service.GetAuditSensitiveMetadataAsync(Guid.NewGuid());
        var malformed = await service.GetAuditSensitiveMetadataAsync(Guid.Empty);

        Assert.False(existing.IsSuccess);
        Assert.Equal("CapabilityDenied", existing.ErrorDetail?.Code);
        Assert.Equal(existing.ErrorDetail, absent.ErrorDetail);
        Assert.Equal(existing.Error, absent.Error);
        Assert.Equal(existing.ErrorDetail, malformed.ErrorDetail);
        Assert.Equal(existing.Error, malformed.Error);
    }

    [Fact]
    public async Task AuditSensitiveMetadataIsTenantScopedAndRecursivelyRemovesProhibitedFields()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        var otherTenantAuditId = await dbContext.AuditLogs.IgnoreQueryFilters()
            .Where(log => log.TenantId == data.TenantB.Id)
            .Select(log => log.Id)
            .SingleAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var currentTenantAudit = new AuditLog
        {
            TenantId = data.TenantA.Id,
            ActorUserId = data.TenantAAdmin.Id,
            Action = "audit.metadata.read",
            EntityType = "AuditLog",
            WorkspaceId = data.WorkspaceA.Id,
            Summary = "Metadata disclosure test.",
            MetadataJson = """
                {
                  "outcome": "Allowed",
                  "change": { "category": "Role", "refreshToken": "never-return", "secret": "never-return" },
                  "items": [
                    { "field": "status", "from": "Pending", "to": "Active" },
                    { "body": "private body", "claimId": "excluded contract", "evidenceId": "excluded contract" }
                  ]
                }
                """,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.AuditLogs.Add(currentTenantAudit);
        await dbContext.SaveChangesAsync();
        var service = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAAdmin,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(
                CanView: true,
                CanReview: true,
                CanApprove: false,
                CanExport: false,
                CanViewSensitiveMetadata: true)));

        var visible = await service.GetAuditSensitiveMetadataAsync(currentTenantAudit.Id);
        var crossTenant = await service.GetAuditSensitiveMetadataAsync(otherTenantAuditId);
        var absent = await service.GetAuditSensitiveMetadataAsync(Guid.NewGuid());
        var malformed = await service.GetAuditSensitiveMetadataAsync(Guid.Empty);

        Assert.True(visible.IsSuccess);
        Assert.Equal(currentTenantAudit.Id, visible.Value!.AuditId);
        Assert.True(visible.Value.RedactionApplied);
        var payload = visible.Value.Metadata.ToJsonString();
        Assert.Contains("Allowed", payload, StringComparison.Ordinal);
        Assert.Contains("Role", payload, StringComparison.Ordinal);
        Assert.Contains("Pending", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("never-return", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("private body", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("excluded contract", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claim", payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidence", payload, StringComparison.OrdinalIgnoreCase);

        Assert.False(crossTenant.IsSuccess);
        Assert.Equal("AuditEventNotFound", crossTenant.ErrorDetail?.Code);
        Assert.Equal(absent.ErrorDetail, crossTenant.ErrorDetail);
        Assert.Equal(absent.Error, crossTenant.Error);
        Assert.Equal(absent.ErrorDetail, malformed.ErrorDetail);
        Assert.Equal(absent.Error, malformed.Error);
    }

    [Fact]
    public async Task AuditGridListNeverIncludesSensitiveMetadataPayload()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        const string safeMetadataValue = "metadata-value-only-for-exact-event";
        dbContext.AuditLogs.Add(new AuditLog
        {
            TenantId = data.TenantA.Id,
            ActorUserId = data.TenantAAdmin.Id,
            Action = "audit.metadata.list-boundary",
            EntityType = "AuditLog",
            WorkspaceId = data.WorkspaceA.Id,
            Summary = "List boundary test.",
            MetadataJson = $$"""{"outcome":"{{safeMetadataValue}}"}""",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        var service = CreateAuditQueryService(
            dbContext,
            currentTenant,
            data.TenantAAdmin,
            new FixedAuditAuthorizationService(new AuditCapabilityResponse(
                CanView: true,
                CanReview: true,
                CanApprove: false,
                CanExport: false,
                CanViewSensitiveMetadata: true)));

        var result = await service.ListAuditGridAsync(new AuditLogQuery(Page: 1, PageSize: 100));

        Assert.True(result.IsSuccess);
        var payload = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain(safeMetadataValue, payload, StringComparison.Ordinal);
        Assert.DoesNotContain("MetadataJson", payload, StringComparison.OrdinalIgnoreCase);
    }

 [Fact]
public async Task WorkspaceAdminCannotReadAuditLogsForTheirWorkspace()
{
    var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
    currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);

    var workspaceAdmin = new User
    {
        DisplayName = "WorkspaceAdmin",
        Email = "workspace-admin@example.test",
        NormalizedEmail = "WORKSPACE-ADMIN@EXAMPLE.TEST",
        PasswordHash = "hash",
        SystemRole = SystemRole.User,
        Status = UserStatus.Active
    };

    dbContext.Users.Add(workspaceAdmin);
    dbContext.TenantUsers.Add(new TenantUser
    {
        TenantId = data.TenantA.Id,
        UserId = workspaceAdmin.Id,
        Role = TenantUserRole.Member,
        Status = TenantUserStatus.Active,
        JoinedAt = DateTimeOffset.UtcNow
    });
    dbContext.WorkspaceMembers.Add(new WorkspaceMember
    {
        TenantId = data.TenantA.Id,
        WorkspaceId = data.WorkspaceA.Id,
        UserId = workspaceAdmin.Id,
        Role = WorkspaceRole.Admin,
        Status = MembershipStatus.Active,
        JoinedAt = DateTimeOffset.UtcNow
    });

    await dbContext.SaveChangesAsync();

    var service = CreateAuditQueryService(dbContext, currentTenant, workspaceAdmin);

    var result = await service.ListAuditLogsAsync(new AuditLogQuery(WorkspaceId: data.WorkspaceA.Id));
    var gridResult = await service.ListAuditGridAsync(new AuditLogQuery(WorkspaceId: data.WorkspaceA.Id));

    Assert.False(result.IsSuccess);
    Assert.Equal("You are not allowed to view audit logs.", result.Error);
    Assert.False(gridResult.IsSuccess);
    Assert.Equal("You are not allowed to view audit logs.", gridResult.Error);
}  
    [Fact]
    public async Task TenantAdminAuditQueryWithOtherTenantWorkspaceReturnsNoLogs()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(dbContext, currentTenant, data.TenantAAdmin);

        var result = await service.ListAuditLogsAsync(new AuditLogQuery(WorkspaceId: data.WorkspaceB.Id, Page: 1, PageSize: 20));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.Items);
        Assert.Equal(0, result.Value.TotalCount);
    }

    [Fact]
    public async Task TenantAdminSecurityEventQuerySeesOnlyCurrentTenantEvents()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(dbContext, currentTenant, data.TenantAAdmin);

        var result = await service.ListSecurityEventsAsync(new SecurityEventQuery(Page: 1, PageSize: 20));

        Assert.True(result.IsSuccess);
        var item = Assert.Single(result.Value!.Items);
        Assert.Equal(data.TenantAMember.Id, item.UserId);
        Assert.Equal("TenantA denied", item.Summary);
    }

    [Fact]
    public async Task PlatformAdminAuditAndSecurityQueriesCanReadGlobalData()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetPlatformScope();
        var service = CreateAuditQueryService(dbContext, currentTenant, data.PlatformAdmin);

        var audit = await service.ListAuditLogsAsync(new AuditLogQuery(Page: 1, PageSize: 20));
        var security = await service.ListSecurityEventsAsync(new SecurityEventQuery(Page: 1, PageSize: 20));

        Assert.True(audit.IsSuccess);
        Assert.Contains(audit.Value!.Items, item => item.WorkspaceId == data.WorkspaceA.Id);
        Assert.Contains(audit.Value.Items, item => item.WorkspaceId == data.WorkspaceB.Id);

        Assert.True(security.IsSuccess);
        Assert.Contains(security.Value!.Items, item => item.Summary == "TenantA denied");
        Assert.Contains(security.Value.Items, item => item.Summary == "TenantB denied");
    }

    [Fact]
    public async Task NonAdminAuditQueryIsDenied()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var service = CreateAuditQueryService(dbContext, currentTenant, data.TenantAMember);

        var result = await service.ListAuditLogsAsync(new AuditLogQuery(WorkspaceId: data.WorkspaceA.Id));
        var gridResult = await service.ListAuditGridAsync(new AuditLogQuery(WorkspaceId: data.WorkspaceA.Id));
        var rowResult = await service.GetAuditGridRowAsync(
            await dbContext.AuditLogs.Where(log => log.TenantId == data.TenantA.Id).Select(log => log.Id).SingleAsync());

        Assert.False(result.IsSuccess);
        Assert.Equal("You are not allowed to view audit logs.", result.Error);
        Assert.False(gridResult.IsSuccess);
        Assert.Equal("You are not allowed to view audit logs.", gridResult.Error);
        Assert.False(rowResult.IsSuccess);
        Assert.Equal("You are not allowed to view audit logs.", rowResult.Error);
    }

    [Fact]
    public async Task TenantFeatureOverridesAndQuotaLimitsStayTenantScoped()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        var plans = new TenantPlanRepository(dbContext);
        var quota = new QuotaService(plans);

        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);
        var tenantAFeatures = new FeatureFlagService(plans, currentTenant);
        Assert.False(await tenantAFeatures.IsEnabledAsync(FeatureKeys.ProductionTracking));
        Assert.False(await tenantAFeatures.IsEnabledAsync(FeatureKeys.FileSharing));
        Assert.False(await tenantAFeatures.IsEnabledAsync(FeatureKeys.TasksNotificationsV1));
        Assert.False((await quota.CanCreateProjectAsync(data.TenantA.Id)).IsSuccess);
        Assert.False((await quota.CanUploadFileAsync(data.TenantA.Id, 55)).IsSuccess);
        Assert.False((await quota.CanUploadFileAsync(data.TenantA.Id, 95)).IsSuccess);

        currentTenant.SetTenant(data.TenantB.Id, data.TenantB.Slug);
        var tenantBFeatures = new FeatureFlagService(plans, currentTenant);
        Assert.True(await tenantBFeatures.IsEnabledAsync(FeatureKeys.ProductionTracking));
        Assert.True(await tenantBFeatures.IsEnabledAsync(FeatureKeys.FileSharing));
        Assert.False(await tenantBFeatures.IsEnabledAsync(FeatureKeys.TasksNotificationsV1));
        Assert.True((await quota.CanCreateProjectAsync(data.TenantB.Id)).IsSuccess);
        Assert.True((await quota.CanUploadFileAsync(data.TenantB.Id, 55)).IsSuccess);
    }

    [Fact]
    public async Task FileMetadataUsesTenantNamespacedStorageKeysAndSignedUrlsAreNotExposedByLocalStorage()
    {
        var (dbContext, currentTenant, data) = await CreateSeededContextAsync();
        currentTenant.SetTenant(data.TenantA.Id, data.TenantA.Slug);

        var file = await dbContext.FileObjects.SingleAsync();
        Assert.StartsWith($"tenants/{data.TenantA.Id:D}/", file.StorageKey, StringComparison.Ordinal);

        var storage = new LocalFileStorageService(Options.Create(new FileStorageOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "coglatas-tenant-isolation", Guid.NewGuid().ToString("N")),
            MaxFileSizeBytes = 1024,
            AllowedExtensions = [".txt"],
            AllowedContentTypes = ["text/plain"]
        }));
        Assert.Null(await storage.CreateSignedReadUrlAsync(file.StorageKey, TimeSpan.FromMinutes(5)));
    }

    private static async Task AssertOnlyTenantAsync(AppDbContext dbContext, Guid tenantId)
    {
        Assert.All(await dbContext.Workspaces.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
        Assert.All(await dbContext.Groups.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
        Assert.All(await dbContext.Projects.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
        Assert.All(await dbContext.TaskItems.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
        Assert.All(await dbContext.FileObjects.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
        Assert.All(await dbContext.Conversations.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
        Assert.All(await dbContext.Announcements.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
        Assert.All(await dbContext.Notifications.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
        Assert.All(await dbContext.AuditLogs.ToListAsync(), item => Assert.Equal(tenantId, item.TenantId));
    }

    private static async Task<(AppDbContext DbContext, CurrentTenantService CurrentTenant, TenantIsolationTestData Data)> CreateSeededContextAsync()
    {
        var currentTenant = new CurrentTenantService();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new AppDbContext(options, currentTenant);
        var data = await TenantIsolationTestData.SeedAsync(dbContext, currentTenant);
        return (dbContext, currentTenant, data);
    }

    private static TenantService CreateTenantService(
        AppDbContext dbContext,
        ICurrentTenant currentTenant,
        User user,
        TenancyOptions options,
        IAuditLogger? auditLogger = null)
    {
        var repository = new TenantRepository(dbContext);
        return new TenantService(
            repository,
            new TenantAuthorizationService(repository),
            currentTenant,
            new TestCurrentUser(user),
            auditLogger ?? new CapturingAuditLogger(),
            new EfUnitOfWork(dbContext),
            new FakeUserSessionService(),
            options);
    }

      private static DbAuditQueryService CreateAuditQueryService(
    AppDbContext dbContext,
    ICurrentTenant currentTenant,
    User user,
    IAuditAuthorizationService? auditAuthorization = null)
{
    return new DbAuditQueryService(
        dbContext,
        new TestCurrentUser(user),
        currentTenant,
        new TenantRepository(dbContext),
        auditAuthorization);
}

    private sealed class TestCurrentUser(User user) : ICurrentUser
    {
        public Guid? UserId => user.Id;
        public Guid? SessionId => Guid.NewGuid();
        public string? Email => user.Email;
        public SystemRole? SystemRole => user.SystemRole;
        public bool IsAuthenticated => true;
    }

    private sealed class CapturingAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }

    private sealed class FixedAuditAuthorizationService(AuditCapabilityResponse capabilities)
        : IAuditAuthorizationService
    {
        public Task<AuditCapabilityResponse> GetCapabilitiesAsync(
            CancellationToken cancellationToken = default) => Task.FromResult(capabilities);

        public Task<Result> AuthorizeAsync(
            string capabilityKey,
            string operation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult((capabilityKey switch
            {
                CapabilityKeys.AuditView => capabilities.CanView,
                CapabilityKeys.AuditReview => capabilities.CanReview,
                CapabilityKeys.AuditApprove => capabilities.CanApprove,
                CapabilityKeys.AuditExport => capabilities.CanExport,
                CapabilityKeys.AuditSensitiveMetadataView => capabilities.CanViewSensitiveMetadata,
                _ => false,
            })
                ? Result.Success()
                : Result.Failure(new ApplicationErrorDetail(
                    "CapabilityDenied",
                    "The requested Audit operation is not permitted.")));
    }

    private sealed class FakeUserSessionService : IUserSessionService
    {
        public Task<SessionValidationResult> ValidateSessionAsync(Guid userId, Guid sessionId, Guid? tenantId, bool requireActiveTenantMembership, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(SessionValidationResult.Success());
        }

        public Task<Result> RevokeSessionAsync(Guid sessionId, Guid? actorUserId, string reason, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result.Success());
        }

        public Task<Result<int>> RevokeUserSessionsAsync(Guid userId, Guid? actorUserId, string reason, Guid? exceptSessionId = null, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Result<int>.Success(0));
        }
    }

    private sealed class FakeWebHostEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Coglatas.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
