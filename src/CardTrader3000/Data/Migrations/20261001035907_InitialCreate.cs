using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CardTrader3000.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImportBatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SourceFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 30, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    TotalInputLines = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalCardsEvaluated = table.Column<int>(type: "INTEGER", nullable: false),
                    NewCardCount = table.Column<int>(type: "INTEGER", nullable: false),
                    MergedCardCount = table.Column<int>(type: "INTEGER", nullable: false),
                    FailedCardCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Bucket1Count = table.Column<int>(type: "INTEGER", nullable: false),
                    Bucket2Count = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalGrossRevenue = table.Column<double>(type: "REAL", nullable: false),
                    TotalEbayFees = table.Column<double>(type: "REAL", nullable: false),
                    TotalPostage = table.Column<double>(type: "REAL", nullable: false),
                    TotalMaxNetReturn = table.Column<double>(type: "REAL", nullable: false),
                    OverallNetMarginPercent = table.Column<double>(type: "REAL", nullable: false),
                    FinalValueFeeRate = table.Column<double>(type: "REAL", nullable: false),
                    FixedOrderFee = table.Column<double>(type: "REAL", nullable: false),
                    StandardEnvelopeRate = table.Column<double>(type: "REAL", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ChunkCount = table.Column<int>(type: "INTEGER", nullable: false),
                    InputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<int>(type: "INTEGER", nullable: false),
                    RawResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryCards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CardSet = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlayerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CardNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Parallel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NormalizedKey = table.Column<string>(type: "TEXT", maxLength: 700, nullable: false),
                    Team = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    IsRookie = table.Column<bool>(type: "INTEGER", nullable: false),
                    Bucket = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SgcCandidate = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    SalesStrategy = table.Column<string>(type: "TEXT", nullable: true),
                    EbayTitle = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    EbayDescription = table.Column<string>(type: "TEXT", nullable: true),
                    EstimatedListPrice = table.Column<double>(type: "REAL", nullable: false),
                    EbayFee = table.Column<double>(type: "REAL", nullable: false),
                    PostageCost = table.Column<double>(type: "REAL", nullable: false),
                    TotalFees = table.Column<double>(type: "REAL", nullable: false),
                    MaxNetReturn = table.Column<double>(type: "REAL", nullable: false),
                    NetMarginPercent = table.Column<double>(type: "REAL", nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastPricedUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryCards", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImportBatchItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ImportBatchId = table.Column<int>(type: "INTEGER", nullable: false),
                    InventoryCardId = table.Column<int>(type: "INTEGER", nullable: true),
                    LineNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    CardSet = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    PlayerName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CardNumber = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    Parallel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    QuantityAdded = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedNewCard = table.Column<bool>(type: "INTEGER", nullable: false),
                    EstimatedListPrice = table.Column<double>(type: "REAL", nullable: true),
                    MaxNetReturn = table.Column<double>(type: "REAL", nullable: true),
                    Bucket = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    SgcCandidate = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatchItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportBatchItems_ImportBatches_ImportBatchId",
                        column: x => x.ImportBatchId,
                        principalTable: "ImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImportBatchItems_InventoryCards_InventoryCardId",
                        column: x => x.InventoryCardId,
                        principalTable: "InventoryCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_CreatedUtc",
                table: "ImportBatches",
                column: "CreatedUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatchItems_ImportBatchId",
                table: "ImportBatchItems",
                column: "ImportBatchId");

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatchItems_InventoryCardId",
                table: "ImportBatchItems",
                column: "InventoryCardId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCards_Bucket",
                table: "InventoryCards",
                column: "Bucket");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCards_CardSet",
                table: "InventoryCards",
                column: "CardSet");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCards_NormalizedKey",
                table: "InventoryCards",
                column: "NormalizedKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCards_PlayerName",
                table: "InventoryCards",
                column: "PlayerName");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCards_SgcCandidate",
                table: "InventoryCards",
                column: "SgcCandidate");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryCards_Status",
                table: "InventoryCards",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImportBatchItems");

            migrationBuilder.DropTable(
                name: "ImportBatches");

            migrationBuilder.DropTable(
                name: "InventoryCards");
        }
    }
}
