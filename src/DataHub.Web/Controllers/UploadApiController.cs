using DataHub.Application;
using DataHub.Application.Uploads;
using DataHub.Domain;
using DataHub.Web.Portal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DataHub.Web.Controllers;

public sealed record StartUploadRequest(UploadType Type, string FileName, long SizeBytes);

public sealed record CompleteUploadRequest(string? Sha256);

/// <summary>
/// JSON endpoints used by the upload page. Each chunk becomes one S3 multipart part; the browser
/// sends at most a few chunks in parallel and can resume by asking which parts are already stored.
/// </summary>
[ApiController]
[PortalApiExceptionFilter]
[Route("portal-api/uploads")]
public sealed class UploadApiController(UploadService uploads, IOptions<UploadOptions> options) : ControllerBase
{
    [HttpPost]
    public async Task<StartUploadResult> Start(StartUploadRequest request, CancellationToken ct) =>
        await uploads.StartAsync(User.Customer(), request.Type, request.FileName, request.SizeBytes, ct);

    [HttpGet("{uploadId:guid}/parts")]
    public async Task<UploadProgress> Parts(Guid uploadId, CancellationToken ct) =>
        await uploads.GetProgressAsync(User.Customer(), uploadId, ct);

    [HttpPut("{uploadId:guid}/parts/{partNumber:int}")]
    [RequestSizeLimit(64 * 1024 * 1024)]
    [Consumes("application/octet-stream")]
    public async Task<IActionResult> Part(Guid uploadId, int partNumber, CancellationToken ct)
    {
        long? length = Request.ContentLength;
        if (length is null or <= 0 || length > options.Value.ChunkSizeBytes)
            throw new DataHubException(ErrorKind.Validation, "UPLOAD_BAD_PART_SIZE", "Invalid chunk size.");

        // Buffer one chunk (≤ 16 MB) so the S3 client gets a seekable stream it can retry.
        using var buffer = new MemoryStream((int)length);
        await Request.Body.CopyToAsync(buffer, ct);
        if (buffer.Length != length)
            throw new DataHubException(ErrorKind.Validation, "UPLOAD_BAD_PART_SIZE", "The chunk was not received completely.");
        buffer.Position = 0;

        await uploads.UploadPartAsync(User.Customer(), uploadId, partNumber, buffer, buffer.Length, ct);
        return NoContent();
    }

    [HttpPost("{uploadId:guid}/complete")]
    public async Task<IActionResult> Complete(Guid uploadId, CompleteUploadRequest request, CancellationToken ct)
    {
        var jobId = await uploads.CompleteAsync(User.Customer(), uploadId, request.Sha256, ct);
        return Ok(new { jobId });
    }

    [HttpDelete("{uploadId:guid}")]
    public async Task<IActionResult> Abort(Guid uploadId, CancellationToken ct)
    {
        await uploads.AbortAsync(User.Customer(), uploadId, ct);
        return NoContent();
    }

    [HttpGet("/portal-api/status")]
    public async Task<object> Status(CancellationToken ct)
    {
        var s = await uploads.GetStatusAsync(User.Customer(), ct);
        var text = PortalText.Job(s.JobStatus);
        var error = s.JobStatus == JobStatus.Failed ? PortalText.Error(s.ErrorCode) : null;
        return new { s.JobStatus, s.FileName, s.UpdatedAt, message = text, error };
    }
}
