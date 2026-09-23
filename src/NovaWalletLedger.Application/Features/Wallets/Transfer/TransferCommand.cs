using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using NovaWalletLedger.Application.Common.Exceptions;
using NovaWalletLedger.Application.Common.Interfaces;
using NovaWalletLedger.Domain.Audit;
using NovaWalletLedger.Domain.Common;
using NovaWalletLedger.Domain.Ledger;
using NovaWalletLedger.Domain.Outbox;

namespace NovaWalletLedger.Application.Features.Wallets.Transfer;

public sealed record TransferCommand(
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    string IdempotencyKey,
    string? Description) : IRequest<TransferResponse>;

public sealed record TransferResponse(
    Guid TransferId,
    Guid SourceWalletId,
    Guid DestinationWalletId,
    long AmountKobo,
    long SourceBalanceKobo,
    long DestinationBalanceKobo,
    DateTimeOffset CreatedAtUtc);

public sealed class TransferCommandValidator : AbstractValidator<TransferCommand>
{
    public TransferCommandValidator()
    {
        RuleFor(x => x.SourceWalletId).NotEmpty();
        RuleFor(x => x.DestinationWalletId).NotEmpty();
        RuleFor(x => x.AmountKobo).GreaterThan(0);
        RuleFor(x => x.IdempotencyKey).NotEmpty().MaximumLength(128);
        RuleFor(x => x)
            .Must(x => x.SourceWalletId != x.DestinationWalletId)
            .WithMessage("Cannot transfer a wallet to itself.")
            .WithName("destinationWalletId");
    }
}

