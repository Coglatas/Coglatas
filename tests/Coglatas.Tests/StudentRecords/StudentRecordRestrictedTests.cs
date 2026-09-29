using System.Text;
using System.Text.Json;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.StudentRecords;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;

namespace Coglatas.Tests.StudentRecords;

public sealed class StudentRecordRestrictedTests
{
    [Fact]
    public void StudentRecordRestrictedFieldsAreClassified()
    {
        Assert.Equal(DataClassification.StudentRecordRestricted, StudentRecordDataPolicy.Classify(StudentRecordDataPolicy.HealthNotes));
        Assert.Equal(DataClassification.StudentRecordRestricted, StudentRecordDataPolicy.Classify(StudentRecordDataPolicy.GuardianContact));
        Assert.Equal(DataClassification.StudentRecordRestricted, StudentRecordDataPolicy.Classify(StudentRecordDataPolicy.Grades));
        Assert.Equal(DataClassification.StudentRecordRestricted, StudentRecordDataPolicy.Classify(StudentRecordDataPolicy.AttendanceStatus));
        Assert.Equal(DataClassification.Public, StudentRecordDataPolicy.Classify(StudentRecordDataPolicy.PublicDisplayName));
        Assert.Equal(DataClassification.Public, StudentRecordDataPolicy.Classify(StudentRecordDataPolicy.HomeroomLabel));
        Assert.Equal(DataClassification.UnknownSensitive, StudentRecordDataPolicy.Classify("internalSensitiveNotes"));
    }

