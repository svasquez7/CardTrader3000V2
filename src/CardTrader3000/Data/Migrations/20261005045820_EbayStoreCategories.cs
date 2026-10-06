using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardTrader3000.Data.Migrations
{
    /// <inheritdoc />
    public partial class EbayStoreCategories : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StoreCategories",
                table: "EbaySettings",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "StoreCategoryName",
                table: "EbayListings",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StoreCategories",
                table: "EbaySettings");

            migrationBuilder.DropColumn(
                name: "StoreCategoryName",
                table: "EbayListings");
        }
    }
}
