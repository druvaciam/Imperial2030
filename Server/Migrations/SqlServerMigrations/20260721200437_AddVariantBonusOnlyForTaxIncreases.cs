using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Imperial2030.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantBonusOnlyForTaxIncreases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "VariantBonusOnlyForTaxIncreases",
                table: "Games",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VariantBonusOnlyForTaxIncreases",
                table: "Games");
        }
    }
}
