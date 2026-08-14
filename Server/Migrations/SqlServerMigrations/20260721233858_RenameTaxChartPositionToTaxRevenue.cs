using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Imperial2030.Server.Migrations
{
    /// <inheritdoc />
    public partial class RenameTaxChartPositionToTaxRevenue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TaxChartPosition",
                table: "NationStates",
                newName: "TaxRevenue");

            migrationBuilder.RenameColumn(
                name: "PreviousTaxChartPosition",
                table: "NationStates",
                newName: "PreviousTaxRevenue");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TaxRevenue",
                table: "NationStates",
                newName: "TaxChartPosition");

            migrationBuilder.RenameColumn(
                name: "PreviousTaxRevenue",
                table: "NationStates",
                newName: "PreviousTaxChartPosition");
        }
    }
}
