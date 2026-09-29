using Coglatas.Application.Common.Interfaces;
using Coglatas.Domain.Entities;
using Coglatas.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Coglatas.Tests.Files;

public sealed class FileFolderModelTests
{
    [Fact]
    public void Model_UsesAuthoritativeFolderAndOptimisticPlacementContracts()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"file-folder-model-{Guid.NewGuid():N}")
            .Options;
        using var db = new AppDbContext(options, new NoTenant());

        var folder = db.Model.FindEntityType(typeof(FileFolder));
        Assert.NotNull(folder);
        Assert.True(folder!.FindProperty(nameof(FileFolder.Version))!.IsConcurrencyToken);
        Assert.Equal(DeleteBehavior.Restrict,
            folder.FindNavigation(nameof(FileFolder.ParentFolder))!.ForeignKey.DeleteBehavior);
        Assert.Equal(DeleteBehavior.Restrict,
            folder.FindNavigation(nameof(FileFolder.Workspace))!.ForeignKey.DeleteBehavior);

        var root = db.Model.FindEntityType(typeof(FileFolderRootState));
        Assert.NotNull(root);
        Assert.True(root!.FindProperty(nameof(FileFolderRootState.Version))!.IsConcurrencyToken);
        Assert.Contains(root.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Count == 1 &&
            index.Properties[0].Name == nameof(FileFolderRootState.WorkspaceId));
        Assert.Equal(DeleteBehavior.Restrict,
            root.FindNavigation(nameof(FileFolderRootState.Workspace))!.ForeignKey.DeleteBehavior);

        var placement = db.Model.FindEntityType(typeof(FileFolderPlacement));
        Assert.NotNull(placement);
        Assert.True(placement!.FindProperty(nameof(FileFolderPlacement.Version))!.IsConcurrencyToken);
        Assert.Contains(placement.GetIndexes(), index =>
            index.IsUnique &&
            index.Properties.Count == 1 &&
            index.Properties[0].Name == nameof(FileFolderPlacement.FileObjectId));
        Assert.Equal(DeleteBehavior.Restrict,
            placement.FindNavigation(nameof(FileFolderPlacement.Folder))!.ForeignKey.DeleteBehavior);
    }

    private sealed class NoTenant : ICurrentTenant
    {
        public Guid TenantId => Guid.Empty;
        public bool IsAvailable => false;
        public string? TenantSlug => null;
        public bool IsPlatformScope => false;
    }
}
