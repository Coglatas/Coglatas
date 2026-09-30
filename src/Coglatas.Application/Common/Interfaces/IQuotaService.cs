
namespace Coglatas.Application.Common.Interfaces;

public sealed record TenantUsageSnapshot(
    Guid TenantId,
    int ActiveUserCount,
    int TotalUserCount,
    int ProjectCount,
    int TaskCount,
    int FileCount,
    long StorageUsedBytes,
    int ApiRequestCount);

public interface IQuotaService
{
    Task<TenantUsageSnapshot> GetCurrentUsageAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<Result> CanCreateUserAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<Result> CanCreateProjectAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task<Result> CanUploadFileAsync(Guid tenantId, long fileSizeBytes, CancellationToken cancellationToken = default);

    Task<Result> CanInviteGuestAsync(Guid tenantId, CancellationToken cancellationToken = default);

    Task RecordApiRequestAsync(Guid tenantId, CancellationToken cancellationToken = default);
}
