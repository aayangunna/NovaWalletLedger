using MediatR;
using NovaWalletLedger.Api.Contracts;
using NovaWalletLedger.Application.Common.Exceptions;
using NovaWalletLedger.Application.Common.Interfaces;
using NovaWalletLedger.Application.Features.Wallets.CreateWallet;
using NovaWalletLedger.Application.Features.Wallets.CreditWallet;
using NovaWalletLedger.Application.Features.Wallets.GetBalance;
using NovaWalletLedger.Application.Features.Wallets.GetStatement;
using NovaWalletLedger.Application.Features.Wallets.Transfer;

namespace NovaWalletLedger.Api.Endpoints.Wallets;

public static class WalletEndpoints
{
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/wallets")
            .WithTags("Wallets")
            .RequireAuthorization();

        group.MapPost("/", CreateWallet)
            .WithName("CreateWallet")
            .WithSummary("Create a wallet for the authenticated customer (starting balance zero)")
            .Produces<CreateWalletResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict);

        group.MapGet("/{walletId:guid}/balance", GetBalance)
            .WithName("GetWalletBalance")
            .WithSummary("Get the current balance of a wallet, in kobo")
            .Produces<GetBalanceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{walletId:guid}/credit", CreditWallet)
            .WithName("CreditWallet")
            .WithSummary("Credit your own wallet (simulates an inbound NIBSS NIP transfer / diaspora remittance)")
            .Produces<CreditWalletResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        group.MapPost("/{walletId:guid}/transfer", Transfer)
            .WithName("TransferFunds")
            .WithSummary("Atomically transfer funds between two wallets. Requires an Idempotency-Key header.")
            .RequireRateLimiting(RateLimiterPolicies.Transfer)
            .Produces<TransferResponse>()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status404NotFound);

        group.MapGet("/{walletId:guid}/statement", GetStatement)
            .WithName("GetWalletStatement")
            .WithSummary("Paginated transaction history for a wallet, newest first")
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> CreateWallet(
        ISender sender, ICurrentUserService currentUser, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CreateWalletCommand(currentUser.CustomerId), cancellationToken);
        return Results.Created($"/api/wallets/{result.WalletId}/balance", result);
    }

    private static async Task<IResult> GetBalance(
        Guid walletId, ISender sender, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetBalanceQuery(walletId), cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> CreditWallet(
        Guid walletId, CreditWalletRequest body, ISender sender, CancellationToken cancellationToken)
    {
        var command = new CreditWalletCommand(walletId, body.AmountKobo, body.ExternalReference, body.Description);
        var result = await sender.Send(command, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> Transfer(
        Guid walletId, TransferRequest body, HttpRequest httpRequest, ISender sender, CancellationToken cancellationToken)
    {
        if (!httpRequest.Headers.TryGetValue("Idempotency-Key", out var keyValues)
            || string.IsNullOrWhiteSpace(keyValues.ToString()))
        {
            throw new IdempotencyKeyRequiredException();
        }

        var command = new TransferCommand(
            walletId, body.DestinationWalletId, body.AmountKobo, keyValues.ToString(), body.Description);

        var result = await sender.Send(command, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> GetStatement(
        Guid walletId, ISender sender, CancellationToken cancellationToken, int page = 1, int pageSize = 20)
    {
        var result = await sender.Send(new GetStatementQuery(walletId, page, pageSize), cancellationToken);
        return Results.Ok(result);
    }
}
