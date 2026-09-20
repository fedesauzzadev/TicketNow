using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace TicketNow.UnitTests.TestMigrations.Catalog
{
    /// <inheritdoc />
    public partial class TestInit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "public");

            migrationBuilder.CreateTable(
                name: "InboxState",
                schema: "public",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ConsumerId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Received = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReceiveCount = table.Column<int>(type: "integer", nullable: false),
                    ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Consumed = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InboxState", x => x.Id);
                    table.UniqueConstraint("AK_InboxState_MessageId_ConsumerId", x => new { x.MessageId, x.ConsumerId });
                });

            migrationBuilder.CreateTable(
                name: "OutboxMessage",
                schema: "public",
                columns: table => new
                {
                    SequenceNumber = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EnqueueTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SentTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Headers = table.Column<string>(type: "text", nullable: true),
                    Properties = table.Column<string>(type: "text", nullable: true),
                    InboxMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    InboxConsumerId = table.Column<Guid>(type: "uuid", nullable: true),
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: true),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    MessageType = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    ConversationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CorrelationId = table.Column<Guid>(type: "uuid", nullable: true),
                    InitiatorId = table.Column<Guid>(type: "uuid", nullable: true),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    SourceAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DestinationAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ResponseAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    FaultAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ExpirationTime = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxMessage", x => x.SequenceNumber);
                });

            migrationBuilder.CreateTable(
                name: "OutboxState",
                schema: "public",
                columns: table => new
                {
                    OutboxId = table.Column<Guid>(type: "uuid", nullable: false),
                    LockId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "bytea", rowVersion: true, nullable: true),
                    Created = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Delivered = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSequenceNumber = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OutboxState", x => x.OutboxId);
                });

            migrationBuilder.CreateTable(
                name: "venues",
                schema: "public",
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
                schema: "public",
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
                        principalSchema: "public",
                        principalTable: "venues",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "onsales",
                schema: "public",
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
                        principalSchema: "public",
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "zones",
                schema: "public",
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
                        principalSchema: "public",
                        principalTable: "events",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                schema: "public",
                table: "venues",
                columns: new[] { "Id", "CapacityTotal", "City", "Name" },
                values: new object[,]
                {
                    { new Guid("10000000-0000-0000-0000-000000000001"), 60000, "Buenos Aires", "Estadio Central" },
                    { new Guid("10000000-0000-0000-0000-000000000002"), 15000, "Buenos Aires", "Arena Norte" },
                    { new Guid("10000000-0000-0000-0000-000000000003"), 3000, "Córdoba", "Teatro del Sol" }
                });

            migrationBuilder.InsertData(
                schema: "public",
                table: "events",
                columns: new[] { "Id", "Artist", "ImageUrl", "StartsAt", "Status", "Title", "VenueId" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000001"), "Las Luciérnagas", null, new DateTimeOffset(new DateTime(2027, 3, 13, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Announced", "Noche de Neón", new Guid("10000000-0000-0000-0000-000000000001") },
                    { new Guid("20000000-0000-0000-0000-000000000002"), "Fuego Lento", null, new DateTimeOffset(new DateTime(2027, 1, 22, 20, 30, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Announced", "Ritmos del Sur", new Guid("10000000-0000-0000-0000-000000000002") },
                    { new Guid("20000000-0000-0000-0000-000000000003"), "María Sola", null, new DateTimeOffset(new DateTime(2026, 11, 5, 21, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Draft", "Íntimos: María Sola", new Guid("10000000-0000-0000-0000-000000000003") }
                });

            migrationBuilder.InsertData(
                schema: "public",
                table: "onsales",
                columns: new[] { "EventId", "AdmissionRatePerSec", "ClosesAt", "MaxConcurrentInside", "MaxPerAccount", "OpensAt", "RequiresQueue" },
                values: new object[,]
                {
                    { new Guid("20000000-0000-0000-0000-000000000001"), 50, null, 2000, 4, new DateTimeOffset(new DateTime(2026, 9, 1, 12, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true },
                    { new Guid("20000000-0000-0000-0000-000000000002"), 40, null, 1500, 4, new DateTimeOffset(new DateTime(2026, 12, 1, 12, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true }
                });

            migrationBuilder.InsertData(
                schema: "public",
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
                schema: "public",
                table: "events",
                column: "StartsAt");

            migrationBuilder.CreateIndex(
                name: "IX_events_Status",
                schema: "public",
                table: "events",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_events_VenueId",
                schema: "public",
                table: "events",
                column: "VenueId");

            migrationBuilder.CreateIndex(
                name: "IX_InboxState_Delivered",
                schema: "public",
                table: "InboxState",
                column: "Delivered");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_EnqueueTime",
                schema: "public",
                table: "OutboxMessage",
                column: "EnqueueTime");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_ExpirationTime",
                schema: "public",
                table: "OutboxMessage",
                column: "ExpirationTime");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_InboxMessageId_InboxConsumerId_SequenceNumber",
                schema: "public",
                table: "OutboxMessage",
                columns: new[] { "InboxMessageId", "InboxConsumerId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessage_OutboxId_SequenceNumber",
                schema: "public",
                table: "OutboxMessage",
                columns: new[] { "OutboxId", "SequenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OutboxState_Created",
                schema: "public",
                table: "OutboxState",
                column: "Created");

            migrationBuilder.CreateIndex(
                name: "IX_zones_EventId",
                schema: "public",
                table: "zones",
                column: "EventId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InboxState",
                schema: "public");

            migrationBuilder.DropTable(
                name: "onsales",
                schema: "public");

            migrationBuilder.DropTable(
                name: "OutboxMessage",
                schema: "public");

            migrationBuilder.DropTable(
                name: "OutboxState",
                schema: "public");

            migrationBuilder.DropTable(
                name: "zones",
                schema: "public");

            migrationBuilder.DropTable(
                name: "events",
                schema: "public");

            migrationBuilder.DropTable(
                name: "venues",
                schema: "public");
        }
    }
}
