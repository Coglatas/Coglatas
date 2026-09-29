using System.IO.Compression;
using System.Text.Json;
using Coglatas.Application.Common.Tenancy;
using Coglatas.Application.Security.Redaction;
using Coglatas.Domain.Entities;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Tenancy;

[Trait("Scope", "WPC02E")]
public sealed class TenantExportRedactionTests
{
    [Fact]
    public async Task MetadataZip_RoutesSerializedRowsThroughExportRowRedaction()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var redactor = new RecordingRedactionService();
        var repository = new TenantExportRepository(dbContext, redactor);
        var context = CreateAuthorizationContext();

        var archive = await repository.CreateMetadataZipAsync(
            context.TenantId!.Value,
            context,
            CancellationToken.None);

        Assert.NotEmpty(archive);
        var call = Assert.Single(redactor.Calls);
        Assert.Equal(context, call.Context);
        Assert.Equal(RedactionProfile.ExportRow, call.Profile);
    }

    [Fact]
    public async Task MetadataZip_SerializesCanonicalProjectionForPersistedTenantRow()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var context = CreateAuthorizationContext();
        var tenantId = context.TenantId!.Value;

        dbContext.Tenants.Add(new Tenant(tenantId)
        {
            Name = "Original tenant",
            Slug = "original-tenant",
            DisplayName = "Sensitive display name"
        });
        await dbContext.SaveChangesAsync();

        var redactor = new ReplacingTenantRowRedactionService();
        var repository = new TenantExportRepository(dbContext, redactor);

        var archiveBytes = await repository.CreateMetadataZipAsync(
            tenantId,
            context,
            CancellationToken.None);

        Assert.Equal(1, redactor.TenantRowCalls);

        using var archiveStream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var tenantEntry = archive.GetEntry("tenant.json");
        Assert.NotNull(tenantEntry);

        await using var tenantStream = tenantEntry.Open();
        using var json = await JsonDocument.ParseAsync(tenantStream);
        Assert.Equal(JsonValueKind.Array, json.RootElement.ValueKind);
        Assert.Equal(1, json.RootElement.GetArrayLength());

        var rows = json.RootElement.EnumerateArray();
        Assert.True(rows.MoveNext());
        var row = rows.Current;
        Assert.False(rows.MoveNext());
        Assert.Equal("[redacted]", row.GetProperty("displayName").GetString());
        Assert.False(row.TryGetProperty("name", out _));
    }

    [Fact]
    public async Task MetadataZip_ProductionRedactor_RedactsPersistedRestrictedFields()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var context = CreateAuthorizationContext();
        var tenantId = context.TenantId!.Value;

        dbContext.Tenants.Add(new Tenant(tenantId)
        {
            Name = "School tenant",
            Slug = "school-tenant",
            DisplayName = "School",
            PrimaryDomain = "students.school.example"
        });
        await dbContext.SaveChangesAsync();

        var repository = new TenantExportRepository(dbContext, new CanonicalRedactionService());
        var archiveBytes = await repository.CreateMetadataZipAsync(
            tenantId,
            context,
            CancellationToken.None);

        using var archiveStream = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read);
        var tenantEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("tenant.json"));
        await using var tenantStream = tenantEntry.Open();
        using var json = await JsonDocument.ParseAsync(tenantStream);
        var row = json.RootElement.EnumerateArray().Single();

        Assert.Equal("School tenant", row.GetProperty("name").GetString());
        Assert.Equal("[redacted:restricted]", row.GetProperty("primaryDomain").GetString());
    }

    [Fact]
    public async Task MetadataZip_FailsClosed_WhenTenantContextDoesNotMatchRequestedTenant()
    {
        var context = CreateAuthorizationContext();
        await AssertContextRejectedAsync(
            CopyContext(context, tenantId: Guid.NewGuid()),
            context.TenantId!.Value);
    }

    [Fact]
    public async Task MetadataZip_FailsClosed_WhenAuthorizationIsNotAllowed()
    {
        var context = CreateAuthorizationContext();
        await AssertContextRejectedAsync(
            CopyContext(context, authorizationState: RedactionAuthorizationState.Denied),
            context.TenantId!.Value);
    }

    [Fact]
    public async Task MetadataZip_FailsClosed_WhenActorIsMissing()
    {
        var context = CreateAuthorizationContext();
        await AssertContextRejectedAsync(
            CopyContext(context, actorId: null, overrideActor: true),
            context.TenantId!.Value);
    }

    [Fact]
    public async Task MetadataZip_FailsClosed_WhenPurposeIsNotExportBuild()
    {
        var context = CreateAuthorizationContext();
        await AssertContextRejectedAsync(
            CopyContext(context, purpose: RedactionPurpose.NormalOperation),
            context.TenantId!.Value);
    }

    [Fact]
    public async Task MetadataZip_FailsClosed_WhenExportRowCannotBeSerialized()
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var repository = new TenantExportRepository(dbContext, new ClosedRedactionService());
        var context = CreateAuthorizationContext();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CreateMetadataZipAsync(
                context.TenantId!.Value,
                context,
                CancellationToken.None));

        Assert.Contains("serializable export row", exception.Message);
    }

    private static AuthorizationContext CopyContext(
        AuthorizationContext source,
        Guid? actorId = null,
        bool overrideActor = false,
        Guid? tenantId = null,
        RedactionPurpose? purpose = null,
        RedactionAuthorizationState? authorizationState = null) =>
        new(
            ActorId: overrideActor ? actorId : source.ActorId,
            TenantId: tenantId ?? source.TenantId,
            ModuleKey: source.ModuleKey,
            Purpose: purpose ?? source.Purpose,
            RequestId: source.RequestId,
            AuthorizationState: authorizationState ?? source.AuthorizationState);

    private static async Task AssertContextRejectedAsync(
        AuthorizationContext context,
        Guid requestedTenantId)
    {
        var currentTenant = new CurrentTenantService();
        currentTenant.SetPlatformScope();
        await using var dbContext = CreateDbContext(currentTenant);
        var repository = new TenantExportRepository(dbContext, new CanonicalRedactionService());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.CreateMetadataZipAsync(
                requestedTenantId,
                context,
                CancellationToken.None));

        Assert.Contains("does not match", exception.Message);
    }

    private static AppDbContext CreateDbContext(CurrentTenantService currentTenant) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
                .Options,
            currentTenant);

    private static AuthorizationContext CreateAuthorizationContext() =>
        new(
            ActorId: Guid.NewGuid(),
            TenantId: Guid.NewGuid(),
            ModuleKey: "TenantExport",
            Purpose: RedactionPurpose.ExportBuild,
            RequestId: "wpc02e-export-build",
            AuthorizationState: RedactionAuthorizationState.Allowed);

    private sealed class RecordingRedactionService : IRedactionService
    {
        public List<(AuthorizationContext Context, RedactionProfile Profile)> Calls { get; } = [];

        public RedactionResult Redact(
            AuthorizationContext context,
            object source,
            RedactionProfile profile)
        {
            Calls.Add((context, profile));
            return new RedactionResult(source, RedactionApplied: false);
        }
    }

    private sealed class ReplacingTenantRowRedactionService : IRedactionService
    {
        public int TenantRowCalls { get; private set; }

        public RedactionResult Redact(
            AuthorizationContext context,
            object source,
            RedactionProfile profile)
        {
            Assert.Equal(RedactionProfile.ExportRow, profile);

            if (source.GetType().GetProperty("DisplayName") is not null)
            {
                TenantRowCalls++;
                return new RedactionResult(
                    new { DisplayName = "[redacted]" },
                    RedactionApplied: true);
            }

            return new RedactionResult(source, RedactionApplied: false);
        }
    }

    private sealed class ClosedRedactionService : IRedactionService
    {
        public RedactionResult Redact(
            AuthorizationContext context,
            object source,
            RedactionProfile profile) =>
            new(new RedactedPayload(profile, "authorization"), RedactionApplied: true);
    }
}
