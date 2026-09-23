using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWalletLedger.Domain.Audit;

namespace NovaWalletLedger.Infrastructure.Persistence.Configurations;

public sealed class AuditLogEntryConfiguration : IEntityTypeConfiguration<AuditLogEntry>
{
    public void Configure(EntityTypeBuilder<AuditLogEntry> builder)
    {
        builder.ToTable("audit_log_entries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.WalletId).IsRequired();
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(e => e.AmountKobo).IsRequired();
        builder.Property(e => e.BalanceBeforeKobo).IsRequired();
        builder.Property(e => e.BalanceAfterKobo).IsRequired();
        builder.Property(e => e.ActorId).IsRequired();
        builder.Property(e => e.CorrelationId).HasMaxLength(64);
        builder.Property(e => e.IdempotencyKey).HasMaxLength(128);
        builder.Property(e => e.Metadata).HasMaxLength(2000);
        builder.Property(e => e.OccurredAtUtc).IsRequired();

        builder.HasIndex(e => new { e.WalletId, e.OccurredAtUtc });

        // Append-only: nothing in this codebase updates or deletes rows in
        // this table. Documented and enforced at the application level; see
        // README "Audit trail immutability" for the DB-role hardening option
        // (REVOKE UPDATE, DELETE) recommended for production.
    }
}
