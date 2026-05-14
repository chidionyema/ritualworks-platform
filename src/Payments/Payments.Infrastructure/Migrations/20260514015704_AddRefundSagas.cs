using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haworks.Payments.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRefundSagas : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RefundSagas",
                schema: "payments",
                columns: table => new
                {
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: false),
                    CurrentState = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    RefundId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    Provider = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ProviderRefundId = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FailureDetail = table.Column<string>(type: "text", nullable: true),
                    FailureCategory = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    RefundTimeoutTokenId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundSagas", x => x.CorrelationId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RefundSagas_OrderId",
                schema: "payments",
                table: "RefundSagas",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundSagas_PaymentId",
                schema: "payments",
                table: "RefundSagas",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_RefundSagas_ProviderRefundId",
                schema: "payments",
                table: "RefundSagas",
                column: "ProviderRefundId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefundSagas",
                schema: "payments");
        }
    }
}
