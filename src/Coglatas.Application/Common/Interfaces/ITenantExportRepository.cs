using Coglatas.Application.Security.Redaction;
using Coglatas.Domain.Entities;

namespace Coglatas.Application.Common.Interfaces;

public interface ITenantExportRepository
{
    Task<Tenant?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<ExportJob?> GetExportJobAsync(Guid exportJobId, CancellationToken cancellationToken = default);

    Task AddExportJobAsync(ExportJob exportJob, CancellationToken cancellationToken = default);

    Task<byte[]> CreateMetadataZipAsync(
        Guid tenantId,
        AuthorizationContext authorizationContext,
        CancellationToken cancellationToken = default);
}
