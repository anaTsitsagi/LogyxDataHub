using System.ComponentModel.DataAnnotations;
using DataHub.Api.Auth;
using DataHub.Application.Invitations;
using DataHub.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace DataHub.Api.Controllers;

/// <param name="CompanyCode">Company identification code (9 digits, or 11 for individual entrepreneurs).</param>
/// <param name="CompanyName">Company name shown to the customer.</param>
/// <param name="Email">Required when channels contains "email".</param>
/// <param name="Phone">Georgian mobile number; required when channels contains "sms".</param>
/// <param name="Channels">"sms", "email" or both.</param>
public sealed record CreateInvitationRequest(
    [Required] string CompanyCode,
    [Required] string CompanyName,
    string? Email,
    string? Phone,
    [Required, MinLength(1)] string[] Channels);

public sealed record InvitationResponse(
    Guid InvitationId,
    Guid TenantId,
    string Link,
    DateTimeOffset ExpiresAt,
    InvitationStatus Status,
    string[] DeliveredChannels);

public sealed record InvitationStatusResponse(
    Guid InvitationId,
    Guid TenantId,
    InvitationStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? VerifiedAt,
    JobStatus? ProcessingStatus,
    string? ProcessingErrorCode,
    DateTimeOffset? ProcessingFinishedAt);

/// <summary>Used by TBC LOS to send a customer the upload link and to follow its progress.</summary>
[ApiController]
[Route("invitations")]
[Authorize(Policy = ApiAuthentication.InvitationsPolicy)]
[Produces("application/json")]
public sealed class InvitationsController(InvitationService invitations) : ControllerBase
{
    /// <summary>Creates an upload link for a company and sends it by SMS and/or email.</summary>
    /// <remarks>
    /// Send an <c>Idempotency-Key</c> header: a retry with the same key returns the original invitation
    /// (HTTP 200) and does not send the link again.
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<InvitationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<InvitationResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<InvitationResponse>> Create(
        CreateInvitationRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        var channels = NotificationChannels.None;
        foreach (var c in request.Channels)
        {
            channels |= c.Trim().ToLowerInvariant() switch
            {
                "sms" => NotificationChannels.Sms,
                "email" => NotificationChannels.Email,
                _ => throw new Application.DataHubException(Application.ErrorKind.Validation, "INVITATION_INVALID",
                    $"Unknown channel '{c}'. Use 'sms' and/or 'email'."),
            };
        }

        var result = await invitations.CreateAsync(new CreateInvitationCommand(
            request.CompanyCode, request.CompanyName, request.Email, request.Phone, channels,
            string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim(), User.ClientId()), ct);

        var response = new InvitationResponse(result.InvitationId, result.TenantId, result.Link, result.ExpiresAt, result.Status,
            ToNames(result.DeliveredChannels));
        return result.Replayed ? Ok(response) : CreatedAtAction(nameof(Get), new { id = result.InvitationId }, response);
    }

    /// <summary>Status of an invitation and its latest upload processing.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<InvitationStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<InvitationStatusResponse> Get(Guid id, CancellationToken ct)
    {
        var s = await invitations.GetStatusAsync(id, User.ClientId(), ct);
        return new InvitationStatusResponse(s.InvitationId, s.TenantId, s.Status, s.CreatedAt, s.ExpiresAt, s.VerifiedAt,
            s.LastJobStatus, s.LastJobErrorCode, s.LastJobFinishedAt);
    }

    private static string[] ToNames(NotificationChannels channels) =>
        [.. channels.HasFlag(NotificationChannels.Sms) ? ["sms"] : Array.Empty<string>(),
         .. channels.HasFlag(NotificationChannels.Email) ? ["email"] : Array.Empty<string>()];
}
