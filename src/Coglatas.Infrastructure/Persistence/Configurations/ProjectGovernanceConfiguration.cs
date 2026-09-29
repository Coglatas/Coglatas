using Coglatas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Coglatas.Infrastructure.Persistence.Configurations;

public sealed class ProjectGovernanceConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.Property(project => project.Visibility)
            .HasConversion<string>()
            .HasMaxLength(40);
        builder.Property(project => project.ActivationState)
            .HasEnumStringConversion()
            .IsRequired();
        builder.Property(project => project.SuspendedFromStatus)
            .HasConversion<string>()
            .HasMaxLength(40);
        builder.Property(project => project.ArchivedFromStatus)
            .HasConversion<string>()
            .HasMaxLength(40);

        builder.HasIndex(project => new { project.TenantId, project.Visibility });
        builder.HasIndex(project => new { project.TenantId, project.ActivationState });

        builder.ToTable("projects", table =>
        {
            table.HasCheckConstraint(
                "CK_projects_visibility",
                "\"Visibility\" IS NULL OR \"Visibility\" IN ('WorkspaceVisible', 'MembersOnly', 'Restricted')");
            table.HasCheckConstraint(
                "CK_projects_activation_state",
                "\"ActivationState\" IN ('LegacyUnknown', 'NeverActivated', 'Activated')");
            table.HasCheckConstraint(
                "CK_projects_activation_provenance",
                "(\"ActivationState\" = 'Activated' AND \"ActivatedAtUtc\" IS NOT NULL AND \"ActivationVersion\" IS NOT NULL AND \"ActivationVersion\" > 0) OR (\"ActivationState\" IN ('LegacyUnknown', 'NeverActivated') AND \"ActivatedAtUtc\" IS NULL AND \"ActivationVersion\" IS NULL)");
        });
    }
}
