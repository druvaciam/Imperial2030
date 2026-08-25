using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Imperial2030.Server.Migrations.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class UnifyPendingInvestorIdsAsPrimitiveCollection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PendingInvestorIdsJson",
                table: "Games",
                newName: "PendingInvestorIds");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PendingInvestorIds",
                table: "Games",
                newName: "PendingInvestorIdsJson");
        }
    }
}
