using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWalletLedger.Domain.Idempotency;

namespace NovaWalletLedger.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Key).IsRequired().HasMaxLength(128);
        builder.HasIndex(r => r.Key).IsUnique();

        builder.Property(r => r.RequestHash).IsRequired().HasMaxLength(64);
        builder.Property(r => r.WalletId).IsRequired();
        builder.Property(r => r.ResponseBody).HasColumnType("jsonb");
        builder.Property(r => r.CreatedAtUtc).IsRequired();
    }
}
