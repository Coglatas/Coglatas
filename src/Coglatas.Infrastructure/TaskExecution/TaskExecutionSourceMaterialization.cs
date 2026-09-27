using Coglatas.Application.Common.Interfaces;
using Coglatas.Application.Projects;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.TaskExecution;

internal static class TaskExecutionSourceMaterialization
{
    internal static IQueryable<Attachment> CurrentTaskAttachments(
        AppDbContext dbContext,
        TaskExecutionRun run) =>
        dbContext.Set<Attachment>()
            .AsNoTracking()
            .Include(attachment => attachment.FileObject)
            .Where(attachment =>
                attachment.TenantId == run.TenantId &&
                attachment.WorkspaceId == run.WorkspaceId &&
                attachment.OwnerType == AttachmentOwnerType.TaskItem &&
                attachment.OwnerId == run.TaskItemId &&
                !attachment.DeletedAt.HasValue &&
                attachment.ScanStatus == FileScanStatus.Clean &&
                attachment.FileObject != null &&
                attachment.FileObject.TenantId == run.TenantId &&
                attachment.FileObject.WorkspaceId == run.WorkspaceId &&
                attachment.FileObject.ProjectId == run.ProjectId &&
                !attachment.FileObject.DeletedAt.HasValue &&
                attachment.FileObject.Status == FileObjectStatus.Active);

    internal static Task<Attachment?> CurrentTaskAttachmentAsync(
        AppDbContext dbContext,
        Guid attachmentId,
        TaskExecutionRun run,
        CancellationToken cancellationToken) =>
        CurrentTaskAttachments(dbContext, run)
            .SingleOrDefaultAsync(
                attachment => attachment.Id == attachmentId,
                cancellationToken);

    internal static async Task<TaskExecutionMaterializedText?> ReadUtf8Async(
        IFileStorageService storage,
        FileObject fileObject,
        string mediaType,
        int maximumForSource,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await storage.OpenReadAsync(fileObject.StorageKey, cancellationToken);
            return await FirstPartyProjectFilesMaterializationV1.ReadUtf8Async(
                stream,
                mediaType,
                maximumForSource,
                cancellationToken);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
