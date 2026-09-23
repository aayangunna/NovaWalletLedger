using MediatR;
using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Application.Common.Exceptions;
using NovaWalletLedger.Application.Common.Interfaces;

namespace NovaWalletLedger.Application.Features.Wallets.GetBalance;

public sealed record GetBalanceQuery(Guid WalletId) : IRequest<GetBalanceResponse>;

public sealed record GetBalanceResponse(Guid WalletId, Guid CustomerId, long BalanceKobo, string Currency);

public sealed class GetBalanceQueryHandler : IRequestHandler<GetBalanceQuery, GetBalanceResponse>
{
    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public GetBalanceQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<GetBalanceResponse> Handle(GetBalanceQuery request, CancellationToken cancellationToken)
    {
        var wallet = await _db.Wallets.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == request.WalletId, cancellationToken)
            ?? throw new WalletNotFoundException(request.WalletId);

        if (wallet.CustomerId != _currentUser.CustomerId)
            throw new ForbiddenException("You may only view the balance of your own wallet.");

        return new GetBalanceResponse(wallet.Id, wallet.CustomerId, wallet.BalanceKobo, wallet.Currency);
    }
}
