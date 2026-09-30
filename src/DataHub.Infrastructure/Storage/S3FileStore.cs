using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using DataHub.Application;
using Microsoft.Extensions.Options;

namespace DataHub.Infrastructure.Storage;

public sealed class S3Options
{
    public const string Section = "S3";

    public string Bucket { get; set; } = "";

    /// <summary>Custom endpoint for S3-compatible storage (e.g. MinIO); empty for AWS.</summary>
    public string? ServiceUrl { get; set; }
    public string Region { get; set; } = "eu-central-1";

    /// <summary>Path-style addressing (required by most S3-compatible servers).</summary>
    public bool ForcePathStyle { get; set; }

    /// <summary>Static credentials; leave empty to use the default AWS credential chain (IAM role, env vars).</summary>
    public string? AccessKey { get; set; }
    public string? SecretKey { get; set; }
}

public sealed class S3FileStore : IFileStore, IDisposable
{
    private readonly IAmazonS3 _s3;

    public S3FileStore(IOptions<S3Options> options)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.Bucket)) throw new InvalidOperationException("S3:Bucket is not configured.");
        Bucket = o.Bucket;

        var config = new AmazonS3Config
        {
            ForcePathStyle = o.ForcePathStyle,
            // Only compute checksums when the operation requires them; keeps S3-compatible servers happy.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };
        if (!string.IsNullOrWhiteSpace(o.ServiceUrl))
        {
            config.ServiceURL = o.ServiceUrl;
            config.AuthenticationRegion = o.Region;
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(o.Region);
        }

        _s3 = string.IsNullOrWhiteSpace(o.AccessKey)
            ? new AmazonS3Client(config)
            : new AmazonS3Client(new BasicAWSCredentials(o.AccessKey, o.SecretKey), config);
    }

    public string Bucket { get; }

    public async Task<string> StartMultipartAsync(string key, string contentType, CancellationToken ct)
    {
        var response = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = key,
            ContentType = contentType,
            ServerSideEncryptionMethod = ServerSideEncryptionMethod.AES256,
        }, ct);
        return response.UploadId;
    }

    public async Task UploadPartAsync(string key, string uploadId, int partNumber, Stream content, long length, CancellationToken ct) =>
        await _s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = Bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = partNumber,
            InputStream = content,
            PartSize = length,
        }, ct);

    public async Task<IReadOnlyList<StoredPart>> ListPartsAsync(string key, string uploadId, CancellationToken ct)
    {
        var parts = new List<StoredPart>();
        int? marker = null;
        while (true)
        {
            var response = await _s3.ListPartsAsync(new ListPartsRequest
            {
                BucketName = Bucket,
                Key = key,
                UploadId = uploadId,
                PartNumberMarker = marker?.ToString(),
            }, ct);

            foreach (var p in response.Parts ?? [])
                parts.Add(new StoredPart(p.PartNumber ?? 0, p.Size ?? 0, p.ETag));

            if (response.IsTruncated != true) return parts;
            marker = response.NextPartNumberMarker;
        }
    }

    public async Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<StoredPart> parts, CancellationToken ct) =>
        await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
        {
            BucketName = Bucket,
            Key = key,
            UploadId = uploadId,
            PartETags = parts.Select(p => new PartETag(p.PartNumber, p.ETag)).ToList(),
        }, ct);

    public async Task AbortMultipartAsync(string key, string uploadId, CancellationToken ct) =>
        await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = Bucket, Key = key, UploadId = uploadId }, ct);

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var metadata = await _s3.GetObjectMetadataAsync(Bucket, key, ct);
        return new S3RangeReadStream(_s3, Bucket, key, metadata.ContentLength);
    }

    public void Dispose() => _s3.Dispose();
}

/// <summary>
/// Read-only, seekable view of an S3 object that fetches 1 MB ranges on demand.
/// Lets <see cref="System.IO.Compression.ZipArchive"/> read the central directory at the end of
/// a large archive without downloading the whole file.
/// </summary>
internal sealed class S3RangeReadStream(IAmazonS3 s3, string bucket, string key, long length) : Stream
{
    private const int BlockSize = 1024 * 1024;
    private readonly byte[] _block = new byte[BlockSize];
    private long _blockStart = -1;
    private int _blockLength;
    private long _position;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => _position = value is >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value));
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_position >= length || buffer.Length == 0) return 0;
        if (_blockStart < 0 || _position < _blockStart || _position >= _blockStart + _blockLength)
            await FetchBlockAsync(_position, ct);

        int offset = (int)(_position - _blockStart);
        int count = Math.Min(buffer.Length, _blockLength - offset);
        _block.AsMemory(offset, count).CopyTo(buffer);
        _position += count;
        return count;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    // ZipArchive's synchronous code path; only a handful of small reads happen during inspection.
    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    private async Task FetchBlockAsync(long start, CancellationToken ct)
    {
        long end = Math.Min(start + BlockSize, length) - 1;
        using var response = await s3.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key,
            ByteRange = new ByteRange(start, end),
        }, ct);

        int total = 0, expected = (int)(end - start + 1);
        while (total < expected)
        {
            int read = await response.ResponseStream.ReadAsync(_block.AsMemory(total, expected - total), ct);
            if (read == 0) break;
            total += read;
        }
        _blockStart = start;
        _blockLength = total;
    }

    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => _position + offset,
        _ => length + offset,
    };

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
