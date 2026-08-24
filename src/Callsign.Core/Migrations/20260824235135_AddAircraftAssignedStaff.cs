using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAircraftAssignedStaff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssignedStaffId",
                table: "AircraftInstances",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedStaffId",
                table: "AircraftInstances");
        }
    }
}
