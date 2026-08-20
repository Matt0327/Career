using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatingReputation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "OperatingReputationMilli",
                table: "Companies",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AirlineReputationEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DeltaMilli = table.Column<int>(type: "INTEGER", nullable: false),
                    BalanceMilli = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AirlineReputationEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AirlineReputationEvents_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AirlineReputationEvents_CompanyId_At",
                table: "AirlineReputationEvents",
                columns: new[] { "CompanyId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AirlineReputationEvents");

            migrationBuilder.DropColumn(
                name: "OperatingReputationMilli",
                table: "Companies");
        }
    }
}
