using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Imperial2030.Server.Migrations.SqliteMigrations
{
    /// <inheritdoc />
    public partial class AddGameActionOrderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GameActions_GameId",
                table: "GameActions");

            migrationBuilder.AddColumn<long>(
                name: "OrderIndex",
                table: "GameActions",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_GameActions_GameId_OrderIndex",
                table: "GameActions",
                columns: new[] { "GameId", "OrderIndex" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GameActions_GameId_OrderIndex",
                table: "GameActions");

            migrationBuilder.DropColumn(
                name: "OrderIndex",
                table: "GameActions");

            migrationBuilder.CreateIndex(
                name: "IX_GameActions_GameId",
                table: "GameActions",
                column: "GameId");
        }
    }
}
