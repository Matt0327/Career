using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AircraftTypes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Key = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    CanonicalName = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    Manufacturer = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    IcaoTypeDesignator = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    IcaoModel = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Category = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    UiTypeRole = table.Column<string>(type: "TEXT", maxLength: 60, nullable: true),
                    Seats = table.Column<int>(type: "INTEGER", nullable: true),
                    UsefulLoadLbs = table.Column<int>(type: "INTEGER", nullable: true),
                    FuelCapacityLbs = table.Column<int>(type: "INTEGER", nullable: true),
                    CruiseKtas = table.Column<int>(type: "INTEGER", nullable: true),
                    MinRunwayFt = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AircraftTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Airports",
                columns: table => new
                {
                    Ident = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    IcaoCode = table.Column<string>(type: "TEXT", maxLength: 12, nullable: true),
                    IataCode = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Latitude = table.Column<double>(type: "REAL", nullable: false),
                    Longitude = table.Column<double>(type: "REAL", nullable: false),
                    ElevationFt = table.Column<int>(type: "INTEGER", nullable: true),
                    IsoCountry = table.Column<string>(type: "TEXT", maxLength: 4, nullable: true),
                    IsoRegion = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Municipality = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ScheduledService = table.Column<bool>(type: "INTEGER", nullable: false),
                    LongestRunwayFt = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Airports", x => x.Ident);
                });

            migrationBuilder.CreateTable(
                name: "Companies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    CashCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Companies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Flights",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobAssignmentId = table.Column<Guid>(type: "TEXT", nullable: true),
                    FlownByPilotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AircraftTitle = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AircraftTypeId = table.Column<Guid>(type: "TEXT", nullable: true),
                    AircraftInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    DepartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ArrivedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TouchdownFpm = table.Column<double>(type: "REAL", nullable: false),
                    DistanceNm = table.Column<double>(type: "REAL", nullable: false),
                    FuelUsedLbs = table.Column<double>(type: "REAL", nullable: false),
                    PayoutCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Xp = table.Column<int>(type: "INTEGER", nullable: false),
                    PayoutBreakdownJson = table.Column<string>(type: "TEXT", nullable: false),
                    SettledAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Flights", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "JobAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    JobId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    PilotId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OriginIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    DestIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    Commodity = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    WeightLbs = table.Column<int>(type: "INTEGER", nullable: false),
                    Pax = table.Column<int>(type: "INTEGER", nullable: false),
                    DistanceNm = table.Column<double>(type: "REAL", nullable: false),
                    RewardQuoteCents = table.Column<long>(type: "INTEGER", nullable: false),
                    XpQuote = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AcceptedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    SettledAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OriginIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    DestIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    Commodity = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    WeightLbs = table.Column<int>(type: "INTEGER", nullable: false),
                    Pax = table.Column<int>(type: "INTEGER", nullable: false),
                    DistanceNm = table.Column<double>(type: "REAL", nullable: false),
                    RewardCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Xp = table.Column<int>(type: "INTEGER", nullable: false),
                    RequiredRank = table.Column<int>(type: "INTEGER", nullable: false),
                    SourceRouteId = table.Column<Guid>(type: "TEXT", nullable: true),
                    GeneratedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiresAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LoadByAt = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AircraftTitleAliases",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AircraftTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    TitleNormalized = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AircraftTitleAliases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AircraftTitleAliases_AircraftTypes_AircraftTypeId",
                        column: x => x.AircraftTypeId,
                        principalTable: "AircraftTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "InstalledPackages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AircraftTypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PackageFolder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AircraftFolder = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    IsOnDisk = table.Column<bool>(type: "INTEGER", nullable: false),
                    ScannedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    HostClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstalledPackages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InstalledPackages_AircraftTypes_AircraftTypeId",
                        column: x => x.AircraftTypeId,
                        principalTable: "AircraftTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Runways",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AirportIdent = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    LengthFt = table.Column<int>(type: "INTEGER", nullable: true),
                    WidthFt = table.Column<int>(type: "INTEGER", nullable: true),
                    Surface = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    Lighted = table.Column<bool>(type: "INTEGER", nullable: false),
                    Closed = table.Column<bool>(type: "INTEGER", nullable: false),
                    LeIdent = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    HeIdent = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runways", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runways_Airports_AirportIdent",
                        column: x => x.AirportIdent,
                        principalTable: "Airports",
                        principalColumn: "Ident",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AircraftInstances",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TypeId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Tail = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    Ownership = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Availability = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    LocationIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    HullConditionMilli = table.Column<int>(type: "INTEGER", nullable: false),
                    EngineConditionMilli = table.Column<int>(type: "INTEGER", nullable: false),
                    AirframeHours = table.Column<double>(type: "REAL", nullable: false),
                    MaintenanceHoursWatermark = table.Column<double>(type: "REAL", nullable: false),
                    PurchasePriceCents = table.Column<long>(type: "INTEGER", nullable: true),
                    AcquiredAt = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AircraftInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AircraftInstances_AircraftTypes_TypeId",
                        column: x => x.TypeId,
                        principalTable: "AircraftTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AircraftInstances_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Bases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AirportIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    IsHome = table.Column<bool>(type: "INTEGER", nullable: false),
                    RentPerDayCents = table.Column<long>(type: "INTEGER", nullable: false),
                    OpenedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRentBilledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Bases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Bases_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "LedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EntryUid = table.Column<Guid>(type: "TEXT", nullable: false),
                    Sequence = table.Column<long>(type: "INTEGER", nullable: true),
                    AccountId = table.Column<Guid>(type: "TEXT", nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    AmountCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AircraftInstanceId = table.Column<Guid>(type: "TEXT", nullable: true),
                    StaffId = table.Column<Guid>(type: "TEXT", nullable: true),
                    BaseId = table.Column<Guid>(type: "TEXT", nullable: true),
                    RelatedEntityType = table.Column<int>(type: "INTEGER", nullable: true),
                    RelatedEntityId = table.Column<string>(type: "TEXT", maxLength: 80, nullable: true),
                    DedupeKey = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LedgerEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LedgerEntries_Companies_AccountId",
                        column: x => x.AccountId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Pilots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    Xp = table.Column<int>(type: "INTEGER", nullable: false),
                    HomeIcao = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    CurrentIcao = table.Column<string>(type: "TEXT", maxLength: 8, nullable: false),
                    ReputationMilli = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Pilots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Pilots_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Staff",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 80, nullable: false),
                    Role = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    WagePerDayCents = table.Column<long>(type: "INTEGER", nullable: false),
                    SkillMilli = table.Column<int>(type: "INTEGER", nullable: false),
                    HiredAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastPaidAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Staff", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Staff_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StandingOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StaffId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AircraftInstanceId = table.Column<Guid>(type: "TEXT", nullable: false),
                    OriginIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    DestIcao = table.Column<string>(type: "TEXT", maxLength: 12, nullable: false),
                    DistanceNm = table.Column<double>(type: "REAL", nullable: false),
                    RoundTripHours = table.Column<double>(type: "REAL", nullable: false),
                    RewardPerTripCents = table.Column<long>(type: "INTEGER", nullable: false),
                    Commodity = table.Column<string>(type: "TEXT", maxLength: 60, nullable: false),
                    WeightLbs = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    StartedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    LastReconciledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StandingOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StandingOrders_AircraftInstances_AircraftInstanceId",
                        column: x => x.AircraftInstanceId,
                        principalTable: "AircraftInstances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StandingOrders_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StandingOrders_Staff_StaffId",
                        column: x => x.StaffId,
                        principalTable: "Staff",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AircraftInstances_CompanyId",
                table: "AircraftInstances",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_AircraftInstances_TypeId",
                table: "AircraftInstances",
                column: "TypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AircraftTitleAliases_AircraftTypeId",
                table: "AircraftTitleAliases",
                column: "AircraftTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_AircraftTitleAliases_TitleNormalized",
                table: "AircraftTitleAliases",
                column: "TitleNormalized");

            migrationBuilder.CreateIndex(
                name: "IX_AircraftTypes_Key",
                table: "AircraftTypes",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Airports_IcaoCode",
                table: "Airports",
                column: "IcaoCode");

            migrationBuilder.CreateIndex(
                name: "IX_Airports_IsoCountry",
                table: "Airports",
                column: "IsoCountry");

            migrationBuilder.CreateIndex(
                name: "IX_Airports_Latitude_Longitude",
                table: "Airports",
                columns: new[] { "Latitude", "Longitude" });

            migrationBuilder.CreateIndex(
                name: "IX_Bases_CompanyId_AirportIcao",
                table: "Bases",
                columns: new[] { "CompanyId", "AirportIcao" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Flights_AircraftInstanceId",
                table: "Flights",
                column: "AircraftInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_Flights_FlownByPilotId",
                table: "Flights",
                column: "FlownByPilotId");

            migrationBuilder.CreateIndex(
                name: "IX_Flights_JobAssignmentId",
                table: "Flights",
                column: "JobAssignmentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InstalledPackages_AircraftTypeId",
                table: "InstalledPackages",
                column: "AircraftTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_JobAssignments_AccountId",
                table: "JobAssignments",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_JobAssignments_JobId",
                table: "JobAssignments",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_Jobs_OriginIcao_ExpiresAt",
                table: "Jobs",
                columns: new[] { "OriginIcao", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AccountId_At",
                table: "LedgerEntries",
                columns: new[] { "AccountId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AccountId_DedupeKey",
                table: "LedgerEntries",
                columns: new[] { "AccountId", "DedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_AircraftInstanceId_At",
                table: "LedgerEntries",
                columns: new[] { "AircraftInstanceId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_BaseId_At",
                table: "LedgerEntries",
                columns: new[] { "BaseId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_EntryUid",
                table: "LedgerEntries",
                column: "EntryUid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_RelatedEntityType_RelatedEntityId",
                table: "LedgerEntries",
                columns: new[] { "RelatedEntityType", "RelatedEntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_LedgerEntries_StaffId_At",
                table: "LedgerEntries",
                columns: new[] { "StaffId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_Pilots_CompanyId",
                table: "Pilots",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_Runways_AirportIdent",
                table: "Runways",
                column: "AirportIdent");

            migrationBuilder.CreateIndex(
                name: "IX_Staff_CompanyId",
                table: "Staff",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_StandingOrders_AircraftInstanceId",
                table: "StandingOrders",
                column: "AircraftInstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_StandingOrders_CompanyId",
                table: "StandingOrders",
                column: "CompanyId");

            migrationBuilder.CreateIndex(
                name: "IX_StandingOrders_StaffId",
                table: "StandingOrders",
                column: "StaffId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AircraftTitleAliases");

            migrationBuilder.DropTable(
                name: "Bases");

            migrationBuilder.DropTable(
                name: "Flights");

            migrationBuilder.DropTable(
                name: "InstalledPackages");

            migrationBuilder.DropTable(
                name: "JobAssignments");

            migrationBuilder.DropTable(
                name: "Jobs");

            migrationBuilder.DropTable(
                name: "LedgerEntries");

            migrationBuilder.DropTable(
                name: "Pilots");

            migrationBuilder.DropTable(
                name: "Runways");

            migrationBuilder.DropTable(
                name: "StandingOrders");

            migrationBuilder.DropTable(
                name: "Airports");

            migrationBuilder.DropTable(
                name: "AircraftInstances");

            migrationBuilder.DropTable(
                name: "Staff");

            migrationBuilder.DropTable(
                name: "AircraftTypes");

            migrationBuilder.DropTable(
                name: "Companies");
        }
    }
}
