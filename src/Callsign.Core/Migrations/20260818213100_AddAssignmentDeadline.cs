using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAssignmentDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "DeadlineAt",
                table: "JobAssignments",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeadlineAt",
                table: "JobAssignments");
        }
    }
}
