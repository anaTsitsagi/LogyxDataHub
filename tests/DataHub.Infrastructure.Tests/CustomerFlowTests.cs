using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DataHub.Application;
using DataHub.Application.Invitations;
using DataHub.Application.Security;
using DataHub.Application.Uploads;
using DataHub.Application.Verification;
using DataHub.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace DataHub.Infrastructure.Tests;

/// <summary>Invitation → verification → chunked upload → queued job, against a real database.</summary>
[Collection(SqlCollection.Name)]
public partial class CustomerFlowTests(SqlDatabaseFixture fixture)
{
    private const int Chunk = 1024;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly FakeSms _sms = new();
    private readonly FakeEmail _email = new();
    private readonly InMemoryFileStore _files = new();
    private readonly CapturingPublisher _publisher = new();
    private readonly UploadOptions _uploadOptions = new() { ChunkSizeBytes = Chunk };
    private readonly Secrets _secrets = new(Options.Create(new SecurityOptions
    {
        SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    }));

    private InvitationService Invitations(IDataHubDb db) => new(db, _secrets, _sms, _email,
        Options.Create(new InvitationOptions { PortalBaseUrl = "https://datahub.test" }), _clock, NullLogger<InvitationService>.Instance);

    private VerificationService Verification(IDataHubDb db) => new(db, _secrets, _sms, _email,
        Options.Create(new OtpOptions()), _clock, NullLogger<VerificationService>.Instance);

    private UploadService Uploads(IDataHubDb db) => new(db, _files, _publisher, Options.Create(_uploadOptions), _clock,
        NullLogger<UploadService>.Instance);

    private static string NewCompanyCode() => Random.Shared.NextInt64(100_000_000, 999_999_999).ToString();

    private static CreateInvitationCommand Command(string code, NotificationChannels channels, string? key = null) =>
        new(code, "შპს ჰირო", channels.HasFlag(NotificationChannels.Email) ? "Owner@Hiro.ge" : null,
            channels.HasFlag(NotificationChannels.Sms) ? "+995 555 12 34 56" : null, channels, key, "tbc-los");

    private static string TokenFrom(string link) => link[(link.LastIndexOf('/') + 1)..];

    private static string CodeFrom(string text) => SixDigits().Match(text).Value;

    private async Task<VerifiedCustomer> VerifiedCustomerAsync(string companyCode)
    {
        await using var db = fixture.CreateContext();
        var invitation = await Invitations(db).CreateAsync(Command(companyCode, NotificationChannels.Email), default);
        var challenge = await Verification(db).StartAsync(TokenFrom(invitation.Link), companyCode, "owner@hiro.ge", default);
        return await Verification(db).ConfirmAsync(challenge.ChallengeId, CodeFrom(_email.Sent[^1].Body), default);
    }