    [Fact]
    public async Task UnauthorizedCallerCannotReadStudentRecordRestrictedValues()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.ExternalGuest));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.False(result.IsSuccess);
        Assert.Null(result.Value);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(fixture.Record.GuardianContact!, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(fixture.Record.Grades!, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(fixture.Record.AttendanceStatus!.Value.ToString(), JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task UnauthorizedDenialAuditDoesNotContainRestrictedValues()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.ExternalGuest));

        await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        var entry = Assert.Single(fixture.Audit.Entries);
        var serialized = JsonSerializer.Serialize(entry);
        Assert.Contains(nameof(DataClassification.StudentRecordRestricted), serialized);
        Assert.Contains(StudentRecordDataPolicy.HealthNotes, serialized);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, serialized);
        Assert.DoesNotContain(fixture.Record.GuardianContact!, serialized);
        Assert.DoesNotContain(fixture.Record.Grades!, serialized);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, serialized);
    }

    [Fact]
    public async Task AuthorizedScopedCallerCanReadOnlyRequestedRestrictedValues()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));

        var result = await fixture.Service.GetRestrictedAsync(
            fixture.Record.Id,
            new StudentRecordRestrictedRequest(
                [StudentRecordDataPolicy.HealthNotes, "internalSensitiveNotes"],
                IncludePublic: true));

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.Equal(fixture.Record.PublicDisplayName, response.Public!.PublicDisplayName);
        Assert.Equal(fixture.Record.HomeroomLabel, response.Public.HomeroomLabel);
        Assert.Equal(fixture.Record.HealthNotes, response.RestrictedFields[StudentRecordDataPolicy.HealthNotes]);
        Assert.DoesNotContain(StudentRecordDataPolicy.Grades, response.RestrictedFields.Keys);
        Assert.DoesNotContain("internalSensitiveNotes", response.RestrictedFields.Keys);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task UnknownSensitiveStudentFieldIsFailClosedByDefault()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));

        var result = await fixture.Service.GetRestrictedAsync(
            fixture.Record.Id,
            new StudentRecordRestrictedRequest(["internalSensitiveNotes"]));

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value!.RestrictedFields);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task GuardianCanReadOnlyPolicyPermittedFieldsOfLinkedStudent()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.Guardian,
            HasGuardianRelationship: true));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.True(result.IsSuccess);
        var fields = result.Value!.RestrictedFields;
        Assert.Equal(fixture.Record.GuardianContact, fields[StudentRecordDataPolicy.GuardianContact]);
        Assert.Equal(fixture.Record.AttendanceStatus!.Value.ToString(), fields[StudentRecordDataPolicy.AttendanceStatus]);
        Assert.DoesNotContain(StudentRecordDataPolicy.Grades, fields.Keys);
        Assert.DoesNotContain(StudentRecordDataPolicy.HealthNotes, fields.Keys);
    }

    [Fact]
    public async Task GuardianCannotReadUnlinkedStudentRestrictedRecord()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.Guardian));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(fixture.Record.GuardianContact!, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(fixture.Record.Grades!, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task StudentCanReadOnlySelfPolicyPermittedFields()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.Student, IsSelf: true));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.True(result.IsSuccess);
        var fields = result.Value!.RestrictedFields;
        Assert.Equal(fixture.Record.AttendanceStatus!.Value.ToString(), fields[StudentRecordDataPolicy.AttendanceStatus]);
        Assert.DoesNotContain(StudentRecordDataPolicy.HealthNotes, fields.Keys);
        Assert.DoesNotContain(StudentRecordDataPolicy.GuardianContact, fields.Keys);
        Assert.DoesNotContain(StudentRecordDataPolicy.Grades, fields.Keys);
    }

    [Fact]
    public async Task TeacherOutsideScopeCannotReadRestrictedStudentRecord()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.Teacher));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(fixture.Record.Grades!, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task HomeroomTeacherCanReadOnlyPolicyPermittedFieldsWithinHomeroomScope()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.HomeroomTeacher,
            HasHomeroomScope: true));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.True(result.IsSuccess);
        var fields = result.Value!.RestrictedFields;
        Assert.Equal(fixture.Record.HealthNotes, fields[StudentRecordDataPolicy.HealthNotes]);
        Assert.Equal(fixture.Record.Grades, fields[StudentRecordDataPolicy.Grades]);
        Assert.DoesNotContain(StudentRecordDataPolicy.GuardianContact, fields.Keys);
    }

    [Fact]
    public async Task GradeTeacherCanReadOnlyPolicyPermittedFieldsWithinGradeScope()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.GradeTeacher,
            HasGradeScope: true));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.True(result.IsSuccess);
        var fields = result.Value!.RestrictedFields;
        Assert.Equal(fixture.Record.Grades, fields[StudentRecordDataPolicy.Grades]);
        Assert.Equal(fixture.Record.AttendanceStatus!.Value.ToString(), fields[StudentRecordDataPolicy.AttendanceStatus]);
        Assert.DoesNotContain(StudentRecordDataPolicy.HealthNotes, fields.Keys);
        Assert.DoesNotContain(StudentRecordDataPolicy.GuardianContact, fields.Keys);
    }

    [Fact]
    public async Task ExternalGuestCannotAccessStudentRecordRestricted()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.ExternalGuest));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(fixture.Record.GuardianContact!, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task MissingRoleFailsClosed()
    {
        var fixture = new Fixture(null);

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task MissingGuardianRelationshipFailsClosed()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.Guardian));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Record.GuardianContact!, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task MissingTeacherScopeFailsClosed()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.Teacher));

        var result = await fixture.Service.GetRestrictedAsync(fixture.Record.Id, RestrictedRequest());

        Assert.False(result.IsSuccess);
        Assert.DoesNotContain(fixture.Record.Grades!, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task MissingFieldAccessPolicyForRoleOmitsRestrictedField()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.StudentAdmin,
            HasStudentAdminScope: true));

        var result = await fixture.Service.GetRestrictedAsync(
            fixture.Record.Id,
            new StudentRecordRestrictedRequest([StudentRecordDataPolicy.HealthNotes, StudentRecordDataPolicy.Grades]));

        Assert.True(result.IsSuccess);
        var fields = result.Value!.RestrictedFields;
        Assert.Equal(fixture.Record.Grades, fields[StudentRecordDataPolicy.Grades]);
        Assert.DoesNotContain(StudentRecordDataPolicy.HealthNotes, fields.Keys);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task SchoolAdminCanAccessOnlyPolicyPermittedFieldsInsideScope()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.SchoolAdmin,
            HasSchoolAdminScope: true));

        var result = await fixture.Service.GetRestrictedAsync(
            fixture.Record.Id,
            new StudentRecordRestrictedRequest([StudentRecordDataPolicy.HealthNotes, "internalSensitiveNotes"]));

        Assert.True(result.IsSuccess);
        Assert.Equal(fixture.Record.HealthNotes, result.Value!.RestrictedFields[StudentRecordDataPolicy.HealthNotes]);
        Assert.DoesNotContain("internalSensitiveNotes", result.Value.RestrictedFields.Keys);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task AuthorizedSensitiveStudentRecordViewCreatesMetadataOnlyAudit()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));

        await fixture.Service.GetRestrictedAsync(
            fixture.Record.Id,
            new StudentRecordRestrictedRequest([StudentRecordDataPolicy.HealthNotes]));

        var entry = Assert.Single(fixture.Audit.Entries, item => item.Action == "student_record.view_sensitive");
        var serialized = JsonSerializer.Serialize(entry);
        Assert.Contains(StudentRecordDataPolicy.HealthNotes, serialized);
        Assert.Contains("allow", serialized);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, serialized);
        Assert.DoesNotContain(fixture.Record.GuardianContact!, serialized);
        Assert.DoesNotContain(fixture.Record.Grades!, serialized);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, serialized);
    }

    [Fact]
    public async Task StudentRecordRestrictedExportWithoutReasonIsDeniedAndAuditedWithoutValues()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));

        var result = await fixture.Service.RequestRestrictedExportAsync(
            fixture.Record.Id,
            ExportRequest(reason: null));

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.ExportGrants.Grants);
        AssertAuditDenial(fixture, "export_reason_missing");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.grant_issue_denied");
    }

    [Fact]
    public async Task StudentRecordRestrictedExportWithInvalidReasonIsDenied()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));

        var result = await fixture.Service.RequestRestrictedExportAsync(
            fixture.Record.Id,
            ExportRequest(reason: "short"));

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.ExportGrants.Grants);
        AssertAuditDenial(fixture, "invalid_export_reason");
    }

    [Fact]
    public async Task StudentRecordRestrictedExportRequestRequiresFreshReauthorizationAndFieldPolicy()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));

        var result = await fixture.Service.RequestRestrictedExportAsync(fixture.Record.Id, ExportRequest());

        Assert.True(result.IsSuccess);
        Assert.Single(fixture.ExportGrants.Grants);
        Assert.Contains(StudentRecordDataPolicy.HealthNotes, result.Value!.AuthorizedFields);
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.reauthorization_passed");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.grant_issued");
    }

    [Fact]
    public async Task FailedReauthorizationBlocksStudentRecordRestrictedExport()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.Guardian));

        var result = await fixture.Service.RequestRestrictedExportAsync(fixture.Record.Id, ExportRequest());

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.ExportGrants.Grants);
        AssertAuditDenial(fixture, "guardian_relationship_removed");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.reauthorization_failed");
    }

    [Fact]
    public async Task UnauthorizedRestrictedFieldIsDeniedDuringExportRequest()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.Guardian,
            HasGuardianRelationship: true));

        var result = await fixture.Service.RequestRestrictedExportAsync(
            fixture.Record.Id,
            ExportRequest(fields: [StudentRecordDataPolicy.HealthNotes]));

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.ExportGrants.Grants);
        AssertAuditDenial(fixture, "field_access_denied");
    }

    [Fact]
    public async Task UnknownSensitiveFieldIsDeniedByDefaultDuringExportRequest()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));

        var result = await fixture.Service.RequestRestrictedExportAsync(
            fixture.Record.Id,
            ExportRequest(fields: ["internalSensitiveNotes"]));

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.ExportGrants.Grants);
        AssertAuditDenial(fixture, "unknown_sensitive_classification");
    }

    [Fact]
    public async Task MissingFieldAccessPolicyFailsClosedForExportRequest()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.StudentAdmin,
            HasStudentAdminScope: true));

        var result = await fixture.Service.RequestRestrictedExportAsync(
            fixture.Record.Id,
            ExportRequest(fields: [StudentRecordDataPolicy.HealthNotes]));

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.ExportGrants.Grants);
        AssertAuditDenial(fixture, "field_access_denied");
    }

    [Fact]
    public async Task ExportBuildRechecksFieldAccessPolicyAndBlocksPolicyChangeAfterRequest()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));
        var grant = await RequestExportGrantAsync(fixture);
        fixture.SchoolAccess.Context = new StudentRecordSchoolAccessContext(
            SchoolRole.GradeTeacher,
            HasGradeScope: true);

        var result = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "policy_changed");
    }

    [Fact]
    public async Task ExportDownloadRechecksFieldAccessPolicyAndBlocksPolicyChangeAfterBuild()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));
        var grant = await RequestExportGrantAsync(fixture);
        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);
        Assert.True(build.IsSuccess);
        fixture.SchoolAccess.Context = new StudentRecordSchoolAccessContext(
            SchoolRole.GradeTeacher,
            HasGradeScope: true);

        var result = await fixture.Service.DownloadRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "policy_changed");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.download_denied_policy_changed");
    }

    [Fact]
    public async Task RoleChangeAfterExportRequestBlocksBuild()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));
        var grant = await RequestExportGrantAsync(fixture);
        fixture.SchoolAccess.Context = new StudentRecordSchoolAccessContext(
            SchoolRole.ExternalGuest);

        var result = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "export_reauthorization_failed");
    }

    [Fact]
    public async Task ExistingExportPackageDownloadIsDeniedAfterRoleChange()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));
        var grant = await RequestExportGrantAsync(fixture);
        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);
        Assert.True(build.IsSuccess);
        fixture.SchoolAccess.Context = new StudentRecordSchoolAccessContext(SchoolRole.ExternalGuest);

        var result = await fixture.Service.DownloadRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "export_reauthorization_failed");
    }

    [Fact]
    public async Task GuardianRelationshipRemovalBlocksBuild()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.Guardian,
            HasGuardianRelationship: true));
        var grant = await RequestExportGrantAsync(fixture, fields: [StudentRecordDataPolicy.GuardianContact]);
        fixture.SchoolAccess.Context = new StudentRecordSchoolAccessContext(SchoolRole.Guardian);

        var result = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "guardian_relationship_removed");
    }

    [Fact]
    public async Task ExistingExportPackageDownloadIsDeniedAfterGuardianRelationshipRemoval()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.Guardian,
            HasGuardianRelationship: true));
        var grant = await RequestExportGrantAsync(fixture, fields: [StudentRecordDataPolicy.GuardianContact]);
        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);
        Assert.True(build.IsSuccess);
        fixture.SchoolAccess.Context = new StudentRecordSchoolAccessContext(SchoolRole.Guardian);

        var result = await fixture.Service.DownloadRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "guardian_relationship_removed");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.download_denied_scope_changed");
    }

    [Fact]
    public async Task TeacherScopeRemovalBlocksBuild()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.Teacher,
            HasTeacherScope: true));
        var grant = await RequestExportGrantAsync(fixture, fields: [StudentRecordDataPolicy.Grades]);
        fixture.SchoolAccess.Context = new StudentRecordSchoolAccessContext(SchoolRole.Teacher);

        var result = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "teacher_scope_removed");
    }

    [Fact]
    public async Task ExistingExportPackageDownloadIsDeniedAfterTeacherScopeRemoval()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.Teacher,
            HasTeacherScope: true));
        var grant = await RequestExportGrantAsync(fixture, fields: [StudentRecordDataPolicy.Grades]);
        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);
        Assert.True(build.IsSuccess);
        fixture.SchoolAccess.Context = new StudentRecordSchoolAccessContext(SchoolRole.Teacher);

        var result = await fixture.Service.DownloadRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "teacher_scope_removed");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.download_denied_scope_changed");
    }

    [Fact]
    public async Task ExpiredExportPackageGrantBlocksDownload()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));
        var grant = await RequestExportGrantAsync(fixture);
        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);
        Assert.True(build.IsSuccess);
        fixture.Clock.UtcNow = grant.ExpiresAt.AddSeconds(1);

        var result = await fixture.Service.DownloadRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "grant_expired");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.grant_expired");
    }

    [Fact]
    public async Task RevokedExportPackageGrantBlocksDownload()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));
        var grant = await RequestExportGrantAsync(fixture);
        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);
        Assert.True(build.IsSuccess);
        fixture.ExportGrants.Grants[0].RevokedAt = fixture.Clock.UtcNow;

        var result = await fixture.Service.DownloadRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "grant_revoked");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.grant_revoked");
    }

    [Fact]
    public async Task ExportPackageGrantActorMismatchBlocksDownload()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));
        var grant = await RequestExportGrantAsync(fixture);
        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);
        Assert.True(build.IsSuccess);
        fixture.CurrentUser.UserIdValue = Guid.NewGuid();

        var result = await fixture.Service.DownloadRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        Assert.Contains("actor_mismatch", JsonSerializer.Serialize(fixture.Audit.Entries));
    }

    [Fact]
    public async Task ExportPackageGrantScopeMismatchBlocksDownload()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.SchoolAdmin, HasSchoolAdminScope: true));
        var grant = await RequestExportGrantAsync(fixture);
        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);
        Assert.True(build.IsSuccess);
        fixture.ExportGrants.Grants[0].WorkspaceId = Guid.NewGuid();

        var result = await fixture.Service.DownloadRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.False(result.IsSuccess);
        AssertAuditDenial(fixture, "scope_mismatch");
        Assert.Contains(fixture.Audit.Entries, item => item.Action == "export_package.download_denied_scope_changed");
    }

    [Fact]
    public async Task ExportPackageGrantStoresOnlyMetadataAndPackageContainsOnlyAuthorizedFields()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(
            SchoolRole.Guardian,
            HasGuardianRelationship: true));
        var grant = await RequestExportGrantAsync(
            fixture,
            fields: [StudentRecordDataPolicy.GuardianContact, StudentRecordDataPolicy.AttendanceStatus]);

        var build = await fixture.Service.BuildRestrictedExportAsync(grant.ExportPackageGrantId);

        Assert.True(build.IsSuccess);
        var storedGrant = Assert.Single(fixture.ExportGrants.Grants);
        var grantJson = JsonSerializer.Serialize(storedGrant);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, grantJson);
        Assert.DoesNotContain(fixture.Record.GuardianContact!, grantJson);
        Assert.DoesNotContain(fixture.Record.Grades!, grantJson);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, grantJson);
        Assert.Contains("StudentRecordRestricted", grantJson);
        Assert.Contains("StudentRecord", grantJson);

        var packageJson = Encoding.UTF8.GetString(build.Value!.Content);
        Assert.Contains(StudentRecordDataPolicy.GuardianContact, packageJson);
        Assert.Contains(StudentRecordDataPolicy.AttendanceStatus, packageJson);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, packageJson);
        Assert.DoesNotContain(fixture.Record.Grades!, packageJson);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, packageJson);
    }

    [Fact]
    public async Task DenialAuditForExportDoesNotContainRestrictedValues()
    {
        var fixture = new Fixture(new StudentRecordSchoolAccessContext(SchoolRole.ExternalGuest));

        await fixture.Service.RequestRestrictedExportAsync(fixture.Record.Id, ExportRequest());

        var serialized = JsonSerializer.Serialize(fixture.Audit.Entries);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, serialized);
        Assert.DoesNotContain(fixture.Record.GuardianContact!, serialized);
        Assert.DoesNotContain(fixture.Record.Grades!, serialized);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, serialized);
    }

    private static StudentRecordRestrictedRequest RestrictedRequest()
    {
        return new StudentRecordRestrictedRequest(
            [
                StudentRecordDataPolicy.HealthNotes,
                StudentRecordDataPolicy.GuardianContact,
                StudentRecordDataPolicy.Grades,
                StudentRecordDataPolicy.AttendanceStatus
            ]);
    }

    private static StudentRecordExportRequest ExportRequest(
        IReadOnlyCollection<string>? fields = null,
        string? reason = "Operational school record export")
    {
        return new StudentRecordExportRequest(
            fields ?? [StudentRecordDataPolicy.HealthNotes],
            reason);
    }

    private static async Task<StudentRecordExportGrantResponse> RequestExportGrantAsync(
        Fixture fixture,
        IReadOnlyCollection<string>? fields = null)
    {
        var result = await fixture.Service.RequestRestrictedExportAsync(fixture.Record.Id, ExportRequest(fields));
        Assert.True(result.IsSuccess);
        return result.Value!;
    }

    private static void AssertAuditDenial(Fixture fixture, string reason)
    {
        var serialized = JsonSerializer.Serialize(fixture.Audit.Entries);
        Assert.Contains(reason, serialized);
        Assert.DoesNotContain(fixture.Record.HealthNotes!, serialized);
        Assert.DoesNotContain(fixture.Record.GuardianContact!, serialized);
        Assert.DoesNotContain(fixture.Record.Grades!, serialized);
        Assert.DoesNotContain(fixture.Record.InternalSensitiveNotes!, serialized);
    }

    private sealed class Fixture
    {
        public Fixture(StudentRecordSchoolAccessContext? context)
        {
            Record.TenantId = TenantId;
            Record.WorkspaceId = WorkspaceId;
            CurrentUser = new FakeCurrentUser(UserId);
            CurrentTenant = new FakeCurrentTenant(TenantId);
            StudentRecords = new FakeStudentRecordRepository(Record);
            Workspaces = new FakeWorkspaceRepository(WorkspaceId, UserId, WorkspaceRole.Admin);
            SchoolAccess = new FakeStudentRecordSchoolAccessContextProvider(context);
            ExportGrants = new FakeStudentRecordExportGrantRepository();
            Clock = new FakeClock();
            UnitOfWork = new FakeUnitOfWork();
            Audit = new FakeAuditLogger();
            Service = new StudentRecordService(
                StudentRecords,
                ExportGrants,
                new StudentRecordAuthorizationService(Workspaces, SchoolAccess),
                CurrentUser,
                CurrentTenant,
                Clock,
                UnitOfWork,
                Audit);
        }

        private Guid TenantId { get; } = Guid.NewGuid();
        private Guid WorkspaceId { get; } = Guid.NewGuid();
        private Guid UserId { get; } = Guid.NewGuid();

        public StudentRecord Record { get; } = new()
        {
            TenantId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            PublicDisplayName = "Public Student",
            HomeroomLabel = "Class 2-A",
            HealthNotes = "peanut allergy requires epipen",
            GuardianContact = "guardian +1-555-0101",
            Grades = "math A, science B",
            AttendanceStatus = AttendanceStatus.NotAttending,
            InternalSensitiveNotes = "counseling plan is private"
        };

        public FakeCurrentUser CurrentUser { get; }
        private FakeCurrentTenant CurrentTenant { get; }
        private FakeStudentRecordRepository StudentRecords { get; }
        private FakeWorkspaceRepository Workspaces { get; }
        public FakeStudentRecordSchoolAccessContextProvider SchoolAccess { get; }
        public FakeStudentRecordExportGrantRepository ExportGrants { get; }
        public FakeClock Clock { get; }
        private FakeUnitOfWork UnitOfWork { get; }
        public FakeAuditLogger Audit { get; }
        public StudentRecordService Service { get; }
    }

    private sealed class FakeStudentRecordSchoolAccessContextProvider(StudentRecordSchoolAccessContext? context) : IStudentRecordSchoolAccessContextProvider
    {
        public StudentRecordSchoolAccessContext? Context { get; set; } = context;

        public Task<StudentRecordSchoolAccessContext?> GetAccessContextAsync(
            Guid userId,
            StudentRecord record,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Context);
        }
    }

    private sealed class FakeStudentRecordRepository(StudentRecord record) : IStudentRecordRepository
    {
        public Task<StudentRecord?> GetByIdAsync(Guid studentRecordId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(studentRecordId == record.Id ? record : null);
        }

        public Task AddAsync(StudentRecord studentRecord, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeWorkspaceRepository(Guid workspaceId, Guid userId, WorkspaceRole role) : IWorkspaceRepository
    {
        public Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid requestedUserId, bool includeAll, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<Workspace>>([]);
        }

        public Task<Workspace?> GetByIdAsync(Guid requestedWorkspaceId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<Workspace?>(null);
        }

        public Task<WorkspaceMember?> GetMemberAsync(Guid requestedWorkspaceId, Guid requestedUserId, CancellationToken cancellationToken = default)
        {
            if (requestedWorkspaceId != workspaceId || requestedUserId != userId)
            {
                return Task.FromResult<WorkspaceMember?>(null);
            }

            return Task.FromResult<WorkspaceMember?>(new WorkspaceMember
            {
                WorkspaceId = workspaceId,
                UserId = userId,
                Role = role,
                Status = MembershipStatus.Active
            });
        }

        public Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid requestedWorkspaceId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IReadOnlyList<WorkspaceMember>>([]);
        }

        public Task AddAsync(Workspace workspace, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task AddMemberAsync(WorkspaceMember member, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid? UserIdValue { get; set; } = userId;
        public Guid? UserId => UserIdValue;
        public Guid? SessionId => Guid.NewGuid();
        public string Email => "student-record-reader@example.test";
        public SystemRole? SystemRole => global::Coglatas.Domain.Enums.SystemRole.Admin;
        public bool IsAuthenticated => true;
    }

    private sealed class FakeCurrentTenant(Guid tenantId) : ICurrentTenant
    {
        public Guid TenantId => tenantId;
        public bool IsAvailable => true;
        public string TenantSlug => "tenant-a";
        public bool IsPlatformScope => false;
    }

    private sealed class FakeStudentRecordExportGrantRepository : IStudentRecordExportGrantRepository
    {
        public List<ExportPackageGrant> Grants { get; } = [];

        public Task<ExportPackageGrant?> GetAsync(Guid exportPackageGrantId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Grants.FirstOrDefault(grant => grant.Id == exportPackageGrantId));
        }

        public Task AddAsync(ExportPackageGrant grant, CancellationToken cancellationToken = default)
        {
            Grants.Add(grant);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 6, 30, 0, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeUnitOfWork : IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(1);
        }
    }

    private sealed class FakeAuditLogger : IAuditLogger
    {
        public List<AuditLogEntry> Entries { get; } = [];

        public Task LogAsync(AuditLogEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
    }
}
