using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddFlightScoring : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ApproachScore",
                table: "Flights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "LandingScore",
                table: "Flights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OverallScore",
                table: "Flights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "StabilizedApproach",
                table: "Flights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TouchdownFpmWorst3",
                table: "Flights",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "TouchdownG",
                table: "Flights",
                type: "REAL",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ViolationPoints",
                table: "Flights",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApproachScore",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "LandingScore",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "OverallScore",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "StabilizedApproach",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "TouchdownFpmWorst3",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "TouchdownG",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "ViolationPoints",
                table: "Flights");
        }
    }
}
