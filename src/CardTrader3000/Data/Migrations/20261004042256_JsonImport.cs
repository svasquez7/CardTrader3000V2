using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardTrader3000.Data.Migrations
{
    /// <inheritdoc />
    public partial class JsonImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PricingProvided",
                table: "ImportBatchItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "ProvidedDataJson",
                table: "ImportBatchItems",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PricingProvided",
                table: "ImportBatchItems");

            migrationBuilder.DropColumn(
                name: "ProvidedDataJson",
                table: "ImportBatchItems");
        }
    }
}
