using Coglatas.Application.Artifacts;
using Coglatas.Application.Audit;
using Coglatas.Application.Common;
using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Files;
using Coglatas.Application.Tenancy;

namespace Coglatas.Infrastructure.Persistence;

public sealed class DbAuditClaimsEvidenceService(
    AppDbContext dbContext,
    IArtifactRepository artifacts,
    IArtifactEvidenceRepository evidenceRepository,
    IArtifactAuthorizationService artifactAuthorization,
    IFileRepository files,
    IFileAuthorizationService fileAuthorization,
    IAuditAuthorizationService auditAuthorization,
    ICurrentUser currentUser) : IAuditClaimsEvidenceService
{
    private readonly AuditClaimsEvidenceProjectionBuilder _projectionBuilder =
        new(dbContext, artifacts, evidenceRepository, artifactAuthorization, files, fileAuthorization);

    public async Task<Result<AuditClaimsEvidenceResponse>> GetAsync(
        Guid artifactVersionId,
        CancellationToken cancellationToken = default)
    {
        var capabilities = await auditAuthorization.GetCapabilitiesAsync(cancellationToken);
        if (!capabilities.CanView)
        {
            var denied = await auditAuthorization.AuthorizeAsync(
                CapabilityKeys.AuditView,
                "audit.claims-evidence.read",
                cancellationToken);
            return AuthorizationFailure(denied);
        }

        if (!currentUser.IsAuthenticated || !currentUser.UserId.HasValue)
        {
            return Failure("AuthenticationRequired", "Authentication is required.");
        }

        var projection = await _projectionBuilder.BuildAsync(
            currentUser.UserId.Value,
            artifactVersionId,
            cancellationToken);
        return projection is null
            ? Failure("ArtifactVersionNotFound", "The artifact version is not available.")
            : Result<AuditClaimsEvidenceResponse>.Success(projection);
    }

    private static Result<AuditClaimsEvidenceResponse> AuthorizationFailure(Result denied) =>
        denied.ErrorDetail is not null
            ? Result<AuditClaimsEvidenceResponse>.Failure(denied.ErrorDetail)
            : Result<AuditClaimsEvidenceResponse>.Failure(denied.Error ?? "Audit access is not permitted.");

    private static Result<AuditClaimsEvidenceResponse> Failure(string code, string message) =>
        Result<AuditClaimsEvidenceResponse>.Failure(new ApplicationErrorDetail(code, message));
}
