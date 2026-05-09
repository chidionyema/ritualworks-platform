using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haworks.Catalog.Infrastructure.Migrations
{
    /// <summary>
    /// B1 — converts the post-order <c>OrderStockReservations</c> tracker
    /// into the ADR-004 <c>StockReservations</c> aggregate
    /// (Pending → Confirmed | Expired). Renames the table in place,
    /// widens <c>OrderId</c> to nullable, adds lifecycle columns, and
    /// backfills any existing rows as already-Confirmed so they're not
    /// swept by the upcoming B3 sweeper.
    /// </summary>
    public partial class AddStockReservationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Drop the old unique index on OrderId — the new model lets
            // multiple Pending reservations exist for the same (eventually
            // null) OrderId and only Confirmed rows have one assigned.
            migrationBuilder.DropIndex(
                name: "IX_OrderStockReservations_OrderId",
                schema: "catalog",
                table: "OrderStockReservations");

            // 1. Rename table OrderStockReservations -> StockReservations.
            //    The PK rename keeps EF model-snapshot diff clean on next
            //    migration scaffolding.
            migrationBuilder.RenameTable(
                name: "OrderStockReservations",
                schema: "catalog",
                newName: "StockReservations",
                newSchema: "catalog");

            migrationBuilder.Sql(
                "ALTER TABLE catalog.\"StockReservations\" " +
                "RENAME CONSTRAINT \"PK_OrderStockReservations\" TO \"PK_StockReservations\";");

            // 2. Widen OrderId: was non-null, now nullable (Pending rows
            //    have no owning order yet).
            migrationBuilder.AlterColumn<Guid>(
                name: "OrderId",
                schema: "catalog",
                table: "StockReservations",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            // 3. New lifecycle columns. UserId/Status/ExpiresAt are
            //    declared NOT NULL in the model — add them with defaults so
            //    any pre-existing rows pass the constraint, then backfill,
            //    then drop the temporary defaults.
            migrationBuilder.AddColumn<string>(
                name: "UserId",
                schema: "catalog",
                table: "StockReservations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "catalog",
                table: "StockReservations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                schema: "catalog",
                table: "StockReservations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "now()");

            migrationBuilder.AddColumn<DateTime>(
                name: "ConfirmedAt",
                schema: "catalog",
                table: "StockReservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiredAt",
                schema: "catalog",
                table: "StockReservations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SagaId",
                schema: "catalog",
                table: "StockReservations",
                type: "uuid",
                nullable: true);

            // 4. Backfill: existing rows came from the legacy saga path,
            //    so they're effectively Confirmed. ExpiresAt is set far
            //    in the future so the B3 sweeper never picks them up.
            migrationBuilder.Sql(@"
                UPDATE catalog.""StockReservations""
                SET ""Status""      = 1,
                    ""ConfirmedAt"" = COALESCE(""ConfirmedAt"", ""ReservedAt""),
                    ""ExpiresAt""   = ""ReservedAt"" + INTERVAL '1 year';
            ");

            // 5. Drop the temporary defaults — application code now
            //    supplies these fields explicitly via Create/CreateConfirmed.
            migrationBuilder.AlterColumn<string>(
                name: "UserId",
                schema: "catalog",
                table: "StockReservations",
                type: "character varying(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(450)",
                oldMaxLength: 450,
                oldDefaultValue: "");

            migrationBuilder.AlterColumn<int>(
                name: "Status",
                schema: "catalog",
                table: "StockReservations",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldDefaultValue: 0);

            migrationBuilder.AlterColumn<DateTime>(
                name: "ExpiresAt",
                schema: "catalog",
                table: "StockReservations",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone",
                oldDefaultValueSql: "now()");

            // 6. Sweeper hot path index: WHERE Status=Pending AND ExpiresAt <= now().
            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_Status_ExpiresAt",
                schema: "catalog",
                table: "StockReservations",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StockReservations_Status_ExpiresAt",
                schema: "catalog",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "SagaId",
                schema: "catalog",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "ExpiredAt",
                schema: "catalog",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "ConfirmedAt",
                schema: "catalog",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                schema: "catalog",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "catalog",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "UserId",
                schema: "catalog",
                table: "StockReservations");

            // OrderId back to non-null. Existing nullable rows would fail
            // this — but the legacy path only ever wrote non-null rows, so
            // a roll-back is safe in the steady-state.
            migrationBuilder.AlterColumn<Guid>(
                name: "OrderId",
                schema: "catalog",
                table: "StockReservations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.Sql(
                "ALTER TABLE catalog.\"StockReservations\" " +
                "RENAME CONSTRAINT \"PK_StockReservations\" TO \"PK_OrderStockReservations\";");

            migrationBuilder.RenameTable(
                name: "StockReservations",
                schema: "catalog",
                newName: "OrderStockReservations",
                newSchema: "catalog");

            migrationBuilder.CreateIndex(
                name: "IX_OrderStockReservations_OrderId",
                schema: "catalog",
                table: "OrderStockReservations",
                column: "OrderId",
                unique: true);
        }
    }
}
