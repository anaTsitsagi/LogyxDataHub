using System.Text.Json;
using DataHub.Application;
using DataHub.Application.Processing;
using DataHub.Infrastructure.Messaging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace DataHub.Worker;

/// <summary>
/// Consumes processing jobs one at a time (prefetch 1, manual ack). A message is acknowledged only
/// after the job's outcome is saved in the database, so a crash leads to redelivery, not loss.
/// </summary>
public sealed class JobConsumer(RabbitMqConnection connection, IServiceScopeFactory scopes, ILogger<JobConsumer> logger) : BackgroundService
{
    private static readonly TimeSpan UnexpectedErrorDelay = TimeSpan.FromSeconds(15);
    private Task _inFlight = Task.CompletedTask;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = await ConnectAsync(stoppingToken);
        if (channel is null) return;

        await channel.BasicQosAsync(prefetchSize: 0, prefetchCount: 1, global: false, stoppingToken);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, delivery) =>
        {
            _inFlight = HandleAsync(channel, delivery, stoppingToken);
            return _inFlight;
        };
        await channel.BasicConsumeAsync(connection.Options.Queue, autoAck: false, consumer, stoppingToken);
        logger.LogInformation("Consuming jobs from queue {Queue}", connection.Options.Queue);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }

        // Let the current job return itself to the queue before the channel closes.
        await _inFlight;
        await channel.CloseAsync(CancellationToken.None);
        await channel.DisposeAsync();
    }

    /// <summary>Retries until RabbitMQ is reachable, so the worker can start before the broker.</summary>
    private async Task<IChannel?> ConnectAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                return await connection.CreateChannelAsync(publisherConfirms: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "RabbitMQ not reachable; retrying in {Delay}", delay);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return null; }
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
            }
        }
        return null;
    }

    private async Task HandleAsync(IChannel channel, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        ProcessingJobMessage? message = null;
        try
        {
            message = JsonSerializer.Deserialize<ProcessingJobMessage>(delivery.Body.Span, RabbitMqJobPublisher.Json);
        }
        catch (JsonException)
        {
        }

        if (message is null || message.JobId == Guid.Empty)
        {
            logger.LogError("Unreadable message {MessageId} moved to the dead-letter queue", delivery.BasicProperties.MessageId);
            await channel.BasicRejectAsync(delivery.DeliveryTag, requeue: false, CancellationToken.None);
            return;
        }

        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(message, ct);
            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, CancellationToken.None);
            logger.LogInformation("Job {JobId} message acknowledged: {Outcome}", message.JobId, outcome);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // Typically the database is unreachable before the job could even be claimed. Pause, then let
            // the broker redeliver; the quorum queue's delivery limit dead-letters a message that never succeeds.
            logger.LogError(ex, "Job {JobId} could not be handled; redelivering in {Delay}", message.JobId, UnexpectedErrorDelay);
            try { await Task.Delay(UnexpectedErrorDelay, ct); } catch (OperationCanceledException) { }
            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        }
    }
}
