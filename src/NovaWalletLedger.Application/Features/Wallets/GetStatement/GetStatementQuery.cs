using MediatR;
using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Application.Common.Exceptions;
using NovaWalletLedger.Application.Common.Interfaces;
using NovaWalletLedger.Application.Common.Models;
using NovaWalletLedger.Domain.Ledger;

namespace NovaWalletLedger.Application.Features.Wallets.GetStatement;

public sealed record GetStatementQuery(Guid WalletId, int Page, int PageSize) : IRequest<PagedResult<StatementLineDto>>;

public sealed record StatementLineDto(
    Guid Id,
    LedgerEntryType Type,
    long AmountKobo,
    long BalanceAfterKobo,
    string Currency,
    Guid? CounterpartyWalletId,
    Guid TransferGroupId,
    string? Description,
    DateTimeOffset CreatedAtUtc);

public sealed class GetStatementQueryHandler : IRequestHandler<GetStatementQuery, PagedResult<StatementLineDto>>
{
    private const int MaxPageSize = 100;

    private readonly IApplicationDbContext _db;
    private readonly ICurrentUserService _currentUser;

    public GetStatementQueryHandler(IApplicationDbContext db, ICurrentUserService currentUser)
    {
        _db = db;
        _currentUser = currentUser;
    }

    public async Task<PagedResult<StatementLineDto>> Handle(GetStatementQuery request, CancellationToken cancellationToken)
    {
        var wallet = await _db.Wallets.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == request.WalletId, cancellationToken)
            ?? throw new WalletNotFoundException(request.WalletId);

        if (wallet.CustomerId != _currentUser.CustomerId)
            throw new ForbiddenException("You may only view the statement of your own wallet.");

        var page = Math.Max(request.Page, 1);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);

        var query = _db.LedgerEntries.AsNoTracking()
            .Where(e => e.WalletId == request.WalletId)
            .OrderByDescending(e => e.CreatedAtUtc)
            .ThenByDescending(e => e.Id);

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new StatementLineDto(
                e.Id, e.Type, e.AmountKobo, e.BalanceAfterKobo, e.Currency,
                e.CounterpartyWalletId, e.TransferGroupId, e.Description, e.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return new PagedResult<StatementLineDto>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount
        };
    }
}
