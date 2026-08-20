using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddClients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClientKey",
                table: "Jobs",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientName",
                table: "Jobs",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientKey",
                table: "JobAssignments",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClientName",
                table: "JobAssignments",
                type: "TEXT",
                maxLength: 80,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Clients",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ClientKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    HomeIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    LoyaltyMilli = table.Column<int>(type: "INTEGER", nullable: false),
                    JobsCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    JobsFailed = table.Column<int>(type: "INTEGER", nullable: false),
                    FirstSeenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastJobAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clients", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clients_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clients_CompanyId_ClientKey",
                table: "Clients",
                columns: new[] { "CompanyId", "ClientKey" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Clients");

            migrationBuilder.DropColumn(
                name: "ClientKey",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ClientName",
                table: "Jobs");

            migrationBuilder.DropColumn(
                name: "ClientKey",
                table: "JobAssignments");

            migrationBuilder.DropColumn(
                name: "ClientName",
                table: "JobAssignments");
        }
    }
}