public sealed class TransferCommandHandler : IRequestHandler<TransferCommand, TransferResponse>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IApplicationDbContext _db;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IWalletLockService _lockService;
    private readonly IIdempotencyService _idempotency;
    private readonly IDateTimeProvider _clock;
    private readonly ICurrentUserService _currentUser;

    public TransferCommandHandler(
        IApplicationDbContext db, IUnitOfWork unitOfWork, IWalletLockService lockService,
        IIdempotencyService idempotency, IDateTimeProvider clock, ICurrentUserService currentUser)
    {
        _db = db;
        _unitOfWork = unitOfWork;
        _lockService = lockService;
        _idempotency = idempotency;
        _clock = clock;
        _currentUser = currentUser;
    }

    public async Task<TransferResponse> Handle(TransferCommand request, CancellationToken cancellationToken)
    {
        var sourceCustomerId = await _db.Wallets.AsNoTracking()
            .Where(w => w.Id == request.SourceWalletId)
            .Select(w => (Guid?)w.CustomerId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new WalletNotFoundException(request.SourceWalletId);

        if (sourceCustomerId != _currentUser.CustomerId)
            throw new ForbiddenException("You may only transfer from your own wallet.");

        var requestHash = ComputeRequestHash(request);

        await using var transaction = await _unitOfWork.BeginTransactionAsync(cancellationToken);

        var claim = await _idempotency.TryClaimAsync(
            request.IdempotencyKey, requestHash, request.SourceWalletId, cancellationToken);

        switch (claim.Outcome)
        {
            case IdempotencyClaimOutcome.KeyReuseConflict:
                await transaction.RollbackAsync(cancellationToken);
                throw new IdempotencyKeyConflictException(request.IdempotencyKey);

            case IdempotencyClaimOutcome.InFlight:
                await transaction.RollbackAsync(cancellationToken);
                throw new IdempotentRequestInFlightException(request.IdempotencyKey);

            case IdempotencyClaimOutcome.Replay:
                await transaction.RollbackAsync(cancellationToken);
                if (claim.StoredStatusCode is >= 200 and < 300)
                    return JsonSerializer.Deserialize<TransferResponse>(claim.StoredResponseBody!, JsonOptions)!;
                throw new ReplayedFailureException(claim.StoredStatusCode!.Value, claim.StoredResponseBody!);
        }

        // Outcome.Claimed: this request owns the key. Process the transfer.
        var now = _clock.UtcNow;

        try
        {
            var (source, destination) = await _lockService.LockPairForUpdateAsync(
                request.SourceWalletId, request.DestinationWalletId, cancellationToken);

            var amount = new Money(request.AmountKobo, source.Currency);
            var sourceBalanceBefore = source.BalanceKobo;
            var destinationBalanceBefore = destination.BalanceKobo;

            var debitedToday = await _lockService.GetDebitedTodayKoboAsync(source.Id, now, cancellationToken);

            source.Debit(amount, debitedToday);
            destination.Credit(new Money(amount.AmountKobo, destination.Currency));

            var transferGroupId = Guid.NewGuid();

            var debitEntry = LedgerEntry.ForDebit(
                source.Id, amount, source.BalanceKobo, now,
                counterpartyWalletId: destination.Id, transferGroupId: transferGroupId, description: request.Description);
            var creditEntry = LedgerEntry.ForCredit(
                destination.Id, amount, destination.BalanceKobo, now,
                counterpartyWalletId: source.Id, transferGroupId: transferGroupId, description: request.Description);

            _db.LedgerEntries.AddRange(debitEntry, creditEntry);

            _db.AuditLogEntries.Add(AuditLogEntry.Create(
                source.Id, AuditAction.Debit, amount.AmountKobo, sourceBalanceBefore, source.BalanceKobo,
                _currentUser.CustomerId, now, _currentUser.CorrelationId, request.IdempotencyKey));
            _db.AuditLogEntries.Add(AuditLogEntry.Create(
                destination.Id, AuditAction.Credit, amount.AmountKobo, destinationBalanceBefore, destination.BalanceKobo,
                _currentUser.CustomerId, now, _currentUser.CorrelationId, request.IdempotencyKey));

            var response = new TransferResponse(
                transferGroupId, source.Id, destination.Id, amount.AmountKobo,
                source.BalanceKobo, destination.BalanceKobo, now);

            _db.OutboxMessages.Add(OutboxMessage.Create(
                "TransferCompleted", JsonSerializer.Serialize(response, JsonOptions), now));

            var responseJson = JsonSerializer.Serialize(response, JsonOptions);
            await _idempotency.CompleteAsync(request.IdempotencyKey, 200, responseJson, cancellationToken);

            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return response;
        }
        catch (DomainException domainEx)
        {
            // Deterministic business rejection (insufficient funds, daily limit, ...):
            // persist the rejection audit trail and the idempotency outcome so a
            // replay of this exact key+payload returns the same failure without
            // re-attempting the transfer, then let the API layer render it.
            var (statusCode, title) = DomainExceptionMapper.Map(domainEx);
            var problem = JsonSerializer.Serialize(new
            {
                type = "about:blank",
                title,
                status = statusCode,
                detail = domainEx.Message
            }, JsonOptions);

            _db.AuditLogEntries.Add(AuditLogEntry.Create(
                request.SourceWalletId,
                domainEx is DailyLimitExceededException
                    ? AuditAction.TransferRejectedDailyLimit
                    : AuditAction.TransferRejectedInsufficientFunds,
                request.AmountKobo, 0, 0,
                _currentUser.CustomerId, now, _currentUser.CorrelationId, request.IdempotencyKey,
                metadata: domainEx.Message));

            await _idempotency.CompleteAsync(request.IdempotencyKey, statusCode, problem, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            throw;
        }
        catch
        {
            // Infrastructure/unexpected failure: do not claim the idempotency
            // key permanently — roll everything back so a legitimate retry
            // with the same key can succeed later.
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private static string ComputeRequestHash(TransferCommand request)
    {
        var canonical = $"{request.SourceWalletId:N}|{request.DestinationWalletId:N}|{request.AmountKobo}|{request.Description}";
        var bytes = Encoding.UTF8.GetBytes(canonical);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
