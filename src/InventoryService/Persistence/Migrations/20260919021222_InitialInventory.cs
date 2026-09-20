using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketNow.InventoryService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "holds",
                schema: "inventory",
                columns: table => new
                {
                    HoldId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Qty = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holds", x => x.HoldId);
                });

            migrationBuilder.CreateTable(
                name: "ledger",
                schema: "inventory",
                columns: table => new
                {
                    EntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: false),
                    EntryType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Delta = table.Column<int>(type: "integer", nullable: false),
                    HoldId = table.Column<Guid>(type: "uuid", nullable: true),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Actor = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ledger", x => x.EntryId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_holds_EventId_ExpiresAt",
                schema: "inventory",
                table: "holds",
                columns: new[] { "EventId", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_EventId_ZoneId_CreatedAt",
                schema: "inventory",
                table: "ledger",
                columns: new[] { "EventId", "ZoneId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ledger_HoldId",
                schema: "inventory",
                table: "ledger",
                column: "HoldId",
                unique: true,
                filter: "\"EntryType\" = 'Confirm'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "holds",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "ledger",
                schema: "inventory");
        }
    }
}
