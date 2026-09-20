using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketNow.UnitTests.TestMigrations.Catalog
{
    /// <inheritdoc />
    public partial class TestInit_Bands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "public",
                table: "events",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                columns: new[] { "Artist", "Title" },
                values: new object[] { "Metallica", "Metallica" });

            migrationBuilder.UpdateData(
                schema: "public",
                table: "events",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                columns: new[] { "Artist", "Title" },
                values: new object[] { "Iron Maiden", "Iron Maiden" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.UpdateData(
                schema: "public",
                table: "events",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000001"),
                columns: new[] { "Artist", "Title" },
                values: new object[] { "Las Luciérnagas", "Noche de Neón" });

            migrationBuilder.UpdateData(
                schema: "public",
                table: "events",
                keyColumn: "Id",
                keyValue: new Guid("20000000-0000-0000-0000-000000000002"),
                columns: new[] { "Artist", "Title" },
                values: new object[] { "Fuego Lento", "Ritmos del Sur" });
        }
    }
}
