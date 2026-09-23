using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NovaWalletLedger.Domain.Wallets;

namespace NovaWalletLedger.Infrastructure.Persistence.Configurations;

public sealed class WalletConfiguration : IEntityTypeConfiguration<Wallet>
{
    public void Configure(EntityTypeBuilder<Wallet> builder)
    {
        builder.ToTable("wallets");
        builder.HasKey(w => w.Id);

        builder.Property(w => w.CustomerId).IsRequired();
        builder.HasIndex(w => w.CustomerId).IsUnique();

        builder.Property(w => w.Currency).IsRequired().HasMaxLength(3);
        builder.Property(w => w.BalanceKobo).IsRequired();
        builder.Property(w => w.DailyOutboundLimitKobo).IsRequired();
        builder.Property(w => w.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        builder.Property(w => w.CreatedAtUtc).IsRequired();

        // Optimistic-concurrency defense-in-depth on top of the pessimistic
        // SELECT ... FOR UPDATE locking used by IWalletLockService.
        builder.Property<uint>("xmin").IsRowVersion().HasColumnName("xmin");

        builder.ToTable(t => t.HasCheckConstraint("ck_wallets_balance_non_negative", "\"balance_kobo\" >= 0"));
    }
}
