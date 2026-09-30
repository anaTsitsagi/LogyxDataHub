using System.Text.Json;
using DataHub.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace DataHub.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string Section = "RabbitMq";

    /// <summary>amqp(s)://user:pass@host:port/vhost</summary>
    public string Uri { get; set; } = "";
    public string Exchange { get; set; } = "datahub.jobs";
    public string Queue { get; set; } = "datahub.processing";
    public string RoutingKey { get; set; } = "process";
    public string DeadLetterExchange { get; set; } = "datahub.jobs.dlx";
    public string DeadLetterQueue { get; set; } = "datahub.processing.dead";

    /// <summary>"quorum" (replicated, recommended) or "classic".</summary>
    public string QueueType { get; set; } = "quorum";
    public int DeliveryLimit { get; set; } = 10;
    public string ClientName { get; set; } = "datahub";
}

/// <summary>Declares the processing topology. Idempotent; both publisher and worker call it.</summary>
public static class RabbitMqTopology
{
    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions o, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(o.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(o.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: new Dictionary<string, object?> { ["x-queue-type"] = o.QueueType }, cancellationToken: ct);
        await channel.QueueBindAsync(o.DeadLetterQueue, o.DeadLetterExchange, "", cancellationToken: ct);

        await channel.ExchangeDeclareAsync(o.Exchange, ExchangeType.Direct, durable: true, cancellationToken: ct);
        var arguments = new Dictionary<string, object?>
        {
            ["x-queue-type"] = o.QueueType,
            ["x-dead-letter-exchange"] = o.DeadLetterExchange,
        };
        // A message that keeps crashing the worker is dead-lettered instead of redelivered forever.
        if (o.QueueType == "quorum") arguments["x-delivery-limit"] = o.DeliveryLimit;
        await channel.QueueDeclareAsync(o.Queue, durable: true, exclusive: false, autoDelete: false, arguments, cancellationToken: ct);
        await channel.QueueBindAsync(o.Queue, o.Exchange, o.RoutingKey, cancellationToken: ct);
    }
}

/// <summary>Holds one long-lived connection per process, as RabbitMQ recommends.</summary>
public sealed class RabbitMqConnection(IOptions<RabbitMqOptions> options) : IAsyncDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private IConnection? _connection;
    private bool _topologyDeclared;

    public RabbitMqOptions Options => options.Value;

    public async Task<IChannel> CreateChannelAsync(bool publisherConfirms, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_connection is not { IsOpen: true })
            {
                if (string.IsNullOrWhiteSpace(Options.Uri)) throw new InvalidOperationException("RabbitMq:Uri is not configured.");
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(Options.Uri),
                    ClientProvidedName = Options.ClientName,
                    AutomaticRecoveryEnabled = true,
                };
                _connection = await factory.CreateConnectionAsync(ct);
                _topologyDeclared = false;
            }

            var channel = await _connection.CreateChannelAsync(
                new CreateChannelOptions(publisherConfirmationsEnabled: publisherConfirms, publisherConfirmationTrackingEnabled: publisherConfirms), ct);
            if (!_topologyDeclared)
            {
                await RabbitMqTopology.DeclareAsync(channel, Options, ct);
                _topologyDeclared = true;
            }
            return channel;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null) await _connection.DisposeAsync();
        _lock.Dispose();
    }
}

public sealed class RabbitMqJobPublisher(RabbitMqConnection connection, ILogger<RabbitMqJobPublisher> logger) : IJobPublisher
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task PublishAsync(ProcessingJobMessage message, CancellationToken ct)
    {
        // Upload completions are rare, so a short-lived channel per publish keeps this thread-safe and simple.
        await using var channel = await connection.CreateChannelAsync(publisherConfirms: true, ct);
        var properties = new BasicProperties
        {
            Persistent = true,
            ContentType = "application/json",
            MessageId = message.JobId.ToString(),
            CorrelationId = message.CorrelationId,
            Type = "datahub.processing-job.v1",
        };
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);

        // With confirmation tracking enabled this completes only after the broker has confirmed the message.
        await channel.BasicPublishAsync(connection.Options.Exchange, connection.Options.RoutingKey, mandatory: true, properties, body, ct);
        logger.LogInformation("Published job {JobId} ({Bytes} bytes)", message.JobId, body.Length);
    }
}
