using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Imperial2030.Server.Migrations
{
    /// <inheritdoc />
    public partial class AddSwissBankState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PendingSwissBankForceNation",
                table: "Games",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PendingSwissBankForceTargetSlot",
                table: "Games",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PendingSwissBankResponders",
                table: "Games",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "[]");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingSwissBankForceNation",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "PendingSwissBankForceTargetSlot",
                table: "Games");

            migrationBuilder.DropColumn(
                name: "PendingSwissBankResponders",
                table: "Games");
        }
    }
}