    private static byte[] Zip(params string[] entryNames)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var name in entryNames)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(RandomNumberGenerator.GetBytes(3000)); // incompressible, so the archive spans several chunks
            }
        return ms.ToArray();
    }

    private async Task<Guid> UploadAsync(VerifiedCustomer customer, byte[] file, string name = "hiro.zip")
    {
        await using var db = fixture.CreateContext();
        var uploads = Uploads(db);
        var start = await uploads.StartAsync(customer, UploadType.OrisDatabase, name, file.Length, default);
        for (int part = 1; part <= start.PartCount; part++)
        {
            var slice = file.AsMemory((part - 1) * Chunk, Math.Min(Chunk, file.Length - (part - 1) * Chunk)).ToArray();
            await uploads.UploadPartAsync(customer, start.UploadId, part, new MemoryStream(slice), slice.Length, default);
        }
        return await uploads.CompleteAsync(customer, start.UploadId, Convert.ToHexString(SHA256.HashData(file)), default);
    }

    [Fact]
    public async Task Los_creates_invitation_and_link_is_sent_on_both_channels()
    {
        await using var db = fixture.CreateContext();
        var result = await Invitations(db).CreateAsync(Command(NewCompanyCode(), NotificationChannels.Sms | NotificationChannels.Email), default);

        Assert.Equal(NotificationChannels.Sms | NotificationChannels.Email, result.DeliveredChannels);
        Assert.StartsWith("https://datahub.test/i/", result.Link);
        Assert.Contains(_sms.Sent, m => m.Phone == "+995555123456" && m.Text.Contains(result.Link));
        Assert.Contains(_email.Sent, m => m.To == "Owner@Hiro.ge" && m.Body.Contains(result.Link));
    }

    [Fact]
    public async Task Retry_with_same_idempotency_key_returns_same_invitation_without_resending()
    {
        var code = NewCompanyCode();
        await using var db = fixture.CreateContext();
        var first = await Invitations(db).CreateAsync(Command(code, NotificationChannels.Sms, "req-1"), default);
        var retry = await Invitations(db).CreateAsync(Command(code, NotificationChannels.Sms, "req-1"), default);

        Assert.Equal(first.InvitationId, retry.InvitationId);
        Assert.Equal(first.Link, retry.Link);
        Assert.True(retry.Replayed);
        Assert.Single(_sms.Sent);
    }

    [Fact]
    public async Task Invitation_requires_contact_for_each_chosen_channel()
    {
        await using var db = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<DataHubException>(() => Invitations(db).CreateAsync(
            new CreateInvitationCommand(NewCompanyCode(), "შპს", null, null, NotificationChannels.Sms | NotificationChannels.Email, null, "tbc-los"), default));

        Assert.Equal("INVITATION_INVALID", ex.Code);
        Assert.Contains("phone is required", ex.Message);
        Assert.Contains("email is required", ex.Message);
    }

    [Fact]
    public async Task Wrong_company_code_is_rejected_and_correct_details_send_email_code()
    {
        var code = NewCompanyCode();
        await using var db = fixture.CreateContext();
        var invitation = await Invitations(db).CreateAsync(Command(code, NotificationChannels.Email), default);
        var token = TokenFrom(invitation.Link);

        var mismatch = await Assert.ThrowsAsync<DataHubException>(() => Verification(db).StartAsync(token, "000000000", "owner@hiro.ge", default));
        Assert.Equal("DETAILS_MISMATCH", mismatch.Code);

        var challenge = await Verification(db).StartAsync(token, code, "OWNER@hiro.ge", default);
        Assert.Equal(NotificationChannels.Email, challenge.Channel);
        Assert.Equal("O***@Hiro.ge", challenge.MaskedDestination);

        var wrong = await Assert.ThrowsAsync<DataHubException>(() => Verification(db).ConfirmAsync(challenge.ChallengeId, "000000", default));
        Assert.Equal("OTP_WRONG", wrong.Code);

        var customer = await Verification(db).ConfirmAsync(challenge.ChallengeId, CodeFrom(_email.Sent[^1].Body), default);
        Assert.Equal(invitation.TenantId, customer.TenantId);
    }

    [Fact]
    public async Task Sms_only_invitation_gets_code_by_sms()
    {
        var code = NewCompanyCode();
        await using var db = fixture.CreateContext();
        var invitation = await Invitations(db).CreateAsync(Command(code, NotificationChannels.Sms), default);

        var challenge = await Verification(db).StartAsync(TokenFrom(invitation.Link), code, null, default);

        Assert.Equal(NotificationChannels.Sms, challenge.Channel);
        Assert.Matches(@"\d{6}", _sms.Sent[^1].Text);
    }

    [Fact]
    public async Task Otp_expires_and_resend_is_rate_limited()
    {
        var code = NewCompanyCode();
        await using var db = fixture.CreateContext();
        var token = TokenFrom((await Invitations(db).CreateAsync(Command(code, NotificationChannels.Email), default)).Link);
        var challenge = await Verification(db).StartAsync(token, code, "owner@hiro.ge", default);
        var otp = CodeFrom(_email.Sent[^1].Body);

        var limited = await Assert.ThrowsAsync<DataHubException>(() => Verification(db).StartAsync(token, code, "owner@hiro.ge", default));
        Assert.Equal("OTP_RATE_LIMITED", limited.Code);

        _clock.Advance(TimeSpan.FromMinutes(6));
        var expired = await Assert.ThrowsAsync<DataHubException>(() => Verification(db).ConfirmAsync(challenge.ChallengeId, otp, default));
        Assert.Equal("OTP_EXPIRED", expired.Code);
    }

    [Fact]
    public async Task Chunked_upload_queues_job_with_file_location_only()
    {
        var customer = await VerifiedCustomerAsync(NewCompanyCode());
        var file = Zip("HIRO/WIRING.TPS", "HIRO/Acc_name.tps", "HIRO/Rate.tps");

        var jobId = await UploadAsync(customer, file);

        var message = Assert.Single(_publisher.Published);
        Assert.Equal(jobId, message.JobId);
        Assert.Equal(customer.TenantId, message.TenantId);
        Assert.Equal("test-bucket", message.S3Bucket);
        Assert.Equal(file, _files.Objects[message.S3Key]);
        Assert.Equal(file.Length, message.SizeBytes);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), message.Sha256);

        await using var db = fixture.CreateContext();
        Assert.Equal(JobStatus.Queued, (await db.ProcessingJobs.SingleAsync(j => j.Id == jobId)).Status);
        Assert.Equal(InvitationStatus.Uploaded, (await db.Invitations.SingleAsync(i => i.Id == customer.InvitationId)).Status);
        Assert.Equal(JobStatus.Queued, (await Uploads(db).GetStatusAsync(customer, default)).JobStatus);
    }

    [Fact]
    public async Task Interrupted_upload_reports_stored_parts_for_resume()
    {
        var customer = await VerifiedCustomerAsync(NewCompanyCode());
        var file = Zip("WIRING.TPS");
        await using var db = fixture.CreateContext();
        var uploads = Uploads(db);
        var start = await uploads.StartAsync(customer, UploadType.OrisDatabase, "hiro.zip", file.Length, default);
        await uploads.UploadPartAsync(customer, start.UploadId, 2, new MemoryStream(file[Chunk..(2 * Chunk)]), Chunk, default);

        var progress = await uploads.GetProgressAsync(customer, start.UploadId, default);
        Assert.Equal([2], progress.CompletedParts);

        var incomplete = await Assert.ThrowsAsync<DataHubException>(() => uploads.CompleteAsync(customer, start.UploadId, null, default));
        Assert.Equal("UPLOAD_INCOMPLETE", incomplete.Code);
    }

    [Fact]
    public async Task Zip_without_tps_files_is_rejected_and_not_queued()
    {
        var customer = await VerifiedCustomerAsync(NewCompanyCode());

        var ex = await Assert.ThrowsAsync<DataHubException>(() => UploadAsync(customer, Zip("report.xlsx")));

        Assert.Equal("UPLOAD_NO_TPS", ex.Code);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task Journal_larger_than_the_worker_is_sized_for_is_rejected_and_not_queued()
    {
        var customer = await VerifiedCustomerAsync(NewCompanyCode());
        _uploadOptions.MaxJournalBytes = 2000; // the test entries are 3,000 bytes

        var ex = await Assert.ThrowsAsync<DataHubException>(() => UploadAsync(customer, Zip("HIRO/WIRING.TPS", "HIRO/Acc_name.tps")));

        Assert.Equal(ProcessingErrors.JournalTooLarge, ex.Code);
        Assert.Empty(_publisher.Published);
    }

    [Fact]
    public async Task Zip_with_path_traversal_is_rejected()
    {
        var customer = await VerifiedCustomerAsync(NewCompanyCode());
        var ex = await Assert.ThrowsAsync<DataHubException>(() => UploadAsync(customer, Zip("../../WIRING.TPS")));
        Assert.Equal("UPLOAD_UNSAFE_PATH", ex.Code);
    }

    [Fact]
    public async Task Csv_upload_is_disabled_until_that_phase()
    {
        var customer = await VerifiedCustomerAsync(NewCompanyCode());
        await using var db = fixture.CreateContext();
        var ex = await Assert.ThrowsAsync<DataHubException>(() =>
            Uploads(db).StartAsync(customer, UploadType.OrisEntriesCsv, "entries.csv", 100, default));
        Assert.Equal("UPLOAD_TYPE_DISABLED", ex.Code);
    }

    [Fact]
    public async Task New_upload_is_blocked_while_previous_is_processing()
    {
        var customer = await VerifiedCustomerAsync(NewCompanyCode());
        await UploadAsync(customer, Zip("WIRING.TPS"));

        var ex = await Assert.ThrowsAsync<DataHubException>(() => UploadAsync(customer, Zip("WIRING.TPS")));
        Assert.Equal("UPLOAD_PROCESSING", ex.Code);
    }

    [Theory]
    [InlineData("ბალანსი 05.05.2025 - შპს 123.zip", "05.05.2025_-_123.zip")]
    [InlineData(@"C:\fakepath\HIRO.zip", "HIRO.zip")]
    [InlineData("....zip", "zip")]
    public void Object_keys_are_ascii_safe(string input, string expected) =>
        Assert.Equal(expected, UploadService.SafeKeyName(input));

    [GeneratedRegex(@"\b\d{6}\b")]
    private static partial Regex SixDigits();
}
