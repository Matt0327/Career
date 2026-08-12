using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddLoans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Loans",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    CompanyId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Tier = table.Column<int>(type: "INTEGER", nullable: false),
                    PrincipalCents = table.Column<long>(type: "INTEGER", nullable: false),
                    AprBps = table.Column<int>(type: "INTEGER", nullable: false),
                    TermDays = table.Column<int>(type: "INTEGER", nullable: false),
                    OutstandingCents = table.Column<long>(type: "INTEGER", nullable: false),
                    TakenAt = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentLastBilledAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    OriginClientId = table.Column<Guid>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Loans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Loans_Companies_CompanyId",
                        column: x => x.CompanyId,
                        principalTable: "Companies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Loans_CompanyId",
                table: "Loans",
                column: "CompanyId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Loans");
        }
    }
}
