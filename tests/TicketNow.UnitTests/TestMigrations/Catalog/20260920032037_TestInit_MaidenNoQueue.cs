using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketNow.UnitTests.TestMigrations.Catalog
{
    /// <inheritdoc />
    public partial class TestInit_MaidenNoQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "public",
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
                schema: "public",
                table: "onsales",
                keyColumn: "EventId",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "RequiresQueue",
                value: true);
        }
    }
}
