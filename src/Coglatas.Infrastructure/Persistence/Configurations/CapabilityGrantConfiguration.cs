using Coglatas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Coglatas.Infrastructure.Persistence.Configurations;

public sealed class CapabilityGrantConfiguration : IEntityTypeConfiguration<CapabilityGrant>
{
    public void Configure(EntityTypeBuilder<CapabilityGrant> builder)
    {
        builder.ToTable("capability_grants", table =>
        {
            table.HasCheckConstraint(
                "CK_capability_grants_scope_shape",
                "(\"ScopeType\" = 'Tenant' AND \"ScopeId\" = \"TenantId\") OR (\"ScopeType\" = 'Workspace' AND \"ScopeId\" IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_capability_grants_version_positive",
                "\"VersionNo\" > 0");
        });
        builder.ConfigureAuditableEntity();

        builder.Property(grant => grant.CapabilityKey).HasMaxLength(120).IsRequired();
        builder.Property(grant => grant.ScopeType).HasEnumStringConversion().IsRequired();
        builder.Property(grant => grant.GrantedAt).IsRequired();
        builder.Property(grant => grant.VersionNo).IsRequired().HasDefaultValue(1L).IsConcurrencyToken();

        builder.HasIndex(grant => grant.SubjectUserId);
        builder.HasIndex(grant => grant.GrantedByUserId);
        builder.HasIndex(grant => grant.ExpiresAt);
        builder.HasIndex(grant => grant.RevokedAt);
        builder.HasIndex(grant => new
        {
            grant.TenantId,
            grant.SubjectUserId,
            grant.CapabilityKey,
            grant.ScopeType,
            grant.ScopeId
        }).IsUnique();

        builder.HasOne(grant => grant.SubjectUser)
            .WithMany()
            .HasForeignKey(grant => grant.SubjectUserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(grant => grant.GrantedByUser)
            .WithMany()
            .HasForeignKey(grant => grant.GrantedByUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
