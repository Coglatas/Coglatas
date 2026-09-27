using Coglatas.Application.Search;
using Coglatas.Domain.Entities;
using Coglatas.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Infrastructure.Persistence;

internal static class FileAttachmentQueryFilters
{
    internal static IQueryable<Attachment> WorkspaceFiles(AppDbContext dbContext, Guid workspaceId) =>
        dbContext.Attachments
            .AsNoTracking()
            .Where(attachment =>
                attachment.WorkspaceId == workspaceId &&
                attachment.OwnerType == AttachmentOwnerType.Workspace &&
                attachment.OwnerId == workspaceId &&
                !attachment.DeletedAt.HasValue &&
                attachment.FileObject != null &&
                !attachment.FileObject.DeletedAt.HasValue &&
                attachment.FileObject.Status != FileObjectStatus.Deleted);

    internal static IQueryable<Attachment> ApplyKind(
        IQueryable<Attachment> query,
        FileSearchKind fileKind) => fileKind switch
    {
        FileSearchKind.Image => query.Where(attachment =>
            EF.Functions.ILike(attachment.FileObject!.ContentType, "image/%")),
        FileSearchKind.Pdf => query.Where(attachment =>
            EF.Functions.ILike(attachment.FileObject!.ContentType, "application/pdf%")),
        FileSearchKind.Video => query.Where(attachment =>
            EF.Functions.ILike(attachment.FileObject!.ContentType, "video/%")),
        FileSearchKind.Archive => query.Where(attachment =>
            EF.Functions.ILike(attachment.FileObject!.ContentType, "application/zip%") ||
            EF.Functions.ILike(attachment.FileObject.ContentType, "application/x-zip-compressed%") ||
            EF.Functions.ILike(attachment.FileObject.OriginalFileName, "%.zip")),
        FileSearchKind.Document => query.Where(attachment =>
            !EF.Functions.ILike(attachment.FileObject!.ContentType, "image/%") &&
            !EF.Functions.ILike(attachment.FileObject.ContentType, "application/pdf%") &&
            !EF.Functions.ILike(attachment.FileObject.ContentType, "video/%") &&
            !EF.Functions.ILike(attachment.FileObject.ContentType, "application/zip%") &&
            !EF.Functions.ILike(attachment.FileObject.ContentType, "application/x-zip-compressed%") &&
            !EF.Functions.ILike(attachment.FileObject.OriginalFileName, "%.zip")),
        _ => query
    };
}
