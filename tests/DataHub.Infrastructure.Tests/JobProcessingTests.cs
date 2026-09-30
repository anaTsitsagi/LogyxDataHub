using System.IO.Compression;
using System.Security.Cryptography;
using DataHub.Application;
using DataHub.Application.Processing;
using DataHub.Application.Security;
using DataHub.Domain;
using DataHub.Infrastructure.Persistence;
using DataHub.Oris.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace DataHub.Infrastructure.Tests;

/// <summary>Worker job processing and recovery against a real database, with S3/SMS/email/RabbitMQ fakes.</summary>
[Collection(SqlCollection.Name)]
public sealed class JobProcessingTests : IDisposable
{
    private const string HiroFolder = @"ORIS DB\ORIS 5\HIRO";

    private readonly SqlDatabaseFixture _fixture;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 30, 12, 0, 0, TimeSpan.Zero));
    private readonly InMemoryFileStore _files = new();
    private readonly CapturingPublisher _publisher = new();
    private readonly FakeSms _sms = new();
    private readonly FakeEmail _email = new();
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"datahub-test-{Guid.NewGuid():N}");
    private readonly ServiceProvider _services;

    public JobProcessingTests(SqlDatabaseFixture fixture)
    {
        _fixture = fixture;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataHubApplication();
        services.AddDataHubPersistence(fixture.Options);
        services.AddSingleton<TimeProvider>(_clock);
        services.AddSingleton<IFileStore>(_files);
        services.AddSingleton<IJobPublisher>(_publisher);
        services.AddSingleton<ISmsSender>(_sms);
        services.AddSingleton<IEmailSender>(_email);
        services.Configure<SecurityOptions>(o => o.SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        services.Configure<ProcessingOptions>(o => o.TempDirectory = _tempDirectory);
        _services = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_tempDirectory)) Directory.Delete(_tempDirectory, recursive: true);
    }

    private sealed record Seeded(Guid CompanyId, Guid TenantId, Guid InvitationId, Guid UploadId, Guid JobId, ProcessingJobMessage Message);

    /// <summary>Creates a company with an invitation (SMS and email), a completed upload stored in S3, and its queued job.</summary>
    private async Task<Seeded> SeedAsync(byte[] file, bool withSha = true, JobStatus status = JobStatus.Queued, int attempts = 0)
    {
        await using var db = _fixture.CreateContext();
        var now = _clock.GetUtcNow();
        var company = new Company
        {
            Id = Guid.NewGuid(), TenantId = Guid.NewGuid(), Name = "შპს ჰირო", CreatedAt = now,
            CompanyCode = Random.Shared.NextInt64(100_000_000, 999_999_999).ToString(),
        };
        var invitation = new Invitation
        {
            Id = Guid.NewGuid(), CompanyId = company.Id, TokenHash = RandomNumberGenerator.GetBytes(32),
            Email = "owner@hiro.ge", Phone = "+995555123456", Channels = NotificationChannels.Sms | NotificationChannels.Email,
            Status = InvitationStatus.Uploaded, CreatedAt = now, ExpiresAt = now.AddDays(14),
        };
        var upload = new Upload
        {
            Id = Guid.NewGuid(), CompanyId = company.Id, InvitationId = invitation.Id, Type = UploadType.OrisDatabase,
            Status = UploadStatus.Completed, FileName = "hiro.zip", S3Bucket = _files.Bucket,
            S3Key = $"uploads/{company.TenantId:N}/{Guid.NewGuid():N}/hiro.zip", SizeBytes = file.Length,
            Sha256 = withSha ? Convert.ToHexStringLower(SHA256.HashData(file)) : null, CreatedAt = now, CompletedAt = now,
        };
        var job = new ProcessingJob
        {
            Id = Guid.NewGuid(), UploadId = upload.Id, CompanyId = company.Id, Status = status, Attempts = attempts,
            QueuedAt = now, StartedAt = status == JobStatus.Processing ? now : null,
            HeartbeatAt = status == JobStatus.Processing ? now : null,
        };
        db.AddRange(company, invitation, upload, job);
        await db.SaveChangesAsync();
        _files.Objects[upload.S3Key] = file;

        return new Seeded(company.Id, company.TenantId, invitation.Id, upload.Id, job.Id,
            ProcessingJobMessage.For(job, upload, company.TenantId, "test"));
    }

    private async Task<JobOutcome> ProcessAsync(ProcessingJobMessage message)
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobProcessor>().ProcessAsync(message, default);
    }

    // The database is shared with other test classes, so the sweep may also touch their jobs.
    private List<ProcessingJobMessage> PublishedFor(Seeded s) => _publisher.Published.Where(m => m.JobId == s.JobId).ToList();

    private async Task<RecoveryResult> RecoverAsync()
    {
        await using var scope = _services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JobRecovery>().RunAsync(default);
    }

    private async Task<(ProcessingJob Job, Invitation Invitation, Company Company)> StateAsync(Seeded s)
    {
        await using var db = _fixture.CreateContext();
        return (await db.ProcessingJobs.SingleAsync(j => j.Id == s.JobId),
                await db.Invitations.SingleAsync(i => i.Id == s.InvitationId),
                await db.Companies.SingleAsync(c => c.Id == s.CompanyId));
    }

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private static byte[] Sample(string file) => File.ReadAllBytes(Samples.PathOf(Path.Combine(HiroFolder, file)));

    private void AssertCustomerNotifiedOfFailure()
    {
        Assert.Contains(_email.Sent, m => m.To == "owner@hiro.ge" && m.Body.Contains("შპს ჰირო"));
        Assert.Contains(_sms.Sent, m => m.Phone == "+995555123456");
    }

    [SampleFact(HiroFolder + @"\WIRING.TPS")]
    public async Task Hiro_database_is_imported_and_activated()
    {
        var file = Zip(("HIRO/WIRING.TPS", Sample("WIRING.TPS")), ("HIRO/Acc_name.tps", Sample("Acc_name.tps")),
            ("HIRO/Notes.txt", "x"u8.ToArray()));
        var s = await SeedAsync(file, withSha: false);

        Assert.Equal(JobOutcome.Succeeded, await ProcessAsync(s.Message));

        var (job, invitation, company) = await StateAsync(s);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal(InvitationStatus.Processed, invitation.Status);
        Assert.Equal(job.DatasetId, company.ActiveDatasetId);

        await using var db = _fixture.CreateContext();
        var dataset = await db.Datasets.SingleAsync(d => d.Id == job.DatasetId);
        Assert.True(dataset.JournalEntryCount > 0);
        Assert.True(dataset.AccountCount > 0);
        Assert.Equal(dataset.JournalEntryCount, await db.JournalEntries.CountAsync(j => j.DatasetId == dataset.Id));
        // The browser does not hash large files; the worker records the checksum.
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(file)), (await db.Uploads.SingleAsync(u => u.Id == s.UploadId)).Sha256);
        Assert.Empty(Directory.EnumerateFiles(_tempDirectory));
        Assert.Empty(_email.Sent);
    }

    [Fact]
    public async Task Archive_without_journal_fails_and_customer_is_told_on_both_channels()
    {
        var s = await SeedAsync(Zip(("HIRO/Acc_name.tps", [1, 2, 3])));

        Assert.Equal(JobOutcome.Failed, await ProcessAsync(s.Message));

        var (job, invitation, company) = await StateAsync(s);
        Assert.Equal((JobStatus.Failed, ProcessingErrors.NoJournal), (job.Status, job.ErrorCode));
        Assert.Equal(InvitationStatus.Failed, invitation.Status);
        Assert.Null(company.ActiveDatasetId);
        AssertCustomerNotifiedOfFailure();
        Assert.Contains("WIRING.TPS", _email.Sent[0].Body);
    }

    [Fact]
    public async Task Corrupt_tps_file_is_reported_as_unreadable_and_staging_data_is_discarded()
    {
        var s = await SeedAsync(Zip(("WIRING.TPS", RandomNumberGenerator.GetBytes(8192))));

        Assert.Equal(JobOutcome.Failed, await ProcessAsync(s.Message));

        var (job, _, _) = await StateAsync(s);
        Assert.Equal(ProcessingErrors.Unreadable, job.ErrorCode);
        await using var db = _fixture.CreateContext();
        Assert.All(await db.Datasets.Where(d => d.ProcessingJobId == s.JobId).ToListAsync(),
            d => Assert.Equal(DatasetStatus.Failed, d.Status));
        Assert.Empty(Directory.EnumerateFiles(_tempDirectory));
    }

    [Fact]
    public async Task Archive_with_two_company_databases_is_rejected()
    {
        var s = await SeedAsync(Zip(("A/WIRING.TPS", [1]), ("B/WIRING.TPS", [2])));

        Assert.Equal(JobOutcome.Failed, await ProcessAsync(s.Message));
        Assert.Equal(ProcessingErrors.MultipleDatabases, (await StateAsync(s)).Job.ErrorCode);
    }

    [Fact]
    public async Task Checksum_mismatch_is_rejected_as_corrupted_upload()
    {
        var s = await SeedAsync(Zip(("WIRING.TPS", [1])));
        _files.Objects[s.Message.S3Key] = Zip(("WIRING.TPS", [2])); // same size, different content

        Assert.Equal(JobOutcome.Failed, await ProcessAsync(s.Message));
        Assert.Equal(ProcessingErrors.Corrupted, (await StateAsync(s)).Job.ErrorCode);
    }

    [Fact]
    public async Task Not_a_zip_is_rejected()
    {
        var s = await SeedAsync(RandomNumberGenerator.GetBytes(4096));

        Assert.Equal(JobOutcome.Failed, await ProcessAsync(s.Message));
        Assert.Equal(ProcessingErrors.NotZip, (await StateAsync(s)).Job.ErrorCode);
    }

    [Fact]
    public async Task Duplicate_message_for_finished_job_is_skipped()
    {
        var s = await SeedAsync(Zip(("x.txt", [1])));
        Assert.Equal(JobOutcome.Failed, await ProcessAsync(s.Message));
        int emails = _email.Sent.Count;

        Assert.Equal(JobOutcome.Skipped, await ProcessAsync(s.Message));
        Assert.Equal(1, (await StateAsync(s)).Job.Attempts);
        Assert.Equal(emails, _email.Sent.Count);
    }

    [Fact]
    public async Task Message_for_job_being_processed_elsewhere_is_skipped()
    {
        var s = await SeedAsync(Zip(("WIRING.TPS", [1])), status: JobStatus.Processing, attempts: 1);

        Assert.Equal(JobOutcome.Skipped, await ProcessAsync(s.Message));
        Assert.Equal(JobStatus.Processing, (await StateAsync(s)).Job.Status);
    }

    [Fact]
    public async Task Transient_storage_outage_is_retried_by_the_sweep_then_fails_after_max_attempts()
    {
        var s = await SeedAsync(Zip(("WIRING.TPS", [1])));
        _files.DownloadFailure = new IOException("S3 unavailable");

        Assert.Equal(JobOutcome.RetryScheduled, await ProcessAsync(s.Message));
        Assert.Equal(JobStatus.Queued, (await StateAsync(s)).Job.Status);
        Assert.Empty(_email.Sent); // customers are not bothered by retries

        // Not due yet, then republished once RequeueAfter has passed, at most once per interval.
        await RecoverAsync();
        Assert.Empty(PublishedFor(s));
        _clock.Advance(TimeSpan.FromMinutes(6));
        await RecoverAsync();
        await RecoverAsync();
        Assert.Single(PublishedFor(s));

        Assert.Equal(JobOutcome.RetryScheduled, await ProcessAsync(s.Message));
        Assert.Equal(JobOutcome.Failed, await ProcessAsync(s.Message));

        var (job, invitation, _) = await StateAsync(s);
        Assert.Equal((JobStatus.Failed, ProcessingErrors.Failed, 3), (job.Status, job.ErrorCode, job.Attempts));
        Assert.Contains("S3 unavailable", job.ErrorDetail);
        Assert.Equal(InvitationStatus.Failed, invitation.Status);
        AssertCustomerNotifiedOfFailure();
        Assert.All(_email.Sent, m => Assert.DoesNotContain("S3", m.Body)); // internal detail is never shown to the customer
    }

    [Fact]
    public async Task Job_abandoned_by_a_crashed_worker_is_requeued_and_can_be_claimed()
    {
        var s = await SeedAsync(Zip(("x.txt", [1])), status: JobStatus.Processing, attempts: 1);
        _clock.Advance(TimeSpan.FromMinutes(4));

        await RecoverAsync();

        Assert.Equal(JobStatus.Queued, (await StateAsync(s)).Job.Status);
        Assert.Equal(JobOutcome.Failed, await ProcessAsync(Assert.Single(PublishedFor(s))));
        Assert.Equal(2, (await StateAsync(s)).Job.Attempts);
    }

    [Fact]
    public async Task Abandoned_job_on_its_last_attempt_is_failed_and_customer_notified()
    {
        var s = await SeedAsync(Zip(("x.txt", [1])), status: JobStatus.Processing, attempts: 3);
        _clock.Advance(TimeSpan.FromMinutes(4));

        await RecoverAsync();

        var (job, invitation, _) = await StateAsync(s);
        Assert.Equal((JobStatus.Failed, ProcessingErrors.Failed), (job.Status, job.ErrorCode));
        Assert.Equal(InvitationStatus.Failed, invitation.Status);
        Assert.Empty(PublishedFor(s));
        AssertCustomerNotifiedOfFailure();
    }
}
