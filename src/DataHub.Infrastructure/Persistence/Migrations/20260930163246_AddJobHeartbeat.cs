using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJobHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HeartbeatAt",
                schema: "datahub",
                table: "ProcessingJobs",
                type: "datetimeoffset",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeartbeatAt",
                schema: "datahub",
                table: "ProcessingJobs");
        }
    }
}
