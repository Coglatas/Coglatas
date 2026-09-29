using Coglatas.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Coglatas.Infrastructure.Persistence.Configurations;

public sealed class OutboxEventConfiguration : IEntityTypeConfiguration<OutboxEvent>
{
    public void Configure(EntityTypeBuilder<OutboxEvent> builder)
    {
        builder.ToTable("outbox_events", table =>
        {
            table.HasCheckConstraint("CK_outbox_events_attempt_count", "\"AttemptCount\" >= 0");
            table.HasCheckConstraint("CK_outbox_events_payload_schema_version", "\"PayloadSchemaVersion\" > 0");
            table.HasCheckConstraint("CK_outbox_events_delivered_at", "\"Status\" <> 'Delivered' OR \"DeliveredAt\" IS NOT NULL");
            table.HasCheckConstraint("CK_outbox_events_dead_lettered_at", "\"Status\" <> 'DeadLetter' OR \"DeadLetteredAt\" IS NOT NULL");
            table.HasCheckConstraint("CK_outbox_events_lock_fields", "(\"LockedAt\" IS NULL AND \"LockOwner\" IS NULL AND \"LockToken\" IS NULL) OR (\"LockedAt\" IS NOT NULL AND \"LockOwner\" IS NOT NULL AND \"LockToken\" IS NOT NULL)");
        });
        builder.ConfigureAuditableEntity();

        builder.Property(eventItem => eventItem.EventType).HasMaxLength(160).IsRequired();
        builder.Property(eventItem => eventItem.PayloadSchemaVersion).IsRequired();
        builder.Property(eventItem => eventItem.AggregateType).HasMaxLength(100).IsRequired();
        builder.Property(eventItem => eventItem.OccurredAt).IsRequired();
        builder.Property(eventItem => eventItem.PayloadJson).HasColumnType("jsonb").HasMaxLength(65536).IsRequired();
        builder.Property(eventItem => eventItem.RoutingJson).HasColumnType("jsonb").HasMaxLength(8192).IsRequired();
        builder.Property(eventItem => eventItem.CorrelationId).HasMaxLength(200);
        builder.Property(eventItem => eventItem.CausationId).HasMaxLength(200);
        builder.Property(eventItem => eventItem.Status).HasEnumStringConversion().IsRequired();
        builder.Property(eventItem => eventItem.LockOwner).HasMaxLength(160);
        builder.Property(eventItem => eventItem.LastErrorCode).HasMaxLength(100);
        builder.Property(eventItem => eventItem.LastErrorSummary).HasMaxLength(1000);

        builder.HasIndex(eventItem => new { eventItem.Status, eventItem.NextAttemptAt, eventItem.CreatedAt });
        builder.HasIndex(eventItem => new { eventItem.TenantId, eventItem.Status, eventItem.CreatedAt });
        builder.HasIndex(eventItem => new { eventItem.AggregateType, eventItem.AggregateId, eventItem.AggregateVersion });
        builder.HasIndex(eventItem => eventItem.DeliveredAt).HasFilter("\"Status\" = 'Delivered'");
        builder.HasIndex(eventItem => eventItem.DeadLetteredAt).HasFilter("\"Status\" = 'DeadLetter'");
        builder.HasIndex(eventItem => eventItem.LockedAt).HasFilter("\"Status\" = 'Processing'");
    }
}
