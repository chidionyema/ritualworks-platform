using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haworks.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockReservationXminConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<uint>(
                name: "xmin",
                schema: "catalog",
                table: "StockReservations",
                type: "xid",
                rowVersion: true,
                nullable: false,
                defaultValue: 0u);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "xmin",
                schema: "catalog",
                table: "StockReservations");
        }
    }
}
