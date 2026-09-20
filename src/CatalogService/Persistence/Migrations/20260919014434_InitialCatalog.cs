using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TicketNow.CatalogService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "catalog");

            migrationBuilder.CreateTable(
                name: "venues",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    City = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CapacityTotal = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_venues", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "events",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Artist = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ImageUrl = table.Column<string>(type: "text", nullable: true),
                    VenueId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_events_venues_VenueId",
                        column: x => x.VenueId,
                        principalSchema: "catalog",
                        principalTable: "venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "onsales",
                schema: "catalog",
                columns: table => new
                {
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpensAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosesAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MaxConcurrentInside = table.Column<int>(type: "integer", nullable: false),
                    AdmissionRatePerSec = table.Column<int>(type: "integer", nullable: false),
                    MaxPerAccount = table.Column<int>(type: "integer", nullable: false),
                    RequiresQueue = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_onsales", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_onsales_events_EventId",
                        column: x => x.EventId,
                        principalSchema: "catalog",
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "zones",
                schema: "catalog",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    Price = table.Column<decimal>(type: "numeric(10,2)", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_zones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_zones_events_EventId",
                        column: x => x.EventId,
                        principalSchema: "catalog",
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "venues",
                columns: new[] { "Id", "CapacityTotal", "City", "Name" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), 60000, "Buenos Aires", "Estadio Central" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), 15000, "Buenos Aires", "Arena Norte" },
                    { new Guid("10000000-0000-0000-0000-000000000003"), 3000, "Córdoba", "Teatro del Sol" }
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "events",
                columns: new[] { "Id", "Artist", "ImageUrl", "StartsAt", "Status", "Title", "VenueId" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000001"), "Las Luciérnagas", null, new DateTimeOffset(new DateTime(2027, 3, 13, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Announced", "Noche de Neón", new Guid("10000000-0000-0000-0000-000000000001") },
                    { new Guid("20000000-0000-0000-0000-000000000002"), "Fuego Lento", null, new DateTimeOffset(new DateTime(2027, 1, 22, 20, 30, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Announced", "Ritmos del Sur", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("20000000-0000-0000-0000-000000000003"), "María Sola", null, new DateTimeOffset(new DateTime(2026, 11, 5, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Draft", "Íntimos: María Sola", new Guid("10000000-0000-0000-0000-000000000003") }
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "onsales",
                columns: new[] { "EventId", "AdmissionRatePerSec", "ClosesAt", "MaxConcurrentInside", "MaxPerAccount", "OpensAt", "RequiresQueue" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000001"), 50, null, 2000, 4, new DateTimeOffset(new DateTime(2026, 9, 1, 12, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true },
                    { new Guid("20000000-0000-0000-0000-000000000002"), 40, null, 1500, 4, new DateTimeOffset(new DateTime(2026, 12, 1, 12, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true }
                });

            migrationBuilder.InsertData(
                schema: "catalog",
                table: "zones",
                columns: new[] { "Id", "Capacity", "EventId", "Name", "Price", "SortOrder" },
                values: new object[,]
                {
                    { new Guid("30000000-0000-0000-0000-000000000001"), 30000, new Guid("20000000-0000-0000-0000-000000000001"), "Campo", 85m, 1 },
                    { new Guid("30000000-0000-0000-0000-000000000002"), 10000, new Guid("20000000-0000-0000-0000-000000000001"), "Platea Baja", 110m, 2 },
                    { new Guid("30000000-0000-0000-0000-000000000003"), 15000, new Guid("20000000-0000-0000-0000-000000000001"), "Platea Alta", 60m, 3 },
                    { new Guid("30000000-0000-0000-0000-000000000004"), 5000, new Guid("20000000-0000-0000-0000-000000000001"), "VIP", 220m, 4 },
                    { new Guid("30000000-0000-0000-0000-000000000011"), 8000, new Guid("20000000-0000-0000-0000-000000000002"), "General", 45m, 1 },
                    { new Guid("30000000-0000-0000-0000-000000000012"), 4000, new Guid("20000000-0000-0000-0000-000000000002"), "Palco", 90m, 2 },
                    { new Guid("30000000-0000-0000-0000-000000000013"), 3000, new Guid("20000000-0000-0000-0000-000000000002"), "VIP", 150m, 3 },
                    { new Guid("30000000-0000-0000-0000-000000000021"), 3000, new Guid("20000000-0000-0000-0000-000000000003"), "Sala", 30m, 1 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_events_StartsAt",
                schema: "catalog",
                table: "events",
                column: "StartsAt");

            migrationBuilder.CreateIndex(
                name: "IX_events_Status",
                schema: "catalog",
                table: "events",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_events_VenueId",
                schema: "catalog",
                table: "events",
                column: "VenueId");

            migrationBuilder.CreateIndex(
                name: "IX_zones_EventId",
                schema: "catalog",
                table: "zones",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "onsales",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "zones",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "events",
                schema: "catalog");

            migrationBuilder.DropTable(
                name: "venues",
                schema: "catalog");
        }
    }
}
