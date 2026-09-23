using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using NovaWalletLedger.Application.Common.Exceptions;
using NovaWalletLedger.Domain.Common;

namespace NovaWalletLedger.Api.Middleware;

/// <summary>
/// Single place that turns every exception the app can throw into an RFC
/// 7807 Problem Details response, so API consumers get one consistent error
/// shape regardless of which layer raised the failure.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
    {
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // A replayed failed idempotent request is not a new failure — it's a
        // stored outcome being returned verbatim — so it's handled (and
        // logged) separately, before the generic Map()/log-as-error path
        // below would otherwise misclassify it as an unhandled exception.
        if (exception is ReplayedFailureException replay)
        {
            _logger.LogInformation(
                "Replaying stored idempotent failure (status {StatusCode}) for {Method} {Path}",
                replay.StatusCode, httpContext.Request.Method, httpContext.Request.Path);

            httpContext.Response.StatusCode = replay.StatusCode;
            httpContext.Response.ContentType = "application/problem+json";
            await httpContext.Response.WriteAsync(replay.ProblemDetailsJson, cancellationToken);
            return true;
        }

        var (statusCode, title, detail, extensions) = Map(exception);

        if (statusCode >= 500)
            _logger.LogError(exception, "Unhandled exception processing {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        else
            _logger.LogWarning(exception, "{Title}: {Detail}", title, detail);

        if (exception is ValidationException validationException)
        {
            var problem = new ValidationProblemDetails(
                validationException.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "One or more validation errors occurred.",
                Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1",
                Instance = httpContext.Request.Path
            };
            problem.Extensions["traceId"] = httpContext.TraceIdentifier;

            httpContext.Response.StatusCode = problem.Status.Value;
            await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken: cancellationToken);
            return true;
        }

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Type = $"https://httpstatuses.io/{statusCode}",
            Instance = httpContext.Request.Path
        };
        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;
        foreach (var (key, value) in extensions)
            problemDetails.Extensions[key] = value;

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, cancellationToken: cancellationToken);
        return true;
    }

    private static (int StatusCode, string Title, string Detail, Dictionary<string, object?> Extensions) Map(Exception exception) =>
        exception switch
        {
            DomainException domainEx => MapDomain(domainEx),
            WalletNotFoundException ex => (StatusCodes.Status404NotFound, "Wallet not found", ex.Message, []),
            WalletAlreadyExistsException ex => (StatusCodes.Status409Conflict, "Wallet already exists", ex.Message, []),
            ForbiddenException ex => (StatusCodes.Status403Forbidden, "Forbidden", ex.Message, []),
            IdempotencyKeyRequiredException ex => (StatusCodes.Status400BadRequest, "Idempotency-Key required", ex.Message, []),
            IdempotencyKeyConflictException ex => (StatusCodes.Status409Conflict, "Idempotency-Key conflict", ex.Message, []),
            IdempotentRequestInFlightException ex => (StatusCodes.Status409Conflict, "Request already in progress", ex.Message, []),
            _ => (StatusCodes.Status500InternalServerError, "An unexpected error occurred", "Please retry the request or contact support.", [])
        };

    private static (int, string, string, Dictionary<string, object?>) MapDomain(DomainException ex)
    {
        var (statusCode, title) = DomainExceptionMapper.Map(ex);

        var extensions = new Dictionary<string, object?>();
        switch (ex)
        {
            case InsufficientFundsException ife:
                extensions["walletId"] = ife.WalletId;
                extensions["requestedKobo"] = ife.RequestedKobo;
                extensions["availableKobo"] = ife.AvailableKobo;
                break;
            case DailyLimitExceededException dle:
                extensions["walletId"] = dle.WalletId;
                extensions["requestedKobo"] = dle.RequestedKobo;
                extensions["alreadyUsedKobo"] = dle.AlreadyUsedKobo;
                extensions["limitKobo"] = dle.LimitKobo;
                break;
        }

        return (statusCode, title, ex.Message, extensions);
    }
}
