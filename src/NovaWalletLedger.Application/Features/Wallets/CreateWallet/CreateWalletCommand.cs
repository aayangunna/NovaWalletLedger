using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Application.Common.Exceptions;
using NovaWalletLedger.Application.Common.Interfaces;
using NovaWalletLedger.Domain.Audit;
using NovaWalletLedger.Domain.Wallets;

namespace NovaWalletLedger.Application.Features.Wallets.CreateWallet;

public sealed record CreateWalletCommand(Guid CustomerId) : IRequest<CreateWalletResponse>;

public sealed record CreateWalletResponse(
    Guid WalletId, Guid CustomerId, string Currency, long BalanceKobo, DateTimeOffset CreatedAtUtc);

public sealed class CreateWalletCommandValidator : AbstractValidator<CreateWalletCommand>
{
    public CreateWalletCommandValidator()
    {
        RuleFor(x => x.CustomerId).NotEmpty();
    }
}

public sealed class CreateWalletCommandHandler : IRequestHandler<CreateWalletCommand, CreateWalletResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUserService _currentUser;

    public CreateWalletCommandHandler(IApplicationDbContext db, IDateTimeProvider clock, ICurrentUserService currentUser)
    {
        _db = db;
        _clock = clock;
        _currentUser = currentUser;
    }

    public async Task<CreateWalletResponse> Handle(CreateWalletCommand request, CancellationToken cancellationToken)
    {
        var exists = await _db.Wallets
            .AnyAsync(w => w.CustomerId == request.CustomerId, cancellationToken);

        if (exists)
            throw new WalletAlreadyExistsException(request.CustomerId);

        var now = _clock.UtcNow;
        var wallet = Wallet.Create(request.CustomerId, now);

        _db.Wallets.Add(wallet);
        _db.AuditLogEntries.Add(AuditLogEntry.Create(
            walletId: wallet.Id,
            action: AuditAction.WalletCreated,
            amountKobo: 0,
            balanceBeforeKobo: 0,
            balanceAfterKobo: 0,
            actorId: _currentUser.CustomerId,
            occurredAtUtc: now,
            correlationId: _currentUser.CorrelationId));

        await _db.SaveChangesAsync(cancellationToken);

        return new CreateWalletResponse(wallet.Id, wallet.CustomerId, wallet.Currency, wallet.BalanceKobo, wallet.CreatedAtUtc);
    }
}
