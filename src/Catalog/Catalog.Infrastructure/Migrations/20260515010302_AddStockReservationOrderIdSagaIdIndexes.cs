using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haworks.Catalog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStockReservationOrderIdSagaIdIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_OrderId",
                schema: "catalog",
                table: "StockReservations",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_SagaId",
                schema: "catalog",
                table: "StockReservations",
                column: "SagaId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockReservations_OrderId",
                schema: "catalog",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_SagaId",
                schema: "catalog",
                table: "StockReservations");
        }
    }
}
