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
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_GameActions_GameId' AND object_id = OBJECT_ID('GameActions'))
                    DROP INDEX IX_GameActions_GameId ON GameActions;

                IF NOT EXISTS (SELECT * FROM sys.columns WHERE name = 'OrderIndex' AND object_id = OBJECT_ID('GameActions'))
                    ALTER TABLE GameActions ADD OrderIndex bigint NOT NULL CONSTRAINT DF_GameActions_OrderIndex DEFAULT 0;

                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_GameActions_GameId_OrderIndex' AND object_id = OBJECT_ID('GameActions'))
                    CREATE INDEX IX_GameActions_GameId_OrderIndex ON GameActions (GameId, OrderIndex);
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                IF EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_GameActions_GameId_OrderIndex' AND object_id = OBJECT_ID('GameActions'))
                    DROP INDEX IX_GameActions_GameId_OrderIndex ON GameActions;

                IF EXISTS (SELECT * FROM sys.columns WHERE name = 'OrderIndex' AND object_id = OBJECT_ID('GameActions'))
                BEGIN
                    DECLARE @ConstraintName nvarchar(200);
                    SELECT @ConstraintName = d.name
                    FROM sys.default_constraints d
                    JOIN sys.columns c ON d.parent_column_id = c.column_id AND d.parent_object_id = c.object_id
                    WHERE d.parent_object_id = OBJECT_ID('GameActions') AND c.name = 'OrderIndex';
                    IF @ConstraintName IS NOT NULL
                        EXEC('ALTER TABLE GameActions DROP CONSTRAINT ' + @ConstraintName);
                    ALTER TABLE GameActions DROP COLUMN OrderIndex;
                END

                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE name = 'IX_GameActions_GameId' AND object_id = OBJECT_ID('GameActions'))
                    CREATE INDEX IX_GameActions_GameId ON GameActions (GameId);
            ");
        }
    }
}
