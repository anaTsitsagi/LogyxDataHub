using DataHub.Application;
using DataHub.Domain;
using Microsoft.EntityFrameworkCore;

namespace DataHub.Infrastructure.Persistence;

public class DataHubDbContext(DbContextOptions<DataHubDbContext> options) : DbContext(options), IDataHubDb
{
    public const string Schema = "datahub";

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<OtpChallenge> OtpChallenges => Set<OtpChallenge>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<Dataset> Datasets => Set<Dataset>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);

        b.Entity<Company>(e =>
        {
            e.HasIndex(x => x.TenantId).IsUnique();
            e.HasIndex(x => x.CompanyCode).IsUnique();
            e.Property(x => x.CompanyCode).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.RowVersion).IsRowVersion();
            e.HasOne<Dataset>().WithMany().HasForeignKey(x => x.ActiveDatasetId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<Invitation>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.CreatedByClient, x.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
            e.Property(x => x.TokenHash).HasMaxLength(32);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.IdempotencyKey).HasMaxLength(100);
            e.Property(x => x.CreatedByClient).HasMaxLength(100);
            e.HasOne(x => x.Company).WithMany().HasForeignKey(x => x.CompanyId);
        });

        b.Entity<OtpChallenge>(e =>
        {
            e.HasIndex(x => x.InvitationId);
            e.Property(x => x.CodeHash).HasMaxLength(32);
            e.HasOne<Invitation>().WithMany().HasForeignKey(x => x.InvitationId);
        });

        b.Entity<Upload>(e =>
        {
            e.HasIndex(x => x.CompanyId);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.S3Bucket).HasMaxLength(63);
            e.Property(x => x.S3Key).HasMaxLength(1024);
            e.Property(x => x.S3UploadId).HasMaxLength(256);
            e.Property(x => x.Sha256).HasMaxLength(64).IsFixedLength();
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.NoAction);
            e.HasOne<Invitation>().WithMany().HasForeignKey(x => x.InvitationId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<ProcessingJob>(e =>
        {
            e.HasIndex(x => x.UploadId);
            e.HasIndex(x => new { x.CompanyId, x.QueuedAt });
            e.Property(x => x.ErrorCode).HasMaxLength(50);
            e.HasOne<Upload>().WithMany().HasForeignKey(x => x.UploadId).OnDelete(DeleteBehavior.NoAction);
        });

        b.Entity<Dataset>(e =>
        {
            e.HasIndex(x => new { x.CompanyId, x.Status });
            e.HasIndex(x => x.Status);
            e.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId).OnDelete(DeleteBehavior.NoAction);
        });

        // Row tables are clustered on (DatasetId, Id): a dataset's rows are contiguous, which keeps
        // tenant-scoped reads fast and lets old datasets be deleted in cheap range batches.
        b.Entity<Account>(e =>
        {
            e.HasKey(x => new { x.DatasetId, x.Id });
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.DatasetId, x.Code, x.Sub });
            e.Property(x => x.Code).HasMaxLength(4).IsFixedLength().IsUnicode(false);
            e.Property(x => x.Sub).HasMaxLength(25);
            e.Property(x => x.Raw).HasMaxLength(25);
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
        });

        b.Entity<JournalEntry>(e =>
        {
            e.HasKey(x => new { x.DatasetId, x.Id });
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.DatasetId, x.OperationDate });
            e.HasIndex(x => new { x.DatasetId, x.Debet, x.OperationDate });
            e.HasIndex(x => new { x.DatasetId, x.Credit, x.OperationDate });
            e.Property(x => x.DocumentNumber).HasMaxLength(20);
            e.Property(x => x.Debet).HasMaxLength(4).IsFixedLength().IsUnicode(false);
            e.Property(x => x.Credit).HasMaxLength(4).IsFixedLength().IsUnicode(false);
            e.Property(x => x.DebetSub).HasMaxLength(25);
            e.Property(x => x.CreditSub).HasMaxLength(25);
            e.Property(x => x.DebetRaw).HasMaxLength(25);
            e.Property(x => x.CreditRaw).HasMaxLength(25);
            e.Property(x => x.Amount).HasPrecision(19, 4);
            e.Property(x => x.Quantity).HasPrecision(19, 4);
            e.Property(x => x.Currency).HasMaxLength(3).IsUnicode(false);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.Unit).HasMaxLength(12);
            e.Property(x => x.PostedBy).HasMaxLength(30);
        });
    }
}
