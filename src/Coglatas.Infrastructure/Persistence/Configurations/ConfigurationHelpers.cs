using Coglatas.Domain.Common;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Coglatas.Infrastructure.Persistence.Configurations;

internal static class ConfigurationHelpers
{
    public static void ConfigureEntity<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : Entity
    {
        builder.HasKey(entity => entity.Id);
    }

    public static void ConfigureAuditableEntity<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : AuditableEntity
    {
        builder.ConfigureEntity();
        builder.Property(entity => entity.CreatedAt).IsRequired();
        builder.Property(entity => entity.UpdatedAt);
        builder.HasIndex(entity => entity.CreatedAt);
    }

    public static void ConfigureSoftDeletableEntity<TEntity>(this EntityTypeBuilder<TEntity> builder)
        where TEntity : SoftDeletableEntity
    {
        builder.ConfigureAuditableEntity();
        builder.Property(entity => entity.DeletedAt);
        builder.Property(entity => entity.DeletedByUserId);
        builder.Property(entity => entity.DeleteReason).HasMaxLength(500);
        builder.HasIndex(entity => entity.DeletedAt);
        builder.HasIndex(entity => entity.DeletedByUserId);
    }

    public static PropertyBuilder<TEnum> HasEnumStringConversion<TEnum>(
        this PropertyBuilder<TEnum> builder,
        int maxLength = 40)
        where TEnum : struct, Enum
    {
        return builder.HasConversion<string>().HasMaxLength(maxLength);
    }
}
