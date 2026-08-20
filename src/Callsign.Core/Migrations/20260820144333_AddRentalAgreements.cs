using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddRentalAgreements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RentalAgreements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AircraftInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    HoursAtPickup = table.Column<double>(type: "REAL", nullable: false),
                    HullMilliAtPickup = table.Column<int>(type: "INTEGER", nullable: false),
                    EngineMilliAtPickup = table.Column<int>(type: "INTEGER", nullable: false),
                    DepositCents = table.Column<long>(type: "INTEGER", nullable: false),
                    HoldingPerDayCents = table.Column<long>(type: "INTEGER", nullable: false),
                    FlightHourRateCents = table.Column<long>(type: "INTEGER", nullable: false),
                    WeeklyRateCents = table.Column<long>(type: "INTEGER", nullable: false),
                    InsuranceWeeklyCents = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRentBilledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    HoursLastBilled = table.Column<double>(type: "REAL", nullable: false),
                    RentCreditedCents = table.Column<long>(type: "INTEGER", nullable: false),
                    BuyoutCents = table.Column<long>(type: "INTEGER", nullable: true),
                    SloppyEventCount = table.Column<int>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RentalAgreements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RentalAgreements_AircraftInstances_AircraftInstanceId",
                        column: x => x.AircraftInstanceId,
                        principalTable: "AircraftInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RentalAgreements_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RentalAgreements_AircraftInstanceId",
                table: "RentalAgreements",
                column: "AircraftInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_RentalAgreements_CompanyId",
                table: "RentalAgreements",
                column: "CompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RentalAgreements");
        }
    }
}
