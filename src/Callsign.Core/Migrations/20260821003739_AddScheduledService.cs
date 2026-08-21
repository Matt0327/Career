using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "LoadFactorMilli",
                table: "Routes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SeatCapacity",
                table: "Routes",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SeatYieldCents",
                table: "Routes",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LoadFactorMilli",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "SeatCapacity",
                table: "Routes");

            migrationBuilder.DropColumn(
                name: "SeatYieldCents",
                table: "Routes");
        }
    }
}
