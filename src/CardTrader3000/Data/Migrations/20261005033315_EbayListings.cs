using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardTrader3000.Data.Migrations
{
    /// <inheritdoc />
    public partial class EbayListings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EbayListings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    InventoryCardId = table.Column<int>(type: "INTEGER", nullable: true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Sku = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Price = table.Column<double>(type: "REAL", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    CategoryId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CardConditionValueId = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AspectsJson = table.Column<string>(type: "TEXT", nullable: false),
                    FulfillmentPolicyId = table.Column<string>(type: "TEXT", nullable: true),
                    ReturnPolicyId = table.Column<string>(type: "TEXT", nullable: true),
                    PaymentPolicyId = table.Column<string>(type: "TEXT", nullable: true),
                    BestOfferMinPercent = table.Column<double>(type: "REAL", nullable: false),
                    BestOfferAutoAcceptPercent = table.Column<double>(type: "REAL", nullable: false),
                    PromotionPercent = table.Column<double>(type: "REAL", nullable: false),
                    OfferId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ListingId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    CampaignId = table.Column<string>(type: "TEXT", nullable: true),
                    AdId = table.Column<string>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PublishedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EbayListings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EbayListings_InventoryCards_InventoryCardId",
                        column: x => x.InventoryCardId,
                        principalTable: "InventoryCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "EbaySettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Environment = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    RefreshTokenProtected = table.Column<string>(type: "TEXT", nullable: true),
                    RefreshTokenExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AccessTokenProtected = table.Column<string>(type: "TEXT", nullable: true),
                    AccessTokenExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ConnectedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DefaultFulfillmentPolicyId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DefaultReturnPolicyId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    DefaultPaymentPolicyId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    MerchantLocationKey = table.Column<string>(type: "TEXT", maxLength: 36, nullable: true),
                    DefaultPromotionPercent = table.Column<double>(type: "REAL", nullable: false),
                    BestOfferMinPercent = table.Column<double>(type: "REAL", nullable: false),
                    BestOfferAutoAcceptPercent = table.Column<double>(type: "REAL", nullable: false),
                    PromotionCampaignId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EbaySettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EbayListingImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EbayListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    LocalFileName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    EbayUrl = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    EbayUrlExpiresUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EbayListingImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EbayListingImages_EbayListings_EbayListingId",
                        column: x => x.EbayListingId,
                        principalTable: "EbayListings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EbayListingImages_EbayListingId",
                table: "EbayListingImages",
                column: "EbayListingId");

            migrationBuilder.CreateIndex(
                name: "IX_EbayListings_Environment_Sku",
                table: "EbayListings",
                columns: new[] { "Environment", "Sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EbayListings_InventoryCardId",
                table: "EbayListings",
                column: "InventoryCardId");

            migrationBuilder.CreateIndex(
                name: "IX_EbayListings_Status",
                table: "EbayListings",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_EbaySettings_Environment",
                table: "EbaySettings",
                column: "Environment",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EbayListingImages");

            migrationBuilder.DropTable(
                name: "EbaySettings");

            migrationBuilder.DropTable(
                name: "EbayListings");
        }
    }
}
