using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddCrewFatigue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FatigueMilli",
                table: "Staff",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FatigueMilli",
                table: "Staff");
        }
    }
}
