using System.Collections.Concurrent;
using DataHub.Application;

namespace DataHub.Infrastructure.Tests;

public sealed class FakeSms : ISmsSender
{
    public List<(string Phone, string Text)> Sent { get; } = [];
    public Task SendAsync(string phone, string text, CancellationToken ct) { Sent.Add((phone, text)); return Task.CompletedTask; }
}

public sealed class FakeEmail : IEmailSender
{
    public List<(string To, string Subject, string Body)> Sent { get; } = [];
    public Task SendAsync(string to, string subject, string body, CancellationToken ct) { Sent.Add((to, subject, body)); return Task.CompletedTask; }
}

public sealed class CapturingPublisher : IJobPublisher
{
    public List<ProcessingJobMessage> Published { get; } = [];
    public Task PublishAsync(ProcessingJobMessage message, CancellationToken ct) { Published.Add(message); return Task.CompletedTask; }
}

/// <summary>S3 stand-in with multipart semantics.</summary>
public sealed class InMemoryFileStore : IFileStore
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte[]>> _multipart = new();
    public ConcurrentDictionary<string, byte[]> Objects { get; } = new();

    public string Bucket => "test-bucket";

    public Task<string> StartMultipartAsync(string key, string contentType, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        _multipart[id] = new();
        return Task.FromResult(id);
    }

    public async Task UploadPartAsync(string key, string uploadId, int partNumber, Stream content, long length, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await content.CopyToAsync(ms, ct);
        _multipart[uploadId][partNumber] = ms.ToArray();
    }

    public Task<IReadOnlyList<StoredPart>> ListPartsAsync(string key, string uploadId, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StoredPart>>(_multipart[uploadId]
            .Select(p => new StoredPart(p.Key, p.Value.Length, $"etag-{p.Key}")).OrderBy(p => p.PartNumber).ToList());

    public Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<StoredPart> parts, CancellationToken ct)
    {
        Objects[key] = parts.SelectMany(p => _multipart[uploadId][p.PartNumber]).ToArray();
        _multipart.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    public Task AbortMultipartAsync(string key, string uploadId, CancellationToken ct)
    {
        _multipart.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Objects[key], writable: false));
}
