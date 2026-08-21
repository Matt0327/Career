using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Callsign.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddStaffLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrentIcao",
                table: "Staff",
                type: "TEXT",
                maxLength: 12,
                nullable: true);

            // Phase 12 — place every existing hired pilot somewhere real so the co-location gate has a truth to
            // check. A crew mid-line is positioned at that line's ORIGIN (where the aircraft sits and where the
            // co-location invariant says they must be) — a standing order first, then a route — and only an idle
            // crew falls back to the company home base. A crew whose company has no pilot row stays NULL, which
            // the app grandfathers in as co-located-with-anything.
            migrationBuilder.Sql(
                "UPDATE Staff SET CurrentIcao = COALESCE(" +
                "(SELECT o.OriginIcao FROM StandingOrders o WHERE o.StaffId = Staff.Id AND o.IsActive = 1 AND o.IsDeleted = 0 LIMIT 1), " +
                "(SELECT r.OriginIcao FROM Routes r WHERE r.StaffId = Staff.Id AND r.Active = 1 AND r.IsDeleted = 0 LIMIT 1), " +
                "(SELECT p.HomeIcao FROM Pilots p WHERE p.CompanyId = Staff.CompanyId LIMIT 1)) " +
                "WHERE CurrentIcao IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrentIcao",
                table: "Staff");
        }
    }
}
