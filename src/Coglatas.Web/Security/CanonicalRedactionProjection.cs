using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Security.Redaction;

namespace Coglatas.Web.Security;

/// <summary>
/// Applies one canonical redaction profile at an HTTP response boundary.
/// The application use case owns the authorization decision; this helper fixes
/// the authenticated actor/Tenant request context and never upgrades that state.
/// </summary>
public static class CanonicalRedactionProjection
{
    public static object Apply<T>(
        HttpContext httpContext,
        T source,
        RedactionProfile profile,
        string moduleKey,
        RedactionAuthorizationState authorizationState,
        RedactionPurpose purpose = RedactionPurpose.NormalOperation,
        FieldAccessPolicySnapshot? fieldAccessPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleKey);

        var requestServices = httpContext.RequestServices
            ?? throw new InvalidOperationException(
                "Canonical redaction requires a configured request service provider.");

        var currentUser = requestServices.GetRequiredService<ICurrentUser>();
        if (!currentUser.IsAuthenticated ||
            !currentUser.UserId.HasValue ||
            currentUser.UserId.Value == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Canonical redaction requires an authenticated request actor.");
        }

        var currentTenant = requestServices.GetRequiredService<ICurrentTenant>();
        Guid? tenantId;
        if (currentTenant.IsPlatformScope)
        {
            tenantId = null;
        }
        else if (currentTenant.IsAvailable && currentTenant.TenantId != Guid.Empty)
        {
            tenantId = currentTenant.TenantId;
        }
        else
        {
            throw new InvalidOperationException(
                "Canonical redaction requires a resolved Tenant or explicit platform scope.");
        }

        var context = new AuthorizationContext(
            ActorId: currentUser.UserId.Value,
            TenantId: tenantId,
            ModuleKey: moduleKey,
            Purpose: purpose,
            RequestId: httpContext.TraceIdentifier,
            AuthorizationState: authorizationState,
            FieldAccessPolicy: fieldAccessPolicy);

        var redactionService = requestServices.GetRequiredService<IRedactionService>();
        var result = redactionService.Redact(context, source!, profile);

        if (result.Value is null or RedactedPayload)
        {
            throw new InvalidOperationException(
                "Canonical redaction did not return an endpoint-compatible response projection.");
        }

        return result.Value;
    }
}
