using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardTrader3000.Data.Migrations
{
    /// <inheritdoc />
    public partial class TrackOnlyAndReprice : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipPricing",
                table: "ImportBatchItems",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UnpricedCardCount",
                table: "ImportBatches",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkipPricing",
                table: "ImportBatchItems");

            migrationBuilder.DropColumn(
                name: "UnpricedCardCount",
                table: "ImportBatches");
        }
    }
}
