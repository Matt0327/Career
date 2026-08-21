using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddDispatchLegs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DispatchLegs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StaffId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AircraftInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    OriginIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    DestIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    Commodity = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    WeightLbs = table.Column<int>(type: "INTEGER", nullable: false),
                    Pax = table.Column<int>(type: "INTEGER", nullable: false),
                    DistanceNm = table.Column<double>(type: "REAL", nullable: false),
                    OneWayHours = table.Column<double>(type: "REAL", nullable: false),
                    RewardCents = table.Column<long>(type: "INTEGER", nullable: false),
                    ClientKey = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    ClientName = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    DispatchedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ReadyAt = table.Column<long>(type: "INTEGER", nullable: false),
                    FlownAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DispatchLegs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DispatchLegs_AircraftInstances_AircraftInstanceId",
                        column: x => x.AircraftInstanceId,
                        principalTable: "AircraftInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DispatchLegs_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DispatchLegs_Staff_StaffId",
                        column: x => x.StaffId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DispatchLegs_AircraftInstanceId",
                table: "DispatchLegs",
                column: "AircraftInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchLegs_CompanyId",
                table: "DispatchLegs",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_DispatchLegs_StaffId",
                table: "DispatchLegs",
                column: "StaffId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DispatchLegs");
        }
    }
}
