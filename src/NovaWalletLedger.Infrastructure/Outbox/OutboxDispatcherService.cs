using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NovaWalletLedger.Infrastructure.Persistence;

namespace NovaWalletLedger.Infrastructure.Outbox;

/// <summary>
/// Polls the transactional outbox and "publishes" pending messages.
/// Messages are written in the same DB transaction as the business mutation
/// they describe (see TransferCommandHandler), so this dispatcher gives
/// at-least-once delivery without a distributed transaction across the
/// database and a message broker.
///
/// This reference implementation publishes by structured-logging the event
/// (visible in the container logs / any log sink wired to Serilog). Swapping
/// the body of <see cref="PublishAsync"/> for a real broker client
/// (Kafka/SNS/RabbitMQ producer) is the only change needed to go to
/// production — the polling, batching, and failure-isolation logic does not
/// need to change.
/// </summary>
public sealed class OutboxDispatcherService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private const int BatchSize = 50;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OutboxDispatcherService> _logger;

    public OutboxDispatcherService(IServiceScopeFactory scopeFactory, ILogger<OutboxDispatcherService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);

        do
        {
            try
            {
                await DispatchPendingAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Outbox dispatch cycle failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task DispatchPendingAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var pending = await db.OutboxMessages
            .Where(m => m.ProcessedAtUtc == null)
            .OrderBy(m => m.OccurredAtUtc)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        if (pending.Count == 0) return;

        foreach (var message in pending)
        {
            try
            {
                PublishAsync(message.Type, message.PayloadJson);
                message.MarkProcessed(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                message.MarkFailed(ex.Message);
                _logger.LogWarning(ex, "Failed to publish outbox message {MessageId} ({Type})", message.Id, message.Type);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private void PublishAsync(string type, string payloadJson)
    {
        _logger.LogInformation("Publishing event {EventType}: {Payload}", type, payloadJson);
    }
}
