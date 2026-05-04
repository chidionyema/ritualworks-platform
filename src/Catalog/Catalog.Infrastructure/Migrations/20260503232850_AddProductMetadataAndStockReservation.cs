using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haworks.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddProductMetadataAndStockReservation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AuthorName",
                schema: "catalog",
                table: "ProductReviews",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Title",
                schema: "catalog",
                table: "ProductReviews",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrderStockReservations",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReservedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReleaseReason = table.Column<string>(type: "text", nullable: true),
                    ItemsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedFromIp = table.Column<string>(type: "text", nullable: true),
                    ModifiedFromIp = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false, defaultValueSql: "'\\x0000000000000000'::bytea"),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: true),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderStockReservations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProductMetadata",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    KeyName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    KeyValue = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedFromIp = table.Column<string>(type: "text", nullable: true),
                    ModifiedFromIp = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false, defaultValueSql: "'\\x0000000000000000'::bytea"),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: true),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductMetadata", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductMetadata_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductSpecifications",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    DisplayOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductSpecifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductSpecifications_Products_ProductId",
                        column: x => x.ProductId,
                        principalSchema: "catalog",
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderStockReservations_OrderId",
                schema: "catalog",
                table: "OrderStockReservations",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductMetadata_ProductId",
                schema: "catalog",
                table: "ProductMetadata",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductSpecifications_ProductId",
                schema: "catalog",
                table: "ProductSpecifications",
                column: "ProductId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderStockReservations",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductMetadata",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "ProductSpecifications",
                schema: "catalog");

            migrationBuilder.DropColumn(
                name: "AuthorName",
                schema: "catalog",
                table: "ProductReviews");

            migrationBuilder.DropColumn(
                name: "Title",
                schema: "catalog",
                table: "ProductReviews");
        }
    }
}
