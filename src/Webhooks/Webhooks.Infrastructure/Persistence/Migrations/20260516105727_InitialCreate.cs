#pragma warning disable CA1861
﻿using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haworks.Webhooks.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "webhooks");

            migrationBuilder.CreateTable(
                name: "webhook_subscriptions",
                schema: "webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PartnerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "text", nullable: false),
                    Secret = table.Column<string>(type: "text", nullable: false),
                    SecretHash = table.Column<string>(type: "text", nullable: false),
                    SecretPreview = table.Column<string>(type: "text", nullable: false),
                    Events = table.Column<string[]>(type: "text[]", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedFromIp = table.Column<string>(type: "text", nullable: true),
                    ModifiedFromIp = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: true),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_subscriptions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "webhook_deliveries",
                schema: "webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<string>(type: "text", nullable: false),
                    EventType = table.Column<string>(type: "text", nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    NextAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    FinalStatus = table.Column<int>(type: "integer", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    CreatedFromIp = table.Column<string>(type: "text", nullable: true),
                    ModifiedFromIp = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: true),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_webhook_deliveries_webhook_subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "webhooks",
                        principalTable: "webhook_subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "webhook_delivery_attempts",
                schema: "webhooks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeliveryId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptIndex = table.Column<int>(type: "integer", nullable: false),
                    StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DurationMs = table.Column<int>(type: "integer", nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    ResponseBody = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedFromIp = table.Column<string>(type: "text", nullable: true),
                    ModifiedFromIp = table.Column<string>(type: "text", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "bytea", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastModifiedBy = table.Column<string>(type: "text", nullable: true),
                    LastModifiedDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_webhook_delivery_attempts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_webhook_delivery_attempts_webhook_deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalSchema: "webhooks",
                        principalTable: "webhook_deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_EventId",
                schema: "webhooks",
                table: "webhook_deliveries",
                column: "EventId");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_Status_NextAttemptAt",
                schema: "webhooks",
                table: "webhook_deliveries",
                columns: new[] { "Status", "NextAttemptAt" },
                filter: "\"Status\" IN ('Pending', 'Failed')");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_SubscriptionId_CreatedAt",
                schema: "webhooks",
                table: "webhook_deliveries",
                columns: new[] { "SubscriptionId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_deliveries_SubscriptionId_EventId",
                schema: "webhooks",
                table: "webhook_deliveries",
                columns: new[] { "SubscriptionId", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_webhook_delivery_attempts_DeliveryId_AttemptIndex",
                schema: "webhooks",
                table: "webhook_delivery_attempts",
                columns: new[] { "DeliveryId", "AttemptIndex" });

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_Events",
                schema: "webhooks",
                table: "webhook_subscriptions",
                column: "Events")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_webhook_subscriptions_PartnerId",
                schema: "webhooks",
                table: "webhook_subscriptions",
                column: "PartnerId",
                filter: "\"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "webhook_delivery_attempts",
                schema: "webhooks");

            migrationBuilder.DropTable(
                name: "webhook_deliveries",
                schema: "webhooks");

            migrationBuilder.DropTable(
                name: "webhook_subscriptions",
                schema: "webhooks");
        }
    }
}
