using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TicketNow.OrdersService.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSagaRowVersion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RowVersion",
                schema: "orders",
                table: "saga_instances");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "RowVersion",
                schema: "orders",
                table: "saga_instances",
                type: "bytea",
                rowVersion: true,
                nullable: false,
                defaultValue: new byte[0]);
        }
    }
}
