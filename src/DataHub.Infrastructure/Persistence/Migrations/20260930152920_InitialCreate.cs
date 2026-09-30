using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "datahub");

            migrationBuilder.CreateTable(
                name: "Accounts",
                schema: "datahub",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatasetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "char(4)", unicode: false, fixedLength: true, maxLength: 4, nullable: false),
                    Sub = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    Raw = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    Level = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => new { x.DatasetId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "JournalEntries",
                schema: "datahub",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DatasetId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecordNumber = table.Column<int>(type: "int", nullable: false),
                    DocumentNumber = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    EntryNumber = table.Column<long>(type: "bigint", nullable: false),
                    Debet = table.Column<string>(type: "char(4)", unicode: false, fixedLength: true, maxLength: 4, nullable: true),
                    DebetSub = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    DebetRaw = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    Credit = table.Column<string>(type: "char(4)", unicode: false, fixedLength: true, maxLength: 4, nullable: true),
                    CreditSub = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    CreditRaw = table.Column<string>(type: "nvarchar(25)", maxLength: 25, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    Currency = table.Column<string>(type: "varchar(3)", unicode: false, maxLength: 3, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    Unit = table.Column<string>(type: "nvarchar(12)", maxLength: 12, nullable: false),
                    PostedBy = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    OperationDate = table.Column<DateOnly>(type: "date", nullable: true),
                    PostingDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JournalEntries", x => new { x.DatasetId, x.Id });
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                schema: "datahub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyCode = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    ActiveDatasetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Datasets",
                schema: "datahub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ProcessingJobId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    AccountCount = table.Column<int>(type: "int", nullable: false),
                    JournalEntryCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Datasets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Datasets_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "datahub",
                        principalTable: "Companies",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Invitations",
                schema: "datahub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TokenHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Channels = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedByClient = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    VerifiedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invitations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Invitations_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "datahub",
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OtpChallenges",
                schema: "datahub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CodeHash = table.Column<byte[]>(type: "varbinary(32)", maxLength: 32, nullable: false),
                    Channel = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OtpChallenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OtpChallenges_Invitations_InvitationId",
                        column: x => x.InvitationId,
                        principalSchema: "datahub",
                        principalTable: "Invitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Uploads",
                schema: "datahub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Type = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    FileName = table.Column<string>(type: "nvarchar(260)", maxLength: 260, nullable: false),
                    S3Bucket = table.Column<string>(type: "nvarchar(63)", maxLength: 63, nullable: false),
                    S3Key = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    S3UploadId = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "nchar(64)", fixedLength: true, maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Uploads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Uploads_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalSchema: "datahub",
                        principalTable: "Companies",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Uploads_Invitations_InvitationId",
                        column: x => x.InvitationId,
                        principalSchema: "datahub",
                        principalTable: "Invitations",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "ProcessingJobs",
                schema: "datahub",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UploadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CompanyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ErrorCode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ErrorDetail = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    DatasetId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    QueuedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    FinishedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessingJobs_Uploads_UploadId",
                        column: x => x.UploadId,
                        principalSchema: "datahub",
                        principalTable: "Uploads",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Accounts_DatasetId_Code_Sub",
                schema: "datahub",
                table: "Accounts",
                columns: new[] { "DatasetId", "Code", "Sub" });

            migrationBuilder.CreateIndex(
                name: "IX_Companies_ActiveDatasetId",
                schema: "datahub",
                table: "Companies",
                column: "ActiveDatasetId");

            migrationBuilder.CreateIndex(
                name: "IX_Companies_CompanyCode",
                schema: "datahub",
                table: "Companies",
                column: "CompanyCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Companies_TenantId",
                schema: "datahub",
                table: "Companies",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Datasets_CompanyId_Status",
                schema: "datahub",
                table: "Datasets",
                columns: new[] { "CompanyId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Datasets_Status",
                schema: "datahub",
                table: "Datasets",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_CompanyId",
                schema: "datahub",
                table: "Invitations",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_CreatedByClient_IdempotencyKey",
                schema: "datahub",
                table: "Invitations",
                columns: new[] { "CreatedByClient", "IdempotencyKey" },
                unique: true,
                filter: "[IdempotencyKey] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Invitations_TokenHash",
                schema: "datahub",
                table: "Invitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_DatasetId_Credit_OperationDate",
                schema: "datahub",
                table: "JournalEntries",
                columns: new[] { "DatasetId", "Credit", "OperationDate" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_DatasetId_Debet_OperationDate",
                schema: "datahub",
                table: "JournalEntries",
                columns: new[] { "DatasetId", "Debet", "OperationDate" });

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_DatasetId_OperationDate",
                schema: "datahub",
                table: "JournalEntries",
                columns: new[] { "DatasetId", "OperationDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OtpChallenges_InvitationId",
                schema: "datahub",
                table: "OtpChallenges",
                column: "InvitationId");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_CompanyId_QueuedAt",
                schema: "datahub",
                table: "ProcessingJobs",
                columns: new[] { "CompanyId", "QueuedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_UploadId",
                schema: "datahub",
                table: "ProcessingJobs",
                column: "UploadId");

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_CompanyId",
                schema: "datahub",
                table: "Uploads",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_InvitationId",
                schema: "datahub",
                table: "Uploads",
                column: "InvitationId");

            migrationBuilder.AddForeignKey(
                name: "FK_Companies_Datasets_ActiveDatasetId",
                schema: "datahub",
                table: "Companies",
                column: "ActiveDatasetId",
                principalSchema: "datahub",
                principalTable: "Datasets",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Companies_Datasets_ActiveDatasetId",
                schema: "datahub",
                table: "Companies");

            migrationBuilder.DropTable(
                name: "Accounts",
                schema: "datahub");

            migrationBuilder.DropTable(
                name: "JournalEntries",
                schema: "datahub");

            migrationBuilder.DropTable(
                name: "OtpChallenges",
                schema: "datahub");

            migrationBuilder.DropTable(
                name: "ProcessingJobs",
                schema: "datahub");

            migrationBuilder.DropTable(
                name: "Uploads",
                schema: "datahub");

            migrationBuilder.DropTable(
                name: "Invitations",
                schema: "datahub");

            migrationBuilder.DropTable(
                name: "Datasets",
                schema: "datahub");

            migrationBuilder.DropTable(
                name: "Companies",
                schema: "datahub");
        }
    }
}
