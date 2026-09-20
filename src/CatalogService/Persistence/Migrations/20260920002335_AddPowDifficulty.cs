using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketNow.CatalogService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPowDifficulty : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PowDifficulty",
                schema: "catalog",
                table: "onsales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                schema: "catalog",
                table: "onsales",
                keyColumn: "EventId",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                column: "PowDifficulty",
                value: 0);

            migrationBuilder.UpdateData(
                schema: "catalog",
                table: "onsales",
                keyColumn: "EventId",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                column: "PowDifficulty",
                value: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PowDifficulty",
                schema: "catalog",
                table: "onsales");
        }
    }
}
