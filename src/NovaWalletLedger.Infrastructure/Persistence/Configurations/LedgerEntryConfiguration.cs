using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWalletLedger.Domain.Ledger;

namespace NovaWalletLedger.Infrastructure.Persistence.Configurations;

public sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable("ledger_entries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.WalletId).IsRequired();
        builder.Property(e => e.Type).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(e => e.AmountKobo).IsRequired();
        builder.Property(e => e.BalanceAfterKobo).IsRequired();
        builder.Property(e => e.Currency).IsRequired().HasMaxLength(3);
        builder.Property(e => e.Description).HasMaxLength(500);
        builder.Property(e => e.ExternalReference).HasMaxLength(128);
        builder.Property(e => e.CreatedAtUtc).IsRequired();

        // Newest-first statement pagination is the hot read path.
        builder.HasIndex(e => new { e.WalletId, e.CreatedAtUtc });
        builder.HasIndex(e => e.TransferGroupId);
    }
}
