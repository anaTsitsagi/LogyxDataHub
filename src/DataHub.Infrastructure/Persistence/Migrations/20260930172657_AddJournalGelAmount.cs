using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataHub.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddJournalGelAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AmountGel",
                schema: "datahub",
                table: "JournalEntries",
                type: "decimal(19,4)",
                precision: 19,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "ExchangeRate",
                schema: "datahub",
                table: "JournalEntries",
                type: "decimal(19,8)",
                precision: 19,
                scale: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountGel",
                schema: "datahub",
                table: "JournalEntries");

            migrationBuilder.DropColumn(
                name: "ExchangeRate",
                schema: "datahub",
                table: "JournalEntries");
        }
    }
}
