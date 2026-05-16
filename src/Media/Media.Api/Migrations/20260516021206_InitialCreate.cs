using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Haworks.Media.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "media");

            migrationBuilder.CreateTable(
                name: "MediaFiles",
                schema: "media",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MediaFiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_Hash_OwnerId",
                schema: "media",
                table: "MediaFiles",
                columns: new[] { "Hash", "OwnerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MediaFiles_OwnerId",
                schema: "media",
                table: "MediaFiles",
                column: "OwnerId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "MediaFiles",
                schema: "media");
        }
    }
}
