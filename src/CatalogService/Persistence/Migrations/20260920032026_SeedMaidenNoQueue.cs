using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketNow.CatalogService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SeedMaidenNoQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "catalog",
                table: "onsales",
                keyColumn: "EventId",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "RequiresQueue",
                value: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "catalog",
                table: "onsales",
                keyColumn: "EventId",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "RequiresQueue",
                value: true);
        }
    }
}
