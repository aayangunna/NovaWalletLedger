using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Application.Common.Exceptions;
using NovaWalletLedger.Application.Common.Interfaces;
using NovaWalletLedger.Domain.Audit;
using NovaWalletLedger.Domain.Common;
using NovaWalletLedger.Domain.Ledger;

namespace NovaWalletLedger.Application.Features.Wallets.CreditWallet;

/// <summary>
/// Simulates an inbound NIBSS NIP transfer (or diaspora remittance) landing
/// in a customer's wallet. In a real deployment this would be triggered by a
/// webhook from the NIP rail / remittance partner, not initiated by the
/// customer — this simplified service has no role tiers, so it is scoped by
/// wallet ownership instead: a customer may only credit their own wallet.
/// </summary>
public sealed record CreditWalletCommand(
    Guid WalletId, long AmountKobo, string? ExternalReference, string? Description) : IRequest<CreditWalletResponse>;

public sealed record CreditWalletResponse(Guid WalletId, long BalanceKobo, string Currency, Guid LedgerEntryId);

public sealed class CreditWalletCommandValidator : AbstractValidator<CreditWalletCommand>
{
    public CreditWalletCommandValidator()
    {
        RuleFor(x => x.WalletId).NotEmpty();
        RuleFor(x => x.AmountKobo).GreaterThan(0);
    }
}

public sealed class CreditWalletCommandHandler : IRequestHandler<CreditWalletCommand, CreditWalletResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWalletLockService _lockService;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUserService _currentUser;

    public CreditWalletCommandHandler(
        IApplicationDbContext db, IUnitOfWork unitOfWork, IWalletLockService lockService,
        IDateTimeProvider clock, ICurrentUserService currentUser)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _lockService = lockService;
        _clock = clock;
        _currentUser = currentUser;
    }

    public async Task<CreditWalletResponse> Handle(CreditWalletCommand request, CancellationToken cancellationToken)
    {
        var ownerCustomerId = await _db.Wallets.AsNoTracking()
            .Where(w => w.Id == request.WalletId)
            .Select(w => (Guid?)w.CustomerId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new WalletNotFoundException(request.WalletId);

        if (ownerCustomerId != _currentUser.CustomerId)
            throw new ForbiddenException("You may only credit your own wallet.");

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        var wallet = await _lockService.LockForUpdateAsync(request.WalletId, cancellationToken);
        var now = _clock.UtcNow;
        var balanceBefore = wallet.BalanceKobo;

        var amount = new Money(request.AmountKobo, wallet.Currency);
        wallet.Credit(amount);

        var ledgerEntry = LedgerEntry.ForCredit(
            wallet.Id, amount, wallet.BalanceKobo, now,
            description: request.Description, externalReference: request.ExternalReference);

        _db.LedgerEntries.Add(ledgerEntry);
        _db.AuditLogEntries.Add(AuditLogEntry.Create(
            wallet.Id, AuditAction.Credit, amount.AmountKobo, balanceBefore, wallet.BalanceKobo,
            _currentUser.CustomerId, now, _currentUser.CorrelationId,
            metadata: request.ExternalReference));

        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreditWalletResponse(wallet.Id, wallet.BalanceKobo, wallet.Currency, ledgerEntry.Id);
    }
}
