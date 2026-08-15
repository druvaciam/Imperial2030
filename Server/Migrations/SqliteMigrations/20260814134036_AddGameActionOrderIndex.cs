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
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_GameActions_GameId;");

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
            migrationBuilder.Sql("DROP INDEX IF EXISTS IX_GameActions_GameId_OrderIndex;");

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
