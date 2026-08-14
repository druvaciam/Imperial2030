using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Imperial2030.Server.Migrations.SqlServerMigrations
{
    /// <inheritdoc />
    public partial class AddGameActionOrderIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_GameActions_GameId' AND object_id = OBJECT_ID('GameActions')) DROP INDEX IX_GameActions_GameId ON GameActions;");

            migrationBuilder.AddColumn<long>(
                name: "OrderIndex",
                table: "GameActions",
                type: "bigint",
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
            migrationBuilder.Sql("IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_GameActions_GameId_OrderIndex' AND object_id = OBJECT_ID('GameActions')) DROP INDEX IX_GameActions_GameId_OrderIndex ON GameActions;");

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
