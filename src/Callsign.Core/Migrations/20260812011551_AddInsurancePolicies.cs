using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddInsurancePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InsurancePolicies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AircraftInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    HullValueCents = table.Column<long>(type: "INTEGER", nullable: false),
                    CoverageMilli = table.Column<int>(type: "INTEGER", nullable: false),
                    DeductibleCents = table.Column<long>(type: "INTEGER", nullable: false),
                    PremiumPerWeekCents = table.Column<long>(type: "INTEGER", nullable: false),
                    PremiumLastBilledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Active = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InsurancePolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InsurancePolicies_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicies_AircraftInstanceId",
                table: "InsurancePolicies",
                column: "AircraftInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_InsurancePolicies_CompanyId",
                table: "InsurancePolicies",
                column: "CompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InsurancePolicies");
        }
    }
}
