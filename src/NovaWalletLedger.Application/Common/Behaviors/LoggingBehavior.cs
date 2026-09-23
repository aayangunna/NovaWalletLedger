using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;
using NovaWalletLedger.Application.Common.Interfaces;

namespace NovaWalletLedger.Application.Common.Behaviors;

public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;
    private readonly ICurrentUserService _currentUser;

    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger, ICurrentUserService currentUser)
    {
        _logger = logger;
        _currentUser = currentUser;
    }

    public async Task<TResponse> Handle(
        TRequest request, RequestHandlerDelegate<TResponse> next, CancellationToken cancellationToken)
    {
        var requestName = typeof(TRequest).Name;
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "Handling {RequestName} for customer {CustomerId} (correlation {CorrelationId})",
            requestName, _currentUser.CustomerId, _currentUser.CorrelationId);

        try
        {
            var response = await next();
            _logger.LogInformation(
                "Handled {RequestName} in {ElapsedMs}ms", requestName, sw.ElapsedMilliseconds);
            return response;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "{RequestName} failed after {ElapsedMs}ms: {Message}", requestName, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }
}
