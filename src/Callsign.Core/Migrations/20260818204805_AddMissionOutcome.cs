using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddMissionOutcome : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OutcomeGrade",
                table: "Flights",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeReason",
                table: "Flights",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OutcomeGrade",
                table: "Flights");

            migrationBuilder.DropColumn(
                name: "OutcomeReason",
                table: "Flights");
        }
    }
}
